using System.Diagnostics;
using BenchForge.Contracts;
using BenchForge.Worker.CSharp.Databases;

namespace BenchForge.Worker.CSharp;

public sealed partial class BenchmarkEngine(RunCase run, ProtocolWriter writer)
{
    private readonly BenchmarkConfig _config = run.Config;
    private Task Emit(string type, object value) => writer.EmitAsync(type, run.RunId, value);
    private PhaseResult Base(string phase) => new() { Target = run.Target, DatasetRows = run.Rows, Clients = run.Clients, Repeat = run.Repeat, Phase = phase };

    public async Task RunAsync(CancellationToken token)
    {
        if (_config.CustomWorkload is not null) { await RunCustomAsync(token); return; }
        await using var database = await OpenReadyAsync(token);
        var metadata = await database.MetadataAsync(token);
        metadata.Settings["server_gc"] = System.Runtime.GCSettings.IsServerGC.ToString();
        metadata.Settings["gc_latency_mode"] = System.Runtime.GCSettings.LatencyMode.ToString();
        metadata.Settings["effective_batch_size"] = _config.EffectiveBatchSize.ToString();
#if DEBUG
        metadata.Settings["build"] = "Debug; not suitable for performance comparison";
#else
        metadata.Settings["build"] = "Release";
#endif
        await Emit("metadata", metadata);
        await Emit("log", new { message = "Создание тестового набора данных" });
        await database.ResetAsync(token);
        await Emit("phase_start", new PhaseStart("bulk_load", 0));
        var loadStart = Stopwatch.GetTimestamp();
        double generationSeconds = 0;
        long amountSum = 0, loaded = 0;
        var nextProgress = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
        for (long first = 1; first <= run.Rows; first += _config.EffectiveBatchSize)
        {
            token.ThrowIfCancellationRequested();
            var generationStart = Stopwatch.GetTimestamp();
            var rows = DataGenerator.Batch(first, (int)Math.Min(_config.EffectiveBatchSize, run.Rows - first + 1), _config.Seed, _config.PayloadBytes);
            generationSeconds += Stopwatch.GetElapsedTime(generationStart).TotalSeconds;
            await database.LoadBatchAsync(rows, token);
            amountSum += rows.Sum(x => x.AmountCents); loaded += rows.Length;
            if (Stopwatch.GetTimestamp() >= nextProgress)
            { await Emit("progress", new { phase = "bulk_load", loaded, total = run.Rows }); nextProgress = Stopwatch.GetTimestamp() + Stopwatch.Frequency; }
        }
        var loadSeconds = Stopwatch.GetElapsedTime(loadStart).TotalSeconds;
        token.ThrowIfCancellationRequested();
        var expectedDigest = new DatasetDigest(run.Rows, checked(run.Rows * (run.Rows + 1) / 2), amountSum);
        var actualDigest = await database.DigestAsync(token);
        if (actualDigest != expectedDigest) throw new InvalidDataException($"Проверка данных: {actualDigest} != {expectedDigest}");
        await Emit("result", Base("bulk_load") with { Unit = "rows/s", ElapsedSeconds = loadSeconds, SuccessfulRows = run.Rows,
            SuccessfulOperations = (long)Math.Ceiling((double)run.Rows / _config.EffectiveBatchSize), RowsPerSecond = run.Rows / loadSeconds,
            GenerationSeconds = generationSeconds, Verified = true });
        token.ThrowIfCancellationRequested();
        if (run.Target is not (TargetKind.Redis or TargetKind.Valkey or TargetKind.ClickHouse))
        {
            await Emit("phase_start", new PhaseStart("index_build", 0));
            var indexStart = Stopwatch.GetTimestamp(); await database.PrepareAsync(token);
            await Emit("result", Base("index_build") with { Unit = "seconds", ElapsedSeconds = Stopwatch.GetElapsedTime(indexStart).TotalSeconds,
                SuccessfulOperations = 1, Verified = true });
        }
        var capabilities = Catalog.Get(run.Target).Scenarios;
        var requested = _config.Scenarios.Count == 0 ? capabilities : _config.Scenarios.ToArray();
        foreach (var unsupported in requested.Except(capabilities)) await Emit("unsupported", new { scenario = unsupported, message = "Сценарий не относится к этому типу СУБД" });
        var scenarios = _config.Operations(run.Target);
        if (_config.WarmupSeconds > 0)
        {
            await Emit("log", new { message = "Прогрев чтением; прогрев исключён из результатов" });
            var operation = capabilities.Contains(Scenario.PointRead) ? Scenario.PointRead : Scenario.RangeRead;
            var warmup = _config.Plan is { } plan
                ? await WarmupAsync(database, operation, plan.Stages[0].Clients, token)
                : await MeasureAsync(database, operation, _config.WarmupSeconds, token, false);
            if (warmup.Errors > 0) throw new InvalidDataException("При прогреве получены ошибки.");
        }
        long expectedCount = run.Rows;
        foreach (var scenario in scenarios)
        {
            token.ThrowIfCancellationRequested();
            if (_config.Plan is not { } plan)
            {
                await Emit("phase_start", new PhaseStart(scenario.ToString(), _config.DurationSeconds));
                var result = await MeasureAsync(database, scenario, _config.DurationSeconds, token, true);
                await VerifyAndEmitAsync(scenario, result, false);
                continue;
            }

            await using var sessions = new SessionPool(run.Clients);
            for (var index = 0; index < plan.Stages.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var stage = plan.Stages[index];
                await Emit("log", new { message = $"{stage.Name} · подготовка до {stage.Clients} подключений" });
                await sessions.EnsureAsync(database, stage.Clients, token);
                var stamp = new LoadStageResult(index + 1, stage.Name, stage.Seconds, stage.OperationsPerSecond);
                await Emit("phase_start", new PhaseStart(scenario.ToString(), stage.Seconds, stamp, stage.Clients));
                var result = await MeasureWindowAsync(sessions, scenario, stage.Clients, stage.Seconds, stage.OperationsPerSecond, stamp, token, true);
                await Emit("phase_end", new { });
                await VerifyAndEmitAsync(scenario, result, true);
            }
        }

        async Task VerifyAndEmitAsync(Scenario scenario, PhaseResult result, bool staged)
        {
            if (scenario is Scenario.Insert or Scenario.BatchInsert) expectedCount += result.SuccessfulRows;
            if (scenario == Scenario.Delete) expectedCount -= result.SuccessfulRows;
            var actualCount = await database.CountAsync(CancellationToken.None);
            var verified = actualCount == expectedCount && result.SuccessfulOperations > 0 && !token.IsCancellationRequested;
            result = result with { Verified = verified, Cancelled = token.IsCancellationRequested };
            await Emit("result", result);
            if (actualCount != expectedCount) throw new InvalidDataException($"Число записей после {scenario}: ожидалось {expectedCount}, получено {actualCount}. Возможен неопределённый исход записи.");
            if (!staged && result.SuccessfulOperations == 0 && !token.IsCancellationRequested) throw new InvalidDataException($"Нет успешных операций в {scenario}.");
        }
    }

