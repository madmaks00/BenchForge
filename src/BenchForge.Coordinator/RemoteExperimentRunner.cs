using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using BenchForge.Contracts;

namespace BenchForge.Coordinator;

public interface IRemoteExperimentRunner
{
    Task CheckAsync(BenchmarkConfig config, CancellationToken token);
    Task<(BenchmarkReport Report, string Folder)> RunAsync(BenchmarkConfig config, string reportsRoot, Action<RunUpdate>? update, CancellationToken token);
}

public sealed class RemoteExperimentRunner(RemoteAgentClient? client = null, RemoteJobStore? store = null) : IRemoteExperimentRunner
{
    private readonly RemoteAgentClient _client = client ?? new();
    private readonly RemoteJobStore _store = store ?? new();
    public async Task CheckAsync(BenchmarkConfig config, CancellationToken token) =>
        _ = await _client.CheckAsync(config.RemoteServer ?? throw new ArgumentException("Не выбран сервер."), config.EngineIds, token);

    public async Task<(BenchmarkReport Report, string Folder)> RunAsync(BenchmarkConfig config, string reportsRoot, Action<RunUpdate>? update, CancellationToken token)
    {
        config.Validate();
        var server = config.RemoteServer ?? throw new ArgumentException("Не выбран сервер.");
        update?.Invoke(new("log", "Проверка сервера " + server.DisplayName));
        var (info, package) = await _client.CheckAsync(server, config.EngineIds, token);
        var job = Guid.NewGuid().ToString("N");
        var folder = Path.Combine(Path.GetFullPath(reportsRoot), DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss'Z'") + "-" + job[..8]);
        Directory.CreateDirectory(folder);
        var ticket = new RemoteJobTicket(job, server, info, package, config, folder, DateTimeOffset.UtcNow, "starting");
        await _store.SaveAsync(ticket);
        try
        {
            update?.Invoke(new("log", "Запуск задания " + job + " на сервере " + server.DisplayName));
            await _client.StartAsync(server, info, package, job, config, folder, token);
        }
        catch (SshConnectionException error)
        {
            // The server may have accepted the detached launch before the connection failed.
            var pending = Initial(ticket); pending.Status = "disconnected"; pending.Warnings.Add(error.Message);
            await _store.SaveAsync(ticket with { Status = "disconnected" }); await ReportStore.SaveAsync(folder, pending);
            update?.Invoke(new("finished", pending.Status, Folder: folder));
            return (pending, folder);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Cancellation can arrive after Docker accepted the detached launch.
            // Observe the same ID and request its cancellation instead of forgetting it.
            return await ObserveAsync(ticket, update, token);
        }
        catch
        {
            await _store.SaveAsync(ticket with { Status = "failed" });
            throw;
        }
        return await ObserveAsync(ticket, update, token);
    }

    public Task<(BenchmarkReport Report, string Folder)> ResumeAsync(RemoteServerOptions server, Action<RunUpdate>? update, CancellationToken token)
    {
        var ticket = _store.Latest(server) ?? throw new InvalidOperationException("Для этого подключения нет незавершённых заданий.");
        server.Validate(); RemoteAgentClient.ValidateJob(ticket.Job);
        return ObserveAsync(ticket with { Server = server }, update, token);
    }

    private static BenchmarkReport Initial(RemoteJobTicket ticket) => new()
    {
        RunId = ticket.Job, StartedAt = ticket.CreatedAt, Config = ticket.Config with { RemoteServer = ticket.Server.ForReport() },
        Environment = new() { ["execution_location"] = "remote", ["remote_label"] = ticket.Server.DisplayName,
            ["remote_host"] = ticket.Info.Hostname, ["remote_job_id"] = ticket.Job, ["controller_os"] = RuntimeInformation.OSDescription,
            ["topology"] = "load generator and temporary database containers on Linux server; controller only sends configuration and receives results" }
    };

    private async Task<(BenchmarkReport Report, string Folder)> ObserveAsync(RemoteJobTicket ticket, Action<RunUpdate>? update, CancellationToken token)
    {
        var report = Initial(ticket);
        var total = ticket.Config.EngineIds.Count * ticket.Config.Targets.Count * ticket.Config.DatasetRows.Count * ticket.Config.CaseClients().Count * ticket.Config.Repeats;
        var completed = 0; long offset = 0; var retries = 0; var cancelling = false; var terminalState = "failed";
        using var stopDeadline = new CancellationTokenSource();
        await _store.SaveAsync(ticket with { Status = "running" });
        await ReportStore.SaveAsync(ticket.Folder, report);
        update?.Invoke(new("started", Folder: ticket.Folder, TotalCases: total));
        while (true)
        {
            try
            {
                if (token.IsCancellationRequested && !cancelling)
                {
                    await _client.CancelAsync(ticket.Server, ticket.Info, ticket.Package, ticket.Job, CancellationToken.None);
                    cancelling = true; stopDeadline.CancelAfter(TimeSpan.FromSeconds(90));
                    await _store.SaveAsync(ticket with { Status = "cancel_requested" });
                    update?.Invoke(new("log", "Сервер получил запрос остановки; ожидается сохранение отчёта."));
                }
                var observation = cancelling ? stopDeadline.Token : token;
                await _client.FollowAsync(ticket.Server, ticket.Info, ticket.Package, ticket.Job, offset, async line =>
                {
                    if (string.IsNullOrWhiteSpace(line)) { offset += Encoding.UTF8.GetByteCount(line) + 1; return; }
                    RunUpdate message;
                    try { message = JsonSerializer.Deserialize<RunUpdate>(line, Json.Options) ?? throw new JsonException(); }
                    catch (JsonException) { throw new SshConnectionException("Передача сообщения прервалась; чтение будет продолжено с последней полной строки."); }
                    if (message.Result is { } row)
                    {
                        ValidateRow(row, ticket.Config);
                        report.Results.Add(row); await ReportStore.SaveAsync(ticket.Folder, report);
                    }
                    completed = Math.Max(completed, message.CompletedCases);
                    update?.Invoke(message with { Folder = ticket.Folder, TotalCases = total });
                    offset += Encoding.UTF8.GetByteCount(line) + 1;
                    retries = 0;
                }, observation);
                var state = await _client.StateAsync(ticket.Server, ticket.Info, ticket.Package, ticket.Job, observation);
                if (state is not ("running" or "completed" or "cancelled" or "failed" or "absent")) throw new InvalidDataException("Неизвестный статус серверного задания.");
                if (state != "running") { terminalState = state; break; }
                await Task.Delay(1000, observation);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested && !cancelling) { continue; }
            catch (SshConnectionException error)
            {
                if (++retries < 3 && !stopDeadline.IsCancellationRequested)
                { update?.Invoke(new("log", "Связь с сервером прервалась. Повторное подключение…")); await Task.Delay(2000, CancellationToken.None); continue; }
                return await DisconnectedAsync(error.Message);
            }
            catch (OperationCanceledException) when (cancelling)
            { return await DisconnectedAsync("Остановка ещё не подтверждена. Задание остаётся на сервере; продолжите наблюдение после восстановления связи."); }
            catch (IOException error) { return await DisconnectedAsync(error.Message); }
            catch (InvalidDataException error) { return await DisconnectedAsync(error.Message); }
        }
        try
        {
            var archive = await _client.DownloadAsync(ticket.Server, ticket.Info, ticket.Package, ticket.Job, ticket.Folder, CancellationToken.None);
            var extracted = Path.Combine(ticket.Folder, "collected-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(extracted);
            try
            {
                await ExtractAsync(archive, extracted);
                var remote = JsonSerializer.Deserialize<BenchmarkReport>(await File.ReadAllTextAsync(Path.Combine(extracted, "report.json")), Json.Options)
                    ?? throw new InvalidDataException("Пустой отчёт сервера.");
                var expected = ticket.Config with { RemoteServer = null, VirtualMachine = null };
                if (remote.SchemaVersion != 1 || remote.Environment.GetValueOrDefault("remote_job_id") != ticket.Job ||
                    remote.Status is not ("completed" or "completed_with_errors" or "partial" or "cancelled" or "failed") || remote.FinishedAt is null ||
                    JsonSerializer.Serialize(remote.Config, Json.Options) != JsonSerializer.Serialize(expected, Json.Options))
                    throw new InvalidDataException("Сервер вернул отчёт с другими настройками.");
                foreach (var row in remote.Results) ValidateRow(row, expected);
                report = new() { RunId = remote.RunId, StartedAt = remote.StartedAt, FinishedAt = remote.FinishedAt, Status = remote.Status,
                    Config = ticket.Config with { RemoteServer = ticket.Server.ForReport() }, Environment = remote.Environment, Engines = remote.Engines,
                    Results = remote.Results, Summary = remote.Summary, Warnings = remote.Warnings, Failures = remote.Failures };
                foreach (var (key, value) in Initial(ticket).Environment) report.Environment[key] = value;
                report.Environment["agent_os"] = remote.Environment.GetValueOrDefault("os", "Linux");
                report.Environment["os"] = ticket.Info.Os;
                report.Environment["server_kernel"] = ticket.Info.Kernel;
                report.Environment["controller_runtime"] = RuntimeInformation.FrameworkDescription;
                foreach (var resource in Directory.EnumerateFiles(extracted, "resources-*.json"))
                    File.Copy(resource, Path.Combine(ticket.Folder, Path.GetFileName(resource)), overwrite: true);
            }
            finally { Directory.Delete(extracted, recursive: true); File.Delete(archive); }
            try
            {
                await _client.CleanupAsync(ticket.Server, ticket.Info, ticket.Package, ticket.Job, CancellationToken.None);
                await _store.SaveAsync(ticket with { Status = report.Status });
            }
            catch (IOException error)
            {
                report.Warnings.Add("Отчёт получен, но очистка сервера не завершилась: " + error.Message);
                await _store.SaveAsync(ticket with { Status = "cleanup_pending" });
            }
            await ReportStore.SaveAsync(ticket.Folder, report);
            update?.Invoke(new("finished", report.Status, CompletedCases: completed, TotalCases: total, Folder: ticket.Folder));
            return (report, ticket.Folder);
        }
        catch (RemoteReportMissingException)
        {
            report.Status = terminalState == "cancelled" ? "cancelled" : "failed";
            report.FinishedAt = DateTimeOffset.UtcNow;
            try
            {
                var error = await _client.ErrorAsync(ticket.Server, ticket.Info, ticket.Package, ticket.Job, CancellationToken.None);
                report.Failures.Add(string.IsNullOrWhiteSpace(error) ? "Серверный агент завершился без отчёта: " + terminalState : error);
                await _client.CleanupAsync(ticket.Server, ticket.Info, ticket.Package, ticket.Job, CancellationToken.None);
                await _store.SaveAsync(ticket with { Status = report.Status });
            }
            catch (IOException error) { return await DisconnectedAsync("Не удалось завершить очистку задания: " + error.Message); }
            await ReportStore.SaveAsync(ticket.Folder, report);
            update?.Invoke(new("finished", report.Status, CompletedCases: completed, TotalCases: total, Folder: ticket.Folder));
            return (report, ticket.Folder);
        }
        catch (JsonException error) { return await DisconnectedAsync("Не удалось прочитать отчёт сервера: " + error.Message); }
        catch (InvalidDataException error) { return await DisconnectedAsync("Серверный отчёт не прошёл проверку: " + error.Message); }
        catch (IOException error) { return await DisconnectedAsync("Отчёт остаётся на сервере. Повторите получение: " + error.Message); }

        async Task<(BenchmarkReport Report, string Folder)> DisconnectedAsync(string message)
        {
            report.Status = "disconnected"; report.FinishedAt = null; report.Warnings.Add(message);
            await _store.SaveAsync(ticket with { Status = "disconnected" }); await ReportStore.SaveAsync(ticket.Folder, report);
            update?.Invoke(new("log", message));
            update?.Invoke(new("finished", report.Status, CompletedCases: completed, TotalCases: total, Folder: ticket.Folder));
            return (report, ticket.Folder);
        }
    }

    private static void ValidateRow(PhaseResult row, BenchmarkConfig config)
    {
        if (!config.EngineIds.Contains(row.Engine) || !config.Targets.Contains(row.Target) || !config.DatasetRows.Contains(row.DatasetRows) || row.Repeat < 1 || row.Repeat > config.Repeats)
            throw new InvalidDataException("Сервер вернул измерения другого задания.");
        if (!ReportAnalysis.SetupPhase(row.Phase) && (!Enum.TryParse<Scenario>(row.Phase, out var scenario) || !config.Operations(row.Target).Contains(scenario)))
            throw new InvalidDataException("Неизвестная операция серверного измерения.");
        if (config.Plan is { } plan && !ReportAnalysis.SetupPhase(row.Phase))
        {
            if (row.Stage is not { } stage || stage.Number < 1 || stage.Number > plan.Stages.Count) throw new InvalidDataException("Неверный этап серверного измерения.");
            var planned = plan.Stages[stage.Number - 1];
            if (row.Clients != planned.Clients || stage.Name != planned.Name || stage.TargetSeconds != planned.Seconds || stage.OperationsPerSecond != planned.OperationsPerSecond)
                throw new InvalidDataException("Нагрузка серверного измерения отличается от плана.");
        }
        else if (row.Stage is not null || !config.CaseClients().Contains(row.Clients)) throw new InvalidDataException("Неверное число клиентов в серверном измерении.");
    }

    private static async Task ExtractAsync(string archive, string directory)
    {
        await using var file = File.OpenRead(archive);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        long total = 0;
        while (await reader.GetNextEntryAsync() is { } entry)
        {
            var name = entry.Name;
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || Path.GetFileName(name) != name || name.Contains('\\') ||
                !(name is "report.json" or "results.csv" or "summary.json" || name.StartsWith("resources-", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal)))
                throw new InvalidDataException("Неожиданный файл в серверном отчёте.");
            total += entry.Length;
            if (total > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Слишком большой распакованный отчёт.");
            await using var destination = new FileStream(Path.Combine(directory, name), FileMode.CreateNew);
            if (entry.DataStream is { } data) await data.CopyToAsync(destination);
        }
    }
}
