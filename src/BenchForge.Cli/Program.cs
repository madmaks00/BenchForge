using System.Text.Json;
using BenchForge.Contracts;
using BenchForge.Coordinator;

Console.OutputEncoding = System.Text.Encoding.UTF8;
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); Console.WriteLine("Остановка: текущие операции будут завершены, отчёт сохранён."); };
try
{
    if (args.Contains("--engines")) { Console.WriteLine(JsonSerializer.Serialize(Engines.All, Json.Pretty)); return 0; }
    if (args.Contains("--targets")) { Console.WriteLine(JsonSerializer.Serialize(Catalog.Targets, Json.Pretty)); return 0; }
    var configIndex = Array.IndexOf(args, "--config");
    BenchmarkConfig config;
    if (args.Contains("--smoke")) config = new() { Targets = [TargetKind.Sqlite], DatasetRows = [1000], Clients = [1, 2], Repeats = 1, DurationSeconds = .2, WarmupSeconds = .1, BatchSize = 100 };
    else if (configIndex >= 0 && configIndex + 1 < args.Length) config = JsonSerializer.Deserialize<BenchmarkConfig>(await File.ReadAllTextAsync(args[configIndex + 1]), Json.Options)!;
    else
    {
        Console.WriteLine("BenchForge · C# engine\n--smoke                  Проверка SQLite без Docker\n--config <file.json>      Эксперимент из конфигурации\n--check --config <file>   Проверка готовности\n--targets / --engines     Поддерживаемые базы и движки\n--reports <directory>     Папка отчётов"); return 0;
    }
    config.Validate();
    if (args.Contains("--check"))
    { var problems = await ExperimentRunner.PreflightAsync(config, cancellation.Token); foreach (var problem in problems) Console.WriteLine(problem); return problems.Count > 0 ? 1 : 0; }
    var reportsIndex = Array.IndexOf(args, "--reports");
    var reports = reportsIndex >= 0 && reportsIndex + 1 < args.Length ? args[reportsIndex + 1] : Path.Combine(Environment.CurrentDirectory, "reports");
    var runner = new ExperimentRunner(update =>
    {
        if (update.Type == "log") Console.WriteLine(update.Message);
        if (update.Result is { } r)
            Console.WriteLine($"  {r.Phase}: {(r.Unit == "rows/s" ? r.RowsPerSecond : r.Unit == "seconds" ? r.ElapsedSeconds : r.OperationsPerSecond):N1} {r.Unit}; p99={r.ServiceLatency?.P99Ms:N3} мс; errors={r.Errors}; dropped={r.Dropped}; verified={r.Verified}");
    });
    var result = await runner.RunAsync(config, reports, cancellation.Token);
    Console.WriteLine($"Статус: {result.Report.Status}\nОтчёт: {Path.Combine(result.Folder, "report.json")}");
    return result.Report.Status == "completed" ? 0 : result.Report.Status == "cancelled" ? 2 : 1;
}
catch (Exception exception) { Console.Error.WriteLine(exception.Message); return 1; }