    private async Task<PhaseResult> WarmupAsync(IDatabase database, Scenario scenario, int clients, CancellationToken token)
    {
        await using var sessions = new SessionPool(clients);
        await sessions.EnsureAsync(database, clients, token);
        return await MeasureWindowAsync(sessions, scenario, clients, _config.WarmupSeconds, null, null, token, false);
    }

    private async Task<IDatabase> OpenReadyAsync(CancellationToken token)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(90);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var database = DatabaseFactory.Create(run);
            try { await database.InitializeAsync(token); return database; }
            catch when (run.Target != TargetKind.Sqlite && !token.IsCancellationRequested && DateTimeOffset.UtcNow < until)
            { await database.DisposeAsync(); await Task.Delay(500, token); }
            catch { await database.DisposeAsync(); throw; }
        }
    }

    private sealed class Counters
    {
        public long Success, Rows, Errors, Dropped, FinishedAt;
        public readonly LatencyHistogram Service = new();
        public readonly LatencyHistogram EndToEnd = new();
        public readonly LatencyHistogram Failed = new();
        public readonly Dictionary<string, long> ErrorExamples = [];
        public readonly Dictionary<string, Counters> Operations = [];
    }

    private sealed class SessionPool(int capacity) : IAsyncDisposable
    {
        public IClientSession?[] Sessions { get; } = new IClientSession?[capacity];
        public long[] Sequences { get; } = new long[capacity];
        public long LastDeletedId;
        private int _opened;

        public async Task EnsureAsync(IDatabase database, int count, CancellationToken token)
        {
            if (_opened >= count) return;
            // Limit the connection handshake burst while preparing every requested client.
            await Parallel.ForEachAsync(Enumerable.Range(_opened, count - _opened),
                new ParallelOptions { MaxDegreeOfParallelism = 32, CancellationToken = token },
                async (index, cancellation) => { Sessions[index] = await database.OpenSessionAsync(cancellation); });
            _opened = count;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var session in Sessions) if (session is not null) await session.DisposeAsync();
        }
    }

    public async Task<PhaseResult> MeasureAsync(IDatabase database, Scenario scenario, double seconds, CancellationToken token, bool progress)
    {
        await using var sessions = new SessionPool(run.Clients);
        await sessions.EnsureAsync(database, run.Clients, token);
        int? rate = _config.LoadModel == LoadModel.ConstantRate ? _config.TargetOpsPerSecond : null;
        return await MeasureWindowAsync(sessions, scenario, run.Clients, seconds, rate, null, token, progress);
    }

    private async Task<PhaseResult> MeasureWindowAsync(SessionPool sessions, Scenario scenario, int clients, double seconds, int? targetRate,
        LoadStageResult? stage, CancellationToken token, bool progress, string? phaseName = null)
    {
        ThreadPool.GetMinThreads(out var workerThreads, out var ioThreads);
        if (run.Target == TargetKind.Sqlite) ThreadPool.SetMinThreads(Math.Max(workerThreads, Math.Min(252, clients) + 4), ioThreads);
        var counters = Enumerable.Range(0, clients).Select(_ => new Counters()).ToArray();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;
        long start = 0, deadline = 0;
        var rate = targetRate.GetValueOrDefault();
        var planned = (long)Math.Floor(seconds * rate);
        var constant = targetRate.HasValue;
        var maxLagTicks = _config.MaxStartLagMs * Stopwatch.Frequency / 1000;
        var tasks = Enumerable.Range(0, clients).Select(index => Task.Run(async () =>
        {
            var c = counters[index];
            if (Interlocked.Increment(ref readyCount) == clients) ready.SetResult();
            await gate.Task;
            var rng = new PortableRandom(_config.Seed ^ ((ulong)index + 1) * 104729UL ^ (ulong)run.Repeat ^ (ulong)(stage?.Number ?? 0) * 32452843UL);
            long sequence = sessions.Sequences[index], slot = index;
            while (!token.IsCancellationRequested && Stopwatch.GetTimestamp() < deadline)
            {
                var scheduled = Stopwatch.GetTimestamp();
                if (constant)
                {
                    if (slot >= planned) break;
                    scheduled = start + (long)(slot * (double)Stopwatch.Frequency / rate);
                    var now = Stopwatch.GetTimestamp();
                    if (now - scheduled > maxLagTicks)
                    {
                        var skipped = Math.Max(1L, (long)Math.Floor((now - scheduled - maxLagTicks) * rate / Stopwatch.Frequency / clients) + 1);
                        skipped = Math.Min(skipped, (planned - 1 - slot) / clients + 1);
                        c.Dropped += skipped; slot += skipped * clients; continue;
                    }
                    while (Stopwatch.GetTimestamp() < scheduled && !token.IsCancellationRequested)
                        await Task.Delay(1, CancellationToken.None);
                    if (token.IsCancellationRequested || Stopwatch.GetTimestamp() >= deadline) break;
                    if (Stopwatch.GetTimestamp() - scheduled > maxLagTicks) { c.Dropped++; slot += clients; continue; }
                }
                var operation = scenario == Scenario.Mixed ? (rng.Next() % 100 < 80 ? Scenario.PointRead : Scenario.Update) : scenario;
                var key = rng.Key(run.Rows, _config.Hotspot);
                DataRow? row = null; DataRow[]? batch = null;
                if (operation is Scenario.Insert or Scenario.BatchInsert)
                {
                    var size = operation == Scenario.BatchInsert ? _config.EffectiveBatchSize : 1;
                    var first = ((long)scenario + 1) * 1_000_000_000_000L + (sequence * run.Clients + index) * size + 1;
                    if (operation == Scenario.Insert) row = DataGenerator.Create(first, _config.Seed, _config.PayloadBytes);
                    else batch = DataGenerator.Batch(first, size, _config.Seed, _config.PayloadBytes);
                }
                if (operation == Scenario.Delete)
                {
                    key = stage is null ? sequence * clients + index + 1 : Interlocked.Increment(ref sessions.LastDeletedId);
                    if (key > run.Rows) break;
                }
                var before = Stopwatch.GetTimestamp();
                var stopAfterError = false;
                try
                {
                    var affected = await sessions.Sessions[index]!.ExecuteAsync(operation, key, row, batch, sessions.Sessions[index] is ICustomSqlSession ? token : CancellationToken.None);
                    var finish = Stopwatch.GetTimestamp();
                    c.Success++; c.Rows += affected; c.Service.RecordTicks(finish - before);
                    c.EndToEnd.RecordTicks(finish - (constant ? scheduled : before));
                    if (sessions.Sessions[index] is ICustomSqlSession custom)
                    {
                        if (!c.Operations.TryGetValue(custom.LastOperation, out var detail)) c.Operations[custom.LastOperation] = detail = new();
                        detail.Success++; detail.Rows += affected; detail.Service.RecordTicks(finish - before); detail.EndToEnd.RecordTicks(finish - (constant ? scheduled : before));
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested && sessions.Sessions[index] is ICustomSqlSession) { break; }
                catch (Exception exception)
                {
                    c.Errors++; c.Failed.RecordTicks(Stopwatch.GetTimestamp() - before);
                    if (sessions.Sessions[index] is ICustomSqlSession custom)
                    {
                        if (!c.Operations.TryGetValue(custom.LastOperation, out var detail)) c.Operations[custom.LastOperation] = detail = new();
                        detail.Errors++;
                    }
                    var error = exception.GetType().Name + ": " + exception.Message;
                    if (!string.IsNullOrEmpty(run.Endpoint.Password)) error = error.Replace(run.Endpoint.Password, "[redacted]");
                    error = error[..Math.Min(error.Length, 250)];
                    c.ErrorExamples[error] = c.ErrorExamples.GetValueOrDefault(error) + 1;
                    if (c.Errors >= 50) stopAfterError = true;
                }
                sequence++; slot += clients;
                if (stopAfterError) break;
            }
            if (constant && slot < planned && !token.IsCancellationRequested) c.Dropped += (planned - 1 - slot) / clients + 1;
            sessions.Sequences[index] = sequence;
            c.FinishedAt = Stopwatch.GetTimestamp();
        })).ToArray();
        await ready.Task;
        using var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var allocatedBefore = GC.GetTotalAllocatedBytes(false);
        var collections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
        var pauseBefore = GC.GetTotalPauseDuration();
        var sampledRss = process.WorkingSet64 / 1048576.0;
        start = Stopwatch.GetTimestamp(); deadline = start + (long)(seconds * Stopwatch.Frequency);
        gate.SetResult();
        using var monitorCancellation = new CancellationTokenSource();
        var monitor = Task.Run(async () =>
        {
            var reportAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
            try
            {
                while (!monitorCancellation.IsCancellationRequested)
                {
                    await Task.Delay(250, monitorCancellation.Token); process.Refresh(); sampledRss = Math.Max(sampledRss, process.WorkingSet64 / 1048576.0);
                    if (progress && Stopwatch.GetTimestamp() >= reportAt)
                    {
                        await Emit("progress", new { phase = phaseName ?? scenario.ToString(), stage = stage?.Name, targetSeconds = seconds, clients, successful = counters.Sum(c => Interlocked.Read(ref c.Success)),
                            errors = counters.Sum(c => Interlocked.Read(ref c.Errors)), elapsedSeconds = Stopwatch.GetElapsedTime(start).TotalSeconds });
                        reportAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
                    }
                }
            }
            catch (OperationCanceledException) { }
        });
        await Task.WhenAll(tasks);
        var end = counters.Max(c => c.FinishedAt);
        monitorCancellation.Cancel(); await monitor;
        var elapsed = Math.Max((end - start) / (double)Stopwatch.Frequency, .000001);
        process.Refresh(); sampledRss = Math.Max(sampledRss, process.WorkingSet64 / 1048576.0);
        var client = new ClientMetrics((process.TotalProcessorTime - cpuBefore).TotalSeconds / elapsed * 100,
            process.WorkingSet64 / 1048576.0, sampledRss, GC.GetTotalAllocatedBytes(false) - allocatedBefore,
            GC.CollectionCount(0) - collections[0], GC.CollectionCount(1) - collections[1], GC.CollectionCount(2) - collections[2],
            (GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds);
        var service = new LatencyHistogram(); var endToEnd = new LatencyHistogram(); var failed = new LatencyHistogram();
        foreach (var c in counters) { service.Merge(c.Service); endToEnd.Merge(c.EndToEnd); failed.Merge(c.Failed); }
        var successful = counters.Sum(c => c.Success); var errors = counters.Sum(c => c.Errors); var dropped = counters.Sum(c => c.Dropped);
        var affectedRows = counters.Sum(c => c.Rows);
        var writes = scenario is Scenario.Insert or Scenario.BatchInsert or Scenario.Update or Scenario.Delete;
        var breakdown = _config.CustomWorkload is null ? null : counters.SelectMany(counter => counter.Operations).GroupBy(pair => pair.Key).Select(group =>
        {
            var latency = new LatencyHistogram(); var endLatency = new LatencyHistogram();
            foreach (var pair in group) { latency.Merge(pair.Value.Service); endLatency.Merge(pair.Value.EndToEnd); }
            return new WorkloadOperationResult(group.Key, _config.CustomWorkload.Operations.Single(operation => operation.Id == group.Key).Name,
                group.Sum(pair => pair.Value.Success), group.Sum(pair => pair.Value.Rows), group.Sum(pair => pair.Value.Errors), latency.Snapshot(), endLatency.Snapshot());
        }).ToList();
        return Base(phaseName ?? scenario.ToString()) with
        {
            Stage = stage, Clients = clients,
            Unit = scenario == Scenario.BatchInsert ? "rows/s" : "ops/s", ElapsedSeconds = elapsed, SuccessfulOperations = successful,
            SuccessfulRows = affectedRows, Errors = errors, Dropped = dropped,
            PlannedOperations = constant && !token.IsCancellationRequested ? planned : successful + errors + dropped,
            OperationsPerSecond = successful / elapsed, RowsPerSecond = writes ? affectedRows / elapsed : null,
            ServiceLatency = service.Snapshot(), EndToEndLatency = endToEnd.Snapshot(), ErrorLatency = failed.Snapshot(), Client = client,
            ErrorExamples = counters.SelectMany(c => c.ErrorExamples).GroupBy(k => k.Key).OrderByDescending(g => g.Sum(x => x.Value)).Take(8).ToDictionary(g => g.Key, g => g.Sum(x => x.Value)),
            Cancelled = token.IsCancellationRequested, OperationBreakdown = breakdown
        };
    }
}
