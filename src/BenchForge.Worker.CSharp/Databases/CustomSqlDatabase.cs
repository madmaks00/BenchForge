using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using BenchForge.Contracts;

namespace BenchForge.Worker.CSharp.Databases;

public interface ICustomSqlSession : IClientSession { string LastOperation { get; } }

public sealed class CustomSqlDatabase(RunCase run) : IDatabase
{
    private readonly SqlWorkload _workload = run.Config.CustomWorkload!;
    private readonly string _connectionString = SqlConnections.ConnectionString(run);
    private DbConnection? _admin;
    private long _sequence;
    private int _clients;
    public string? SelectedOperation { get; set; }
    public bool WarmupOnly { get; set; }
    public Dictionary<string, long> TableRows { get; } = run.Config.CustomWorkload!.Datasets.ToDictionary(table => table.Name, table => table.RowCount(run.Rows), StringComparer.OrdinalIgnoreCase);
    public long Completed { get; set; }
    public Dictionary<string, long> CompletedByOperation { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, IReadOnlyList<object>> Samples { get; } = new(StringComparer.OrdinalIgnoreCase);

    public async Task InitializeAsync(CancellationToken token)
    {
        await SqlConnections.EnsureDatabaseAsync(run, token);
        _admin = SqlConnections.Create(run.Target, _connectionString);
        try { await _admin.OpenAsync(token); await SqlConnections.ConfigureAsync(_admin, run.Target, true, token, run.Config); }
        catch { await _admin.DisposeAsync(); _admin = null; throw; }
    }
    private async Task ExecAsync(string sql, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(sql)) return;
        foreach (var statement in SqlScripts.Statements(sql, run.Target))
        {
            await using var command = _admin!.CreateCommand(); SqlConnections.SetText(command, statement); command.CommandTimeout = 600;
            await command.ExecuteNonQueryAsync(token);
        }
    }
    public async Task ResetAsync(CancellationToken token)
    {
        // Every case is an owned fresh DB. Within the case, only declared tables are reset.
        if (run.Target == TargetKind.Sqlite) await ExecAsync("PRAGMA foreign_keys=OFF", token);
        try
        {
            foreach (var table in _workload.Datasets.AsEnumerable().Reverse()) await ExecAsync("DROP TABLE IF EXISTS " + SqlWorkloadText.Quote(table.Name, run.Target), token);
            var schema = _workload.Schema.For(run.Target);
            await ExecAsync(string.IsNullOrWhiteSpace(schema) ? SqlWorkloadText.AutoSchema(_workload, run.Target) : schema, token);
        }
        finally { if (run.Target == TargetKind.Sqlite) await ExecAsync("PRAGMA foreign_keys=ON", CancellationToken.None); }
        _sequence = 0; Completed = 0; CompletedByOperation.Clear();
        Samples.Clear();
    }

