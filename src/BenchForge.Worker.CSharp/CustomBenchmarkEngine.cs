using System.Diagnostics;
using BenchForge.Contracts;
using BenchForge.Worker.CSharp.Databases;

namespace BenchForge.Worker.CSharp;

public sealed partial class BenchmarkEngine
{
    private async Task RunCustomAsync(CancellationToken token)
    {
        var workload = _config.CustomWorkload!;
        await using var database = new CustomSqlDatabase(run);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        while (true)
        {
            try { await database.InitializeAsync(token); break; }
            catch when (run.Target != TargetKind.Sqlite && !token.IsCancellationRequested && DateTimeOffset.UtcNow < deadline) { await Task.Delay(500, token); }
        }
        var metadata = await database.MetadataAsync(token);
#if DEBUG
        metadata.Settings["build"] = "Debug; not suitable for performance comparison";
#else
        metadata.Settings["build"] = "Release";
#endif
        await Emit("metadata", metadata);
        var prepared = false;
        foreach (var (phase, operation) in WorkloadPhases.Units(workload))
        {
            token.ThrowIfCancellationRequested();
            var setup = "custom/setup/" + (operation ?? "mix") + "/";
            if (!prepared || workload.ResetBetweenOperations)
            {
                await Emit("log", new { message = "Подготовка собственного набора · " + WorkloadPhases.Name(phase, workload) });
                await SetupAsync(setup + "schema", () => database.ResetAsync(token));
                var loaded = 0L;
                await SetupAsync(setup + "load", async () =>
                {
                    var last = Stopwatch.GetTimestamp();
                    loaded = await database.LoadAsync((current, total) =>
                    {
                        if (Stopwatch.GetElapsedTime(last).TotalSeconds < 1) return;
                        last = Stopwatch.GetTimestamp();
                        Emit("progress", new { phase = setup + "load", loaded = current, total }).GetAwaiter().GetResult();
                    }, token);
                }, () => loaded);
                await SetupAsync(setup + "indexes", () => database.PrepareAsync(token)); prepared = true;
            }
            if (workload.Checks.Any(check => check.Before)) await SetupAsync(setup + "before", () => database.CheckAsync(true, token));
            database.SelectedOperation = operation;
            if (_config.WarmupSeconds > 0 && workload.ActiveOperations.Any(candidate => candidate.ReadOnly && (operation is null || candidate.Id == operation)))
            {
                await Emit("log", new { message = "Прогрев разрешёнными операциями чтения; исключён из измерений" });
                database.WarmupOnly = true;
                try
                {
                    var clients = _config.Plan?.Stages[0].Clients ?? run.Clients;
                    await using var warmup = new SessionPool(clients);
                    await warmup.EnsureAsync(database, clients, token);
                    var measured = await MeasureWindowAsync(warmup, Scenario.PointRead, clients, _config.WarmupSeconds, null, null, token, false, phase);
                    if (measured.Errors > 0) throw new InvalidDataException("Ошибки при прогреве своего сценария: " + string.Join("; ", measured.ErrorExamples.Keys));
                }
                finally { database.WarmupOnly = false; }
            }
            await using var sessions = new SessionPool(run.Clients);
            var stages = _config.Plan?.Stages ?? [new("Нагрузка", run.Clients, _config.DurationSeconds, _config.LoadModel == LoadModel.ConstantRate ? _config.TargetOpsPerSecond : null)];
            for (var index = 0; index < stages.Count; index++)
            {
                var stage = stages[index]; token.ThrowIfCancellationRequested();
                await sessions.EnsureAsync(database, stage.Clients, token);
                var stamp = _config.Plan is null ? null : new LoadStageResult(index + 1, stage.Name, stage.Seconds, stage.OperationsPerSecond);
                await Emit("phase_start", new PhaseStart(phase, stage.Seconds, stamp, stage.Clients));
                var result = await MeasureWindowAsync(sessions, Scenario.PointRead, stage.Clients, stage.Seconds, stage.OperationsPerSecond, stamp, token, true, phase);
                await Emit("phase_end", new { });
                database.Completed += result.SuccessfulOperations;
                foreach (var detail in result.OperationBreakdown ?? []) database.CompletedByOperation[detail.Id] = database.CompletedByOperation.GetValueOrDefault(detail.Id) + detail.SuccessfulOperations;
                // Integrity checks run outside query timing after each measured window.
                var verified = result.SuccessfulOperations > 0 && !token.IsCancellationRequested;
                if (workload.Checks.Any(check => check.After) && !token.IsCancellationRequested)
                {
                    try { await database.CheckAsync(false, token); }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        await Emit("result", result with { Verified = false });
                        await Emit("result", Base(setup + "after") with { Unit = "seconds", Errors = 1, Verified = false, ErrorExamples = new() { [error.Message] = 1 } });
                        throw;
                    }
                }
                await Emit("result", result with { Verified = verified, Cancelled = token.IsCancellationRequested });
                token.ThrowIfCancellationRequested();
            }
        }

        async Task SetupAsync(string phase, Func<Task> action, Func<long>? count = null)
        {
            await Emit("phase_start", new PhaseStart(phase, 0)); var start = Stopwatch.GetTimestamp();
            try
            {
                await action(); var elapsed = Math.Max(Stopwatch.GetElapsedTime(start).TotalSeconds, .000001);
                await Emit("phase_end", new { });
                await Emit("result", Base(phase) with { Unit = count is null ? "seconds" : "rows/s", ElapsedSeconds = elapsed,
                    SuccessfulOperations = 1, SuccessfulRows = count?.Invoke() ?? 0, RowsPerSecond = count is null ? null : count() / elapsed, Verified = true });
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                await Emit("phase_end", new { });
                await Emit("result", Base(phase) with { Unit = count is null ? "seconds" : "rows/s", ElapsedSeconds = Stopwatch.GetElapsedTime(start).TotalSeconds,
                    Errors = 1, ErrorExamples = new() { [error.Message] = 1 }, Verified = false });
                throw;
            }
        }
    }
}
