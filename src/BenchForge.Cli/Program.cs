using System.Text.Json;
using BenchForge.Contracts;
using BenchForge.Coordinator;

Console.OutputEncoding = System.Text.Encoding.UTF8;
var events = args.Contains("--events");
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); if (!events) Console.WriteLine("Остановка: текущие операции будут завершены, отчёт сохранён."); };
var cancelFileIndex = Array.IndexOf(args, "--cancel-file");
if (cancelFileIndex >= 0 && cancelFileIndex + 1 < args.Length) _ = Task.Run(async () =>
{
    var cancelFile = args[cancelFileIndex + 1];
    while (!cancellation.IsCancellationRequested)
    {
        if (File.Exists(cancelFile)) { cancellation.Cancel(); return; }
        await Task.Delay(100);
    }
});
else if (events) _ = Task.Run(async () =>
{
    while (await Console.In.ReadLineAsync() is { } command)
        if (command == "cancel") cancellation.Cancel();
});
try
{
    if (args.Contains("--engines")) { Console.WriteLine(JsonSerializer.Serialize(Engines.All, Json.Pretty)); return 0; }
    if (args.Contains("--worker-vectors")) { Console.WriteLine(JsonSerializer.Serialize(WorkerConformance.Vectors(), Json.Options)); return 0; }
    if (args.Contains("--verify-workers"))
    {
        var languages = Array.IndexOf(args, "--languages");
        var selected = languages >= 0 && languages + 1 < args.Length ? args[languages + 1].Split(',') : new[] { "csharp" };
        foreach (var language in selected) await WorkerCatalog.CheckAsync(language, cancellation.Token);
        return 0;
    }
    if (args.Contains("--prepare-workers"))
    {
        var languageIndex = Array.IndexOf(args, "--languages");
        var selected = languageIndex >= 0 && languageIndex + 1 < args.Length ? args[languageIndex + 1].Split(',') : Engines.All.Select(engine => engine.Id).ToArray();
        await WorkerPreparation.PrepareLocalAsync(selected, Console.WriteLine, cancellation.Token);
        return 0;
    }
    if (args.Contains("--targets")) { Console.WriteLine(JsonSerializer.Serialize(Catalog.Targets, Json.Pretty)); return 0; }
    var configIndex = Array.IndexOf(args, "--config");
    if (args.Contains("--prepare-server"))
    {
        if (configIndex < 0 || configIndex + 1 >= args.Length) throw new ArgumentException("Укажите --config с настройками remoteServer.");
        var preparation = JsonSerializer.Deserialize<BenchmarkConfig>(await File.ReadAllTextAsync(args[configIndex + 1]), Json.Options)!;
        preparation.Validate();
        await new RemoteAgentClient().PrepareAsync(preparation.RemoteServer ?? throw new ArgumentException("Не указан remoteServer."), preparation.EngineIds, Console.WriteLine, cancellation.Token);
        return 0;
    }
    if (args.Contains("--prepare-vm"))
    {
        var preparation = configIndex >= 0 && configIndex + 1 < args.Length
            ? JsonSerializer.Deserialize<BenchmarkConfig>(await File.ReadAllTextAsync(args[configIndex + 1]), Json.Options)!
            : new BenchmarkConfig { VirtualMachine = new(), Targets = Catalog.Targets.Select(target => target.Kind).ToList() };
        await new VirtualMachineTemplate().PrepareAsync(preparation.VirtualMachine ?? new(),
            preparation.Targets.Where(target => target != TargetKind.Sqlite).Select(preparation.Image), Console.WriteLine, cancellation.Token);
        return 0;
    }
    BenchmarkConfig config;
    if (args.Contains("--smoke")) config = new() { Targets = [TargetKind.Sqlite], DatasetRows = [1000], Clients = [1, 2], Repeats = 1, DurationSeconds = .2, WarmupSeconds = .1, BatchSize = 100 };
    else if (configIndex >= 0 && configIndex + 1 < args.Length) config = JsonSerializer.Deserialize<BenchmarkConfig>(await File.ReadAllTextAsync(args[configIndex + 1]), Json.Options)!;
    else
    {
        Console.WriteLine("BenchForge · native language engines\n--smoke                  Проверка SQLite без Docker\n--config <file.json>      Эксперимент из конфигурации\n--check --config <file>   Проверка готовности\n--prepare-vm             Подготовка шаблона чистой VM\n--prepare-workers        Подготовить движки на этом компьютере\n--prepare-server --config FILE  Подготовить Linux-сервер по SSH\n--resume-server --config FILE   Получить текущее задание сервера\n--languages <ids>        Языки: csharp,java,python,rust,node,go\n--targets / --engines     Поддерживаемые базы и движки\n--reports <directory>     Папка отчётов"); return 0;
    }
    config.Validate();
    if (args.Contains("--resume-server"))
    {
        var resumed = await new RemoteExperimentRunner().ResumeAsync(config.RemoteServer ?? throw new ArgumentException("Не указан remoteServer."),
            change => { if (events) Console.WriteLine(JsonSerializer.Serialize(change, Json.Options)); else if (change.Type == "log") Console.WriteLine(change.Message); }, cancellation.Token);
        if (!events) Console.WriteLine("Статус: " + resumed.Report.Status + "\nОтчёт: " + Path.Combine(resumed.Folder, "report.json"));
        return resumed.Report.Status == "completed" ? 0 : resumed.Report.Status == "cancelled" ? 2 : 1;
    }
    if (args.Contains("--check"))
    { var problems = await ExperimentRunner.PreflightAsync(config, cancellation.Token); foreach (var problem in problems) Console.WriteLine(problem); return problems.Count > 0 ? 1 : 0; }
    var reportsIndex = Array.IndexOf(args, "--reports");
    var reports = reportsIndex >= 0 && reportsIndex + 1 < args.Length ? args[reportsIndex + 1] : Path.Combine(Environment.CurrentDirectory, "reports");
    var runner = new ExperimentRunner(update =>
    {
        if (events) { Console.WriteLine(JsonSerializer.Serialize(update, Json.Options)); return; }
        if (update.Type == "log") Console.WriteLine(update.Message);
        if (update.Result is { } r)
            Console.WriteLine($"  {r.Phase}: {(r.Unit == "rows/s" ? r.RowsPerSecond : r.Unit == "seconds" ? r.ElapsedSeconds : r.OperationsPerSecond):N1} {r.Unit}; p99={r.ServiceLatency?.P99Ms:N3} мс; errors={r.Errors}; dropped={r.Dropped}; verified={r.Verified}");
    });
    var result = await runner.RunAsync(config, reports, cancellation.Token);
    if (!events) Console.WriteLine($"Статус: {result.Report.Status}\nОтчёт: {Path.Combine(result.Folder, "report.json")}");
    return result.Report.Status == "completed" ? 0 : result.Report.Status == "cancelled" ? 2 : 1;
}
catch (Exception exception) { Console.Error.WriteLine(exception.Message); return 1; }