    public async Task<long> LoadAsync(Action<long, long>? progress, CancellationToken token)
    {
        long loaded = 0; var total = TableRows.Values.Sum();
        foreach (var table in _workload.Datasets)
        {
            var count = TableRows[table.Name];
            var reservoirs = _workload.ActiveOperations.SelectMany(operation => operation.Parameters).Where(value => value.Generator == ValueGenerator.Sample && value.Reference == table.Name)
                .DistinctBy(WorkloadData.SampleKey).Select(value => new SampleReservoir(value, run.Config.Seed)).ToArray();
            await using var command = _admin!.CreateCommand(); command.CommandTimeout = 600;
            SqlConnections.SetText(command, "INSERT INTO " + SqlWorkloadText.Quote(table.Name, run.Target) + " (" + string.Join(',', table.Columns.Select(column => SqlWorkloadText.Quote(column.Name, run.Target))) +
                ") VALUES (" + string.Join(',', table.Columns.Select(column => "@" + column.Name)) + ")");
            foreach (var column in table.Columns) { var parameter = AddParameter(command, column.Name, column.Type); if (column.Type == SqlValueType.Guid && run.Target is not (TargetKind.PostgreSql or TargetKind.SqlServer or TargetKind.DuckDb or TargetKind.Oracle or TargetKind.CockroachDb)) parameter.DbType = DbType.String;
                if (parameter is Oracle.ManagedDataAccess.Client.OracleParameter oracle && column.Type == SqlValueType.Text && !column.PrimaryKey && !column.Unique) oracle.OracleDbType = Oracle.ManagedDataAccess.Client.OracleDbType.Clob; }
            await command.PrepareAsync(token);
            IEnumerator<Dictionary<string, object?>>? imported = null;
            if (table.Import is { } import)
            {
                if (run.InputDirectory is null) throw new InvalidDataException("Не передана папка входных файлов сценария.");
                var path = Path.Combine(run.InputDirectory, import.File);
                await using (var stream = File.OpenRead(path))
                    if (!Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).Equals(import.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SHA-256 файла изменился: " + import.File);
                imported = WorkloadData.ReadFile(path, import.Format, token, import.Delimiter).GetEnumerator();
            }
            DbTransaction? transaction = null;
            var datasetSeed = WorkloadData.StableSeed(run.Config.Seed, table.Name, 0);
            try
            {
                for (long index = 1; index <= count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    if (transaction is null) { transaction = await _admin.BeginTransactionAsync(token); command.Transaction = transaction; }
                    if (imported is not null && !imported.MoveNext()) throw new InvalidDataException("В файле меньше строк, чем объявлено: " + table.Name);
                    var rng = new PortableRandom(datasetSeed ^ unchecked((ulong)index * 0xd6e8feb86659fd93UL));
                    for (var columnIndex = 0; columnIndex < table.Columns.Count; columnIndex++)
                    {
                        var column = table.Columns[columnIndex]; object value;
                        if (imported is null) value = WorkloadData.Generate(column, new(index, index, 0, index, run.Rows, TableRows), ref rng);
                        else if (!imported.Current.TryGetValue(column.Name, out var cell)) value = column.Nullable ? DBNull.Value : throw new InvalidDataException("В файле нет поля " + column.Name);
                        else value = WorkloadData.ConvertValue(cell is string { Length: 0 } && column.Nullable ? null : cell, column.Type);
                        if (value is DBNull && !column.Nullable) throw new InvalidDataException("NULL в обязательном поле " + column.Name);
                        SetValue(command.Parameters[columnIndex], value, column.Type, run.Target);
                        foreach (var reservoir in reservoirs) if (reservoir.Definition.SampleColumn == column.Name && value is not DBNull) reservoir.Add(value);
                    }
                    await command.ExecuteNonQueryAsync(token); loaded++;
                    if (index % run.Config.BatchSize == 0 || index == count)
                    { await transaction.CommitAsync(token); await transaction.DisposeAsync(); transaction = null; command.Transaction = null; progress?.Invoke(loaded, total); }
                }
                if (imported is not null && imported.MoveNext()) throw new InvalidDataException("В файле больше строк, чем объявлено: " + table.Name);
            }
            finally { if (transaction is not null) await transaction.DisposeAsync(); imported?.Dispose(); }
            await VerifyTableAsync(table, token);
            foreach (var reservoir in reservoirs) Samples.Add(WorkloadData.SampleKey(reservoir.Definition), reservoir.Values);
        }
        return loaded;
    }
    private async Task VerifyTableAsync(SqlDataset table, CancellationToken token)
    {
        await using var command = _admin!.CreateCommand(); command.CommandTimeout = 600;
        command.CommandText = "SELECT COUNT(*) FROM " + SqlWorkloadText.Quote(table.Name, run.Target);
        if (Convert.ToInt64(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) != TableRows[table.Name]) throw new InvalidDataException("Не совпало число строк: " + table.Name);
        if (!_workload.Datasets.SelectMany(other => other.Columns).Concat(_workload.Operations.SelectMany(operation => operation.Parameters)).Any(column => column.Generator == ValueGenerator.Reference && column.Reference == table.Name)) return;
        var key = table.Columns.SingleOrDefault(column => column.PrimaryKey && column.Type is SqlValueType.Int64 or SqlValueType.Int32) ?? throw new InvalidDataException("Для генератора ссылок нужен числовой первичный ключ: " + table.Name);
        command.CommandText = "SELECT MIN(" + SqlWorkloadText.Quote(key.Name, run.Target) + "), MAX(" + SqlWorkloadText.Quote(key.Name, run.Target) + ") FROM " + SqlWorkloadText.Quote(table.Name, run.Target);
        await using var reader = await command.ExecuteReaderAsync(token); await reader.ReadAsync(token);
        if (TableRows[table.Name] == 0 || Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture) != 1 || Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture) != TableRows[table.Name])
            throw new InvalidDataException("Генератор ссылок требует ключи 1..N без пропусков: " + table.Name);
    }
    public Task PrepareAsync(CancellationToken token) => ExecAsync(_workload.AfterLoad.For(run.Target), token);
    public async Task CheckAsync(bool before, CancellationToken token)
    {
        foreach (var check in _workload.Checks.Where(check => before ? check.Before : check.After))
        {
            await using var command = _admin!.CreateCommand(); SqlConnections.SetText(command, check.Command.For(run.Target)); command.CommandTimeout = _workload.CommandTimeoutSeconds;
            foreach (var name in SqlWorkloadText.Parameters(check.Command.For(run.Target)))
            { var parameter = AddParameter(command, name, SqlValueType.Int64); parameter.Value = Builtin(name, 0, 0, 0); }
            var value = await command.ExecuteScalarAsync(token);
            if (value is null or DBNull) throw new InvalidDataException("Проверка вернула NULL: " + check.Name);
            var actual = SqlScalars.Decimal(value);
            var expected = check.Expected == "baseRows" ? run.Rows : check.Expected.StartsWith("rows:", StringComparison.Ordinal) && TableRows.TryGetValue(check.Expected[5..], out var rows) ? rows : decimal.Parse(check.Expected, CultureInfo.InvariantCulture);
            if (!(check.Comparison switch { CheckComparison.Equal => actual == expected, CheckComparison.AtLeast => actual >= expected, _ => actual <= expected }))
                throw new InvalidDataException($"{check.Name}: получено {actual}, ожидалось {check.Comparison} {expected}.");
        }
    }
    public object Builtin(string name, long sequence, int client, long key) => name.ToLowerInvariant() switch
    {
        "key" => key, "sequence" => checked(run.Rows + sequence), "client" => client, "baserows" => run.Rows, "seed" => unchecked((long)run.Config.Seed), "completed" => Completed,
        _ => name.StartsWith("rows_", StringComparison.OrdinalIgnoreCase) && TableRows.TryGetValue(name[5..], out var rows) ? rows :
            name.StartsWith("completed_", StringComparison.OrdinalIgnoreCase) ? CompletedByOperation.GetValueOrDefault(name[10..]) : throw new InvalidDataException("Неизвестный параметр @" + name)
    };
    public Task LoadBatchAsync(BenchForge.Contracts.DataRow[] rows, CancellationToken token) => throw new NotSupportedException();
    public Task<DatasetDigest> DigestAsync(CancellationToken token) => throw new NotSupportedException();
    public Task<long> CountAsync(CancellationToken token) => Task.FromResult(TableRows.Values.Sum());
    public async Task<EngineMetadata> MetadataAsync(CancellationToken token)
    {
        await using var command = _admin!.CreateCommand(); command.CommandText = SqlConnections.VersionQuery(run.Target);
        return new("csharp", RuntimeInformation.FrameworkDescription, DriverIdentity.Of(_admin.GetType()), Convert.ToString(await command.ExecuteScalarAsync(token))!,
            new() { ["workload_name"] = _workload.Name, ["workload_sha256"] = _workload.Fingerprint, ["workload_mode"] = _workload.Mode.ToString(),
                ["sql_dialect"] = run.Target.ToString(), ["transaction_retries"] = "none; retryable conflicts are counted as errors",
                ["execution_profile"] = run.Target switch { TargetKind.DuckDb => $"embedded OLAP; memory budget {run.Config.MemoryMb} MB; {(int)Math.Ceiling(run.Config.CpuLimit)} query threads; worker overhead not capped",
                    TargetKind.CockroachDb => "single node; replication disabled; default SERIALIZABLE", TargetKind.Oracle => "Oracle Free edition limits; own DBLAB schema in FREEPDB1", _ => "single instance SQL profile" },
                ["connections"] = "one dedicated SQL connection per logical client; pooling=false", ["prepared_queries"] = _workload.PrepareCommands.ToString(),
                ["batch_transport"] = "parameterized row INSERT inside batch transaction; same approach for custom SQL targets", ["generator"] = "custom-splitmix64-v1",
                ["workload_reset"] = _workload.ResetBetweenOperations ? "declared tables reloaded between separate operations and mix; stages share state" : "state shared between operations and stages",
                ["workload_checks"] = _workload.Checks.Count.ToString(CultureInfo.InvariantCulture),
                ["key_sampling"] = string.Join("; ", _workload.ActiveOperations.SelectMany(operation => operation.Parameters).Where(parameter => parameter.Generator == ValueGenerator.Sample)
                    .DistinctBy(WorkloadData.SampleKey).Select(parameter => parameter.Reference + "." + parameter.SampleColumn + ": reservoir <= " + parameter.SampleSize + " original non-null values; sampled keys remain in worker RAM")),
                ["operation_unit"] = "one complete operation, including all steps, custom parameter generation, step validation and transaction commit; integrity checks outside each window" });
    }
    public async Task<IClientSession> OpenSessionAsync(CancellationToken token)
    {
        var connection = SqlConnections.Create(run.Target, _connectionString);
        try
        {
            await connection.OpenAsync(token); await SqlConnections.ConfigureAsync(connection, run.Target, false, token, run.Config);
            var session = new CustomSqlSession(this, run, connection, Interlocked.Increment(ref _clients));
            await session.PrepareAsync(token); return session;
        }
        catch { await connection.DisposeAsync(); throw; }
    }
    internal long NextSequence() => Interlocked.Increment(ref _sequence);
    private sealed class SampleReservoir(ValueDefinition definition, ulong seed)
    {
        public ValueDefinition Definition { get; } = definition;
        public List<object> Values { get; } = [];
        private long _seen;
        private PortableRandom _random = new(WorkloadData.StableSeed(seed, WorkloadData.SampleKey(definition), 0));
        public void Add(object value)
        {
            _seen++;
            if (Values.Count < Definition.SampleSize) Values.Add(value);
            else { var slot = (long)(_random.Next() % (ulong)_seen); if (slot < Values.Count) Values[(int)slot] = value; }
        }
    }
    internal static DbParameter AddParameter(DbCommand command, string name, SqlValueType type)
    {
        var parameter = command.CreateParameter(); parameter.ParameterName = SqlConnections.ParameterName(command, name); parameter.Value = DBNull.Value;
        parameter.DbType = type switch { SqlValueType.Int64 => DbType.Int64, SqlValueType.Int32 => DbType.Int32, SqlValueType.Decimal => DbType.Decimal, SqlValueType.Double => DbType.Double,
            SqlValueType.Boolean => DbType.Boolean, SqlValueType.DateTime => DbType.DateTime, SqlValueType.Guid => parameter is Oracle.ManagedDataAccess.Client.OracleParameter ? DbType.Binary : DbType.Guid, _ => DbType.String };
        if (parameter is Microsoft.Data.SqlClient.SqlParameter)
        {
            if (type == SqlValueType.Text) parameter.Size = -1;
            if (type == SqlValueType.Decimal) { parameter.Precision = 28; parameter.Scale = 12; }
            if (type == SqlValueType.DateTime) { parameter.DbType = DbType.DateTime2; parameter.Size = 8; parameter.Scale = 6; }
        }
        if (parameter is Oracle.ManagedDataAccess.Client.OracleParameter oracle)
        {
            if (type == SqlValueType.Guid) { oracle.OracleDbType = Oracle.ManagedDataAccess.Client.OracleDbType.Raw; oracle.Size = 16; }
            if (type == SqlValueType.Text) { oracle.OracleDbType = Oracle.ManagedDataAccess.Client.OracleDbType.NVarchar2; oracle.Size = 0; }
            if (type == SqlValueType.DateTime) oracle.OracleDbType = Oracle.ManagedDataAccess.Client.OracleDbType.TimeStamp;
            if (type == SqlValueType.Decimal) { oracle.Precision = 28; oracle.Scale = 12; }
        }
        command.Parameters.Add(parameter); return parameter;
    }
    internal static void SetValue(DbParameter parameter, object value, SqlValueType type, TargetKind target)
    {
        parameter.Value = ProviderValue(value, type, target);
        if (parameter is Oracle.ManagedDataAccess.Client.OracleParameter oracle && type == SqlValueType.Text)
        {
            var length = value is string text ? text.Length : 0;
            if (length > 2000) oracle.OracleDbType = Oracle.ManagedDataAccess.Client.OracleDbType.Clob;
            oracle.Size = length;
        }
    }
    internal static object ProviderValue(object value, SqlValueType type, TargetKind target) => value is Guid guid && target == TargetKind.Oracle ? guid.ToByteArray() : value is not DBNull && type == SqlValueType.Guid && target is not (TargetKind.PostgreSql or TargetKind.SqlServer or TargetKind.DuckDb or TargetKind.CockroachDb) ? value.ToString()! : value;
    public async ValueTask DisposeAsync() { if (_admin is not null) await _admin.DisposeAsync(); }
}

