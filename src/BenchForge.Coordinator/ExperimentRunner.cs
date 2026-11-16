using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using BenchForge.Contracts;

namespace BenchForge.Coordinator;

public sealed record RunUpdate(string Type, string Message = "", PhaseResult? Result = null, int CompletedCases = 0, int TotalCases = 0, string? Folder = null);
public sealed class ExperimentRunner(Action<RunUpdate>? update = null, IWorkerHost? workerHost = null, IVirtualMachineCaseRunner? virtualMachines = null)
{
    private readonly IWorkerHost _workerHost = workerHost ?? new WorkerProcess();
    private readonly IVirtualMachineCaseRunner _virtualMachines = virtualMachines ?? new VirtualMachineCaseRunner();
    private void Log(string text) => update?.Invoke(new("log", text));
    public static async Task<List<string>> PreflightAsync(BenchmarkConfig config, CancellationToken token, IVirtualMachineCaseRunner? virtualMachines = null)
    {
        var problems = new List<string>();
        if (config.VirtualMachine is { } vm)
        {
            try { await (virtualMachines ?? new VirtualMachineCaseRunner()).CheckAsync(vm, token); }
            catch (Exception error) when (error is not OperationCanceledException) { problems.Add("VM недоступна. " + error.Message); }
            return problems;
        }
        try { _ = ProcessUtil.WorkerPath(); } catch (Exception e) { problems.Add(e.Message); }
        if (config.Targets.Any(t => t != TargetKind.Sqlite))
        {
            try
            {
                var os = await ProcessUtil.RunAsync(ProcessUtil.Docker(), ["info", "--format", "{{.OSType}}"], token, 15);
                if (os != "linux") problems.Add("В Docker Desktop нужен режим Linux containers.");
            }
            catch (Exception e) when (e is not OperationCanceledException) { problems.Add("Docker недоступен. Запустите Docker Desktop. " + e.Message); }
        }
        return problems;
    }
    public async Task<(BenchmarkReport Report, string Folder)> RunAsync(BenchmarkConfig config, string reportsRoot, CancellationToken token)
    {
        config.Validate();
        var problems = await PreflightAsync(config, token, _virtualMachines);
        if (problems.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, problems));
        var report = new BenchmarkReport { Config = config, Environment = new() { ["os"] = RuntimeInformation.OSDescription,
            ["architecture"] = RuntimeInformation.ProcessArchitecture.ToString(), ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["logical_cpus"] = Environment.ProcessorCount.ToString(), ["generator"] = DataGenerator.Version,
            ["histogram"] = "binary128-us-v1: <=0.782% bucket width, integer-us floor; service and end-to-end distributions separately",
            ["resource_semantics"] = "client RSS/working set; Docker RAM excludes inactive file cache on Linux; sampled maxima, CPU 100%=one core; I/O approximate sample deltas",
            ["topology"] = "one owned fresh database instance and one C# worker process per case; sequential cases",
            ["database_profile"] = "single node; SQL and MongoDB durable acknowledgements; Redis/Valkey memory-only cache; ClickHouse HTTP MergeTree analytics",
            ["measurements"] = "client CPU/GC are per-phase; load rate includes generation, whose time is also recorded; setup and validation excluded from query timing" } };
        var folder = Path.Combine(Path.GetFullPath(reportsRoot), DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss'Z'") + "-" + report.RunId[..8]);
        Directory.CreateDirectory(folder);
        await ReportStore.SaveAsync(folder, report);
        var clientCounts = config.CaseClients();
        var total = config.Targets.Count * config.DatasetRows.Count * clientCounts.Count * config.Repeats;
        var completed = 0;
        update?.Invoke(new("started", Folder: folder, TotalCases: total));
        try
        {
            for (var repeat = 1; repeat <= config.Repeats; repeat++)
            {
                var offset = (repeat - 1) % config.Targets.Count;
                foreach (var target in config.Targets.Skip(offset).Concat(config.Targets.Take(offset)))
                foreach (var rows in config.DatasetRows)
                foreach (var clients in clientCounts)
                {
                    token.ThrowIfCancellationRequested();
                    var caseId = Guid.NewGuid().ToString("N");
                    var load = config.Plan is { } plan ? LoadPlan.Name(plan.Profile) : $"клиентов: {clients}";
                    var label = $"{Catalog.Get(target).Name} · {rows:N0} записей · {load} · повтор {repeat}";
                    Log(label);
                    if (config.VirtualMachine is not null)
                    {
                        try
                        {
                            var guest = await _virtualMachines.RunAsync(config, target, rows, clients, caseId, folder, async change =>
                            {
                                if (change.Result is { } measured)
                                {
                                    if (measured.Target != target || measured.DatasetRows != rows || measured.Repeat != 1 || !MatchesLoad(measured, config.Plan, clients))
                                        throw new InvalidDataException("Агент VM вернул результаты другого запуска.");
                                    var result = measured with { Repeat = repeat };
                                    report.Results.Add(result);
                                    await ReportStore.SaveAsync(folder, report);
                                    update?.Invoke(new("result", Result: result, CompletedCases: completed, TotalCases: total));
                                }
                                else if (change.Type is "log" or "progress")
                                    update?.Invoke(change with { CompletedCases = completed, TotalCases = total, Folder = folder });
                            }, token);
                            foreach (var (key, metadata) in guest.Engines) report.Engines.Add(key, metadata);
                            report.Environment["host_os"] = RuntimeInformation.OSDescription;
                            foreach (var (key, value) in guest.Environment) report.Environment[key] = value;
                            report.Environment["topology"] = "fresh Linux VM per case cloned from a stopped template; database and worker inside VM; sequential cases";
                            report.Environment["isolation"] = "clean VM; host cache and physical hardware are shared";
                            report.Warnings.AddRange(guest.Warnings);
                            report.Failures.AddRange(guest.Failures.Select(message => label + ": " + message));
                            if (guest.Status == "cancelled" && !token.IsCancellationRequested)
                                report.Failures.Add(label + ": агент VM остановил прогон досрочно.");
                            if (guest.Status is not ("completed" or "completed_with_errors" or "cancelled") && guest.Failures.Count == 0)
                                report.Failures.Add(label + ": агент VM завершил прогон со статусом " + guest.Status);
                        }
                        catch (VirtualMachineCleanupException) { throw; }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch (Exception error) { report.Failures.Add(label + ": " + error.Message); Log("Ошибка VM: " + error.Message); }
                        finally
                        {
                            completed++;
                            await ReportStore.SaveAsync(folder, report);
                            update?.Invoke(new("case_finished", CompletedCases: completed, TotalCases: total));
                        }
                        token.ThrowIfCancellationRequested();
                        continue;
                    }
                    var owned = new OwnedTarget(target, config, caseId, folder, Log);
                    var resources = new ResourceCollector();
                    long phaseStart = Stopwatch.GetTimestamp();
                    try
                    {
                        await owned.StartAsync(token); resources.Start(owned.Container);
                        var run = new RunCase(caseId, target, rows, clients, repeat, config, owned.Endpoint);
                        long? phaseEnd = null;
                        await _workerHost.RunAsync(run, async message =>
                        {
                            switch (message.Type)
                            {
                                case "metadata":
                                    var metadata = message.Data.Deserialize<EngineMetadata>(Json.Options)!;
                                    metadata.Settings["image_tag"] = target == TargetKind.Sqlite ? "embedded" : config.Image(target);
                                    metadata.Settings["image_id"] = owned.ImageId ?? "embedded";
                                    metadata.Settings["cpu_limit"] = target == TargetKind.Sqlite ? "not isolated: worker process" : config.CpuLimit.ToString(System.Globalization.CultureInfo.InvariantCulture);
                                    metadata.Settings["memory_limit_mb"] = target == TargetKind.Sqlite ? "not isolated: worker process" : config.MemoryMb.ToString();
                                    report.Engines.Add(caseId, metadata); break;
                                case "phase_start":
                                    phaseStart = Stopwatch.GetTimestamp(); phaseEnd = null;
                                    var phase = message.Data.Deserialize<PhaseStart>(Json.Options)!;
                                    var step = phase.Stage is null ? "" : $" · {phase.Stage.Name} · клиентов: {phase.Clients}";
                                    Log(label + step + " · " + phase.Phase); break;
                                case "phase_end":
                                    phaseEnd = Stopwatch.GetTimestamp(); break;
                                case "result":
                                    var result = message.Data.Deserialize<PhaseResult>(Json.Options)!;
                                    if (result.Target != target || result.DatasetRows != rows || result.Repeat != repeat || !MatchesLoad(result, config.Plan, clients))
                                        throw new InvalidDataException("Движок вернул результаты другого запуска.");
                                    if (target != TargetKind.Sqlite) result = result with { Server = resources.Summary(phaseStart, phaseEnd ?? Stopwatch.GetTimestamp()) };
                                    report.Results.Add(result); await ReportStore.SaveAsync(folder, report);
                                    update?.Invoke(new("result", Result: result, CompletedCases: completed, TotalCases: total)); break;
                                case "log": Log(message.Data.GetProperty("message").GetString()!); break;
                                case "progress": update?.Invoke(new("progress", message.Data.GetRawText(), CompletedCases: completed, TotalCases: total)); break;
                                case "unsupported": Log("Пропуск: " + message.Data.GetRawText()); break;
                            }
                        }, token);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception exception) { report.Failures.Add(label + ": " + exception.Message); Log("Ошибка: " + exception.Message); }
                    finally
                    {
                        try { await resources.DisposeAsync(); }
                        catch (Exception e) { report.Warnings.Add("Мониторинг: " + e.Message); }
                        if (resources.Warning is not null) { report.Warnings.Add(resources.Warning); Log(resources.Warning); }
                        await File.WriteAllTextAsync(Path.Combine(folder, "resources-" + caseId + ".json"), JsonSerializer.Serialize(resources.Samples, Json.Pretty));
                        try { await owned.DisposeAsync(); } catch (Exception e) { report.Warnings.Add("Очистка " + caseId + ": " + e.Message); }
                        completed++; await ReportStore.SaveAsync(folder, report);
                        update?.Invoke(new("case_finished", CompletedCases: completed, TotalCases: total));
                    }
                }
            }
            report.Status = report.Failures.Count > 0 ? (report.Results.Count > 0 ? "partial" : "failed") : report.Results.Any(r => r.Errors > 0 || !r.Verified) ? "completed_with_errors" : "completed";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { report.Status = "cancelled"; }
        catch (Exception e) { report.Status = "failed"; report.Failures.Add(e.Message); }
        finally
        {
            if (token.IsCancellationRequested) report.Status = "cancelled";
            report.FinishedAt = DateTimeOffset.UtcNow;
            await ReportStore.SaveAsync(folder, report);
            update?.Invoke(new("finished", report.Status, CompletedCases: completed, TotalCases: total, Folder: folder));
        }
        return (report, folder);
    }

    private static bool MatchesLoad(PhaseResult result, LoadPlan? plan, int clients)
    {
        if (plan is null || ReportAnalysis.SetupPhase(result.Phase)) return result.Stage is null && result.Clients == clients;
        if (result.Stage is not { } stage || stage.Number < 1 || stage.Number > plan.Stages.Count) return false;
        var planned = plan.Stages[stage.Number - 1];
        return result.Clients == planned.Clients && stage.Name == planned.Name && stage.TargetSeconds == planned.Seconds &&
            stage.OperationsPerSecond == planned.OperationsPerSecond;
    }
}
