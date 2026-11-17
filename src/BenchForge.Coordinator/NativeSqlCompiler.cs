using System.Globalization;
using System.Text.Json;
using BenchForge.Contracts;

namespace BenchForge.Coordinator;

public sealed class NativeSqlCompiler : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "benchforge-native-sql-" + Guid.NewGuid().ToString("N"));
    public async Task<NativeSqlProgram> CompileAsync(RunCase run, CancellationToken token)
    {
        var workload = run.Config.CustomWorkload ?? throw new ArgumentException("SQL workload required");
        var config = run.Config with { WorkloadDirectory = run.InputDirectory ?? run.Config.WorkloadDirectory };
        await WorkloadFiles.CheckAsync(config, token);
        var datasets = new List<NativeSqlDataset>();
        foreach (var table in workload.Datasets)
        {
            string? input = null;
            if (table.Import is { } import)
            {
                Directory.CreateDirectory(_directory); input = Path.Combine(_directory, table.Name + ".jsonl");
                await using var file = new StreamWriter(input, false, new System.Text.UTF8Encoding(false));
                long rows = 0;
                foreach (var row in WorkloadData.ReadFile(WorkloadFiles.SourcePath(config, import.File), import.Format, token, import.Delimiter))
                {
                    token.ThrowIfCancellationRequested();
                    var values = table.Columns.Select(column =>
                    {
                        if (!row.TryGetValue(column.Name, out var value)) { if (!column.Nullable) throw new InvalidDataException("Missing input column " + column.Name); return null; }
                        var converted = WorkloadData.ConvertValue(value is string { Length: 0 } && column.Nullable ? null : value, column.Type);
                        if (converted is DBNull && !column.Nullable) throw new InvalidDataException("NULL in required column " + column.Name);
                        return PortableSql.Text(converted, column.Type, run.Target);
                    }).ToArray();
                    await file.WriteLineAsync(JsonSerializer.Serialize(values, Json.Options).AsMemory(), token); rows++;
                }
                if (rows != import.Rows) throw new InvalidDataException("Input row count changed: " + table.Name);
            }
            var types = table.Columns.ToDictionary(column => column.Name, column => column.Type, StringComparer.OrdinalIgnoreCase);
            var insert = "INSERT INTO " + SqlWorkloadText.Quote(table.Name, run.Target) + " (" + string.Join(',', table.Columns.Select(column => SqlWorkloadText.Quote(column.Name, run.Target))) + ") VALUES (" + string.Join(',', table.Columns.Select(column => "@" + column.Name)) + ")";
            datasets.Add(new(table with { Import = null }, table.RowCount(run.Rows), WorkloadData.StableSeed(run.Config.Seed, table.Name, 0).ToString(CultureInfo.InvariantCulture), PortableSql.Command(insert, types, run.Target, run.Engine), input));
        }
        var operations = workload.ActiveOperations.Select(operation =>
        {
            var types = operation.Parameters.ToDictionary(value => value.Name, value => value.Type, StringComparer.OrdinalIgnoreCase);
            foreach (var capture in operation.Steps.Where(step => step.CaptureAs is not null)) types[capture.CaptureAs!] = capture.CaptureType;
            return new NativeSqlOperation(operation with { Steps = operation.Steps.Select(step => step with { Command = new() }).ToList() }, operation.Steps.Select(step => PortableSql.Command(PortableSql.ReadSql(step.Command.For(run.Target), step.Result, run.Target, run.Engine), types, run.Target, run.Engine)).ToArray());
        }).ToArray();
        var schema = workload.Schema.For(run.Target);
        return new(workload.Fingerprint,
            workload.Datasets.AsEnumerable().Reverse().Select(table => "DROP TABLE IF EXISTS " + SqlWorkloadText.Quote(table.Name, run.Target)).ToArray(),
            SqlScripts.PortableStatements(string.IsNullOrWhiteSpace(schema) ? SqlWorkloadText.AutoSchema(workload, run.Target) : schema, run.Target).ToArray(),
            SqlScripts.PortableStatements(workload.AfterLoad.For(run.Target), run.Target).ToArray(), datasets.ToArray(), operations,
            workload.Checks.Select(check => new NativeSqlCheck(check with { Command = new() }, PortableSql.Command(PortableSql.ReadSql(check.Command.For(run.Target), SqlResultMode.Scalar, run.Target, run.Engine), new Dictionary<string, SqlValueType>(), run.Target, run.Engine))).ToArray(),
            WorkloadPhases.Units(workload).Select(unit => new NativeSqlUnit(unit.Phase, unit.Operation)).ToArray());
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