internal sealed class CustomSqlSession(CustomSqlDatabase database, RunCase run, DbConnection connection, int client) : ICustomSqlSession
{
    private readonly SqlWorkload _workload = run.Config.CustomWorkload!;
    private readonly Dictionary<string, List<(SqlStep Step, DbCommand Command)>> _commands = [];
    private readonly Dictionary<string, Dictionary<string, SqlValueType>> _types = [];
    private readonly Dictionary<string, object> _values = new(StringComparer.OrdinalIgnoreCase);
    private SqlOperation[] _choices = [];
    private SqlOperation? _selected;
    private long _totalWeight;
    private PortableRandom _random = new(WorkloadData.StableSeed(run.Config.Seed ^ (ulong)run.Repeat, "client", client));
    public string LastOperation { get; private set; } = "";

    public async Task PrepareAsync(CancellationToken token)
    {
        _choices = _workload.ActiveOperations.Where(operation => !database.WarmupOnly || operation.ReadOnly).ToArray();
        _selected = database.SelectedOperation is { } selected ? _choices.Single(operation => operation.Id == selected) : null;
        _totalWeight = _choices.Sum(operation => (long)operation.Weight);
        foreach (var operation in _choices)
        {
            var parameters = operation.Parameters.ToDictionary(parameter => parameter.Name, parameter => parameter.Type, StringComparer.OrdinalIgnoreCase);
            foreach (var capture in operation.Steps.Where(step => step.CaptureAs is not null)) parameters.Add(capture.CaptureAs!, capture.CaptureType);
            _types.Add(operation.Id, parameters);
            var commands = new List<(SqlStep, DbCommand)>(); _commands.Add(operation.Id, commands);
            foreach (var step in operation.Steps)
            {
                var command = connection.CreateCommand(); SqlConnections.SetText(command, step.Command.For(run.Target)); command.CommandTimeout = _workload.CommandTimeoutSeconds;
                commands.Add((step, command));
                foreach (var name in SqlWorkloadText.Parameters(step.Command.For(run.Target)))
                { var type = parameters.GetValueOrDefault(name, SqlValueType.Int64); var parameter = CustomSqlDatabase.AddParameter(command, name, type); if (type == SqlValueType.Guid && run.Target is not (TargetKind.PostgreSql or TargetKind.SqlServer or TargetKind.DuckDb or TargetKind.Oracle or TargetKind.CockroachDb)) parameter.DbType = DbType.String; }
                if (_workload.PrepareCommands) await command.PrepareAsync(token);
            }
        }
    }
    public async ValueTask<long> ExecuteAsync(Scenario scenario, long key, BenchForge.Contracts.DataRow? row, BenchForge.Contracts.DataRow[]? batch, CancellationToken token)
    {
        var operation = _selected ?? _choices[0];
        if (_selected is null)
        { var pick = (long)(_random.Next() % (ulong)_totalWeight); foreach (var candidate in _choices) if ((pick -= candidate.Weight) < 0) { operation = candidate; break; } }
        LastOperation = operation.Id;
        var sequence = database.NextSequence();
        var values = _values; values.Clear();
        foreach (var parameter in operation.Parameters) values.Add(parameter.Name, WorkloadData.Generate(parameter, new(sequence, sequence, client, key, run.Rows, database.TableRows, database.Samples), ref _random));
        DbTransaction? transaction = null;
        try
        {
            if (operation.Transaction) transaction = operation.Isolation == SqlIsolation.Default ? await connection.BeginTransactionAsync(token) :
                await connection.BeginTransactionAsync(operation.Isolation switch { SqlIsolation.ReadCommitted => IsolationLevel.ReadCommitted, SqlIsolation.RepeatableRead => IsolationLevel.RepeatableRead, _ => IsolationLevel.Serializable }, token);
            long affected = 0;
            foreach (var (step, command) in _commands[operation.Id])
            {
                command.Transaction = transaction;
                foreach (DbParameter parameter in command.Parameters)
                {
                    var name = parameter.ParameterName.TrimStart('@', ':', '$');
                    var value = values.TryGetValue(name, out var generated) ? generated : database.Builtin(name, sequence, client, key);
                    CustomSqlDatabase.SetValue(parameter, value, _types[operation.Id].GetValueOrDefault(name, SqlValueType.Int64), run.Target);
                }
                long count;
                if (step.Result == SqlResultMode.NonQuery) count = await command.ExecuteNonQueryAsync(token);
                else if (step.Result == SqlResultMode.Scalar)
                {
                    var scalar = await command.ExecuteScalarAsync(token); count = scalar is null or DBNull ? 0 : 1;
                    if (step.ExpectedScalar is { } expected && Convert.ToString(scalar, CultureInfo.InvariantCulture) != expected) throw new InvalidDataException("Значение Scalar отличается от ожидаемого.");
                    if (step.CaptureAs is { } name) values[name] = WorkloadData.ConvertValue(scalar, step.CaptureType);
                }
                else
                {
                    count = 0; await using var reader = await command.ExecuteReaderAsync(token);
                    do { while (await reader.ReadAsync(token)) { for (var index = 0; index < reader.FieldCount; index++) _ = reader.GetValue(index); count++; } } while (await reader.NextResultAsync(token));
                }
                if (step.MinRows is { } minimum && count < minimum || step.MaxRows is { } maximum && count > maximum) throw new InvalidDataException("Число строк SQL-шага вне ожидаемого диапазона.");
                affected += Math.Max(0, count);
            }
            if (transaction is not null) await transaction.CommitAsync(token);
            return affected;
        }
        catch
        {
            if (transaction is not null) try { await transaction.RollbackAsync(CancellationToken.None); } catch { }
            throw;
        }
        finally { foreach (var (_, command) in _commands[operation.Id]) command.Transaction = null; if (transaction is not null) await transaction.DisposeAsync(); }
    }
    public async ValueTask DisposeAsync()
    { foreach (var commands in _commands.Values) foreach (var (_, command) in commands) await command.DisposeAsync(); await connection.DisposeAsync(); }
}
