using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BenchForge.Contracts;
using BenchForge.Coordinator;
using BenchForge.Worker.CSharp;
using BenchForge.Worker.CSharp.Databases;
using Xunit;

namespace BenchForge.Tests;

public sealed class DataAndMetricsTests
{
    [Fact] public void SplitMixMatchesIndependentGoldenVector()
    {
        var random = new PortableRandom(0);
        Assert.Equal(0xe220a8397b1dcdafUL, random.Next());
        Assert.Equal(0x6e789e6aa1b965f4UL, random.Next());
        Assert.Equal(0x06c45d188009454fUL, random.Next());
    }
    [Fact] public void DatasetIsDeterministicAndAscii()
    {
        var batch = DataGenerator.Batch(1, 123, 42, 511);
        Assert.Equal(Enumerable.Range(1, 123).Select(x => (long)x), batch.Select(x => x.Id));
        Assert.All(batch, row => { Assert.Equal(511, Encoding.UTF8.GetByteCount(row.Payload)); Assert.All(row.Payload, ch => Assert.Contains(ch, DataGenerator.Alphabet)); });
        Assert.Equal(batch[52], DataGenerator.Create(53, 42, 511));
        Assert.NotEqual(batch[52], DataGenerator.Create(53, 43, 511));
    }
    [Fact] public void HistogramPercentilesHaveBoundedQuantizationAndMerge()
    {
        var first = new LatencyHistogram(); var second = new LatencyHistogram();
        for (var i = 1; i <= 100_000; i++) (i % 2 == 0 ? first : second).RecordMicroseconds(i);
        first.Merge(second); var data = first.Snapshot();
        Assert.Equal(100_000, data.Count); Assert.Equal(data.Count, data.Buckets.Values.Sum());
        Assert.InRange(data.P50Ms!.Value, 50, 50 * 1.00782 + .001);
        Assert.InRange(data.P95Ms!.Value, 95, 95 * 1.00782 + .001);
        Assert.InRange(data.P99Ms!.Value, 99, 99 * 1.00782 + .001);
        Assert.Equal(50.0005, data.MeanMs!.Value, 6);
        Assert.Null(new LatencyHistogram().Snapshot().P99Ms);
        var roundTrip = JsonSerializer.Deserialize<HistogramData>(JsonSerializer.Serialize(data, Json.Options), Json.Options)!;
        Assert.Equal(data.P99Ms, roundTrip.P99Ms);
    }
    [Theory]
    [InlineData("1.5GiB", 1610612736d)] [InlineData("2MB", 2000000d)] [InlineData("0B", 0d)] [InlineData(" 4KiB ", 4096d)]
    public void DockerUnitsAreConvertedWithoutInventingValues(string text, double expected) => Assert.Equal(expected, ResourceCollector.Bytes(text));
    [Fact] public void MissingResourceSamplesRemainNull()
    {
        var data = new ResourceCollector().Summary(0, long.MaxValue);
        Assert.Null(data.MemorySampledMaxMb); Assert.Null(data.CpuMeanPercent); Assert.Null(data.ReadMb); Assert.Equal(0, data.Samples);
    }
    [Fact] public void DockerConsoleControlSequencesDoNotBreakMetrics()
    {
        var sample = ResourceCollector.ParseSample("\u001b[2J\u001b[H{\"MemUsage\":\"256MiB / 2GiB\",\"CPUPerc\":\"125.50%\",\"BlockIO\":\"1MiB / 2MiB\"}", 123)!;
        Assert.Equal(123, sample.TimestampTicks); Assert.Equal(256, sample.MemoryMb); Assert.Equal(125.5, sample.CpuPercent);
        Assert.Equal(1, sample.ReadMb); Assert.Equal(2, sample.WrittenMb);
        Assert.Null(ResourceCollector.ParseSample("\u001b[H", 123));
    }
    [Fact] public void InvalidConfigurationsFailBeforeStartingProcesses()
    {
        foreach (var config in new[] { new BenchmarkConfig { DurationSeconds = double.NaN }, new BenchmarkConfig { Targets = [] },
            new BenchmarkConfig { Clients = [1, 1] }, new BenchmarkConfig { DatasetRows = [99] }, new BenchmarkConfig { PayloadBytes = 0 },
            new BenchmarkConfig { Targets = [(TargetKind)999] } }) Assert.Throws<ArgumentException>(config.Validate);
    }

    [Theory]
    [InlineData(5000)]
    [InlineData(100_000)]
    [InlineData(int.MaxValue)]
    public void LargeClientCountsAreAcceptedInEveryLoadProfile(int clients)
    {
        new BenchmarkConfig { Clients = [clients] }.Validate();
        foreach (var profile in new[] { LoadProfile.Smoke, LoadProfile.Steady, LoadProfile.FixedRate })
            LoadPlan.Single(profile, clients, 1, profile == LoadProfile.FixedRate ? 100 : null).Validate();
        var ramp = LoadPlan.Ramp(1, clients, 5, 1);
        ramp.Validate();
        Assert.Equal(clients, ramp.PeakClients);
        Assert.Equal(clients, ramp.Stages[^1].Clients);
        LoadPlan.Spike(1, clients, 1, 1, 1).Validate();
        LoadPlan.Soak(clients, 2, 2).Validate();
        var graph = LoadPlan.FromGraph(new([new(0, 1, Curve: LoadCurve.Linear), new(4, clients)], 1));
        graph.Validate();
        Assert.Equal(clients, graph.PeakClients);
    }
    [Fact] public void CapabilitiesDistinguishDatabaseFamiliesAndNativeEngines()
    {
        Assert.Equal(9, Catalog.Targets.Length); Assert.Equal(6, Engines.All.Length);
        Assert.All(Engines.All, e => Assert.Equal("implemented", e.Status));
        Assert.DoesNotContain(Scenario.Aggregate, Catalog.Get(TargetKind.Redis).Scenarios);
        Assert.DoesNotContain(Scenario.Update, Catalog.Get(TargetKind.ClickHouse).Scenarios);
        Assert.Contains(Scenario.BatchInsert, Catalog.Get(TargetKind.ClickHouse).Scenarios);
    }
    [Fact] public void SummaryExcludesErrorsAndCancellationAndKeepsBatchUnits()
    {
        var row = new PhaseResult { Phase = "BatchInsert", Unit = "rows/s", RowsPerSecond = 1000, OperationsPerSecond = 1, Verified = true };
        var summary = ReportStore.Summarize([row, row with { RowsPerSecond = 3000 }, row with { RowsPerSecond = 999999, Errors = 1 }, row with { Cancelled = true }]);
        Assert.Equal(2, summary.Single().ValidRepeats); Assert.Equal(2000, summary[0].MedianRate); Assert.Equal("rows/s", summary[0].Unit);
    }
}

public sealed class EngineTests
{
    private sealed class FakeDatabase(bool fail = false, int delay = 0) : IDatabase
    {
        public Task InitializeAsync(CancellationToken t) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken t) => Task.CompletedTask;
        public Task LoadBatchAsync(DataRow[] rows, CancellationToken t) => Task.CompletedTask;
        public Task PrepareAsync(CancellationToken t) => Task.CompletedTask;
        public Task<DatasetDigest> DigestAsync(CancellationToken t) => Task.FromResult(new DatasetDigest(100, 5050, 0));
        public Task<long> CountAsync(CancellationToken t) => Task.FromResult(100L);
        public Task<EngineMetadata> MetadataAsync(CancellationToken t) => throw new NotSupportedException();
        public Task<IClientSession> OpenSessionAsync(CancellationToken t) => Task.FromResult<IClientSession>(new Session(fail, delay));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private sealed class Session(bool fail, int delay) : IClientSession
        {
            public async ValueTask<long> ExecuteAsync(Scenario scenario, long key, DataRow? row, DataRow[]? batch, CancellationToken t)
            { if (delay > 0) await Task.Delay(delay, t); if (fail) throw new InvalidOperationException("injected request failure"); return 1; }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
    private static RunCase Case(BenchmarkConfig config) => new("test", TargetKind.Sqlite, 100, config.Clients[0], 1, config, new("", 0, "", "", "", "unused"));
    [Fact] public async Task FailedRequestsNeverIncreaseSuccessfulThroughput()
    {
        var config = new BenchmarkConfig { DatasetRows = [100], Clients = [2], DurationSeconds = .2 };
        var engine = new BenchmarkEngine(Case(config), new ProtocolWriter(TextWriter.Null));
        var result = await engine.MeasureAsync(new FakeDatabase(fail: true), Scenario.PointRead, .2, CancellationToken.None, false);
        Assert.Equal(0, result.SuccessfulOperations); Assert.Equal(100, result.Errors); Assert.Equal(0, result.OperationsPerSecond);
        Assert.Null(result.ServiceLatency!.P99Ms); Assert.Equal(100, result.ErrorLatency!.Count);
    }
    [Fact] public async Task ConstantRateAccountsForOverloadAndEndToEndLatency()
    {
        var config = new BenchmarkConfig { DatasetRows = [100], Clients = [1], LoadModel = LoadModel.ConstantRate, TargetOpsPerSecond = 100_000, MaxStartLagMs = 1, DurationSeconds = .15 };
        var engine = new BenchmarkEngine(Case(config), new ProtocolWriter(TextWriter.Null));
        var result = await engine.MeasureAsync(new FakeDatabase(delay: 3), Scenario.PointRead, .15, CancellationToken.None, false);
        Assert.True(result.Dropped > 0); Assert.True(result.SuccessfulOperations > 0);
        Assert.Equal(15000, result.PlannedOperations); Assert.Equal(result.PlannedOperations, result.SuccessfulOperations + result.Errors + result.Dropped);
        Assert.True(result.EndToEndLatency!.MeanMs >= result.ServiceLatency!.MeanMs);
    }

    [Fact]
    public async Task EngineRunsWithMoreThan2048Clients()
    {
        var config = new BenchmarkConfig { DatasetRows = [100], Clients = [2500], LoadModel = LoadModel.ConstantRate,
            TargetOpsPerSecond = 200, DurationSeconds = .05 };
        config.Validate();
        var engine = new BenchmarkEngine(Case(config) with { Target = TargetKind.Redis }, new ProtocolWriter(TextWriter.Null));
        var result = await engine.MeasureAsync(new FakeDatabase(), Scenario.PointRead, config.DurationSeconds, CancellationToken.None, false);
        Assert.Equal(2500, result.Clients);
        Assert.Equal(10, result.PlannedOperations);
        Assert.Equal(result.PlannedOperations, result.SuccessfulOperations + result.Errors + result.Dropped);
        Assert.Equal(0, result.Errors);
    }
}

public sealed class ProcessIntegrationTests
{
    private sealed class Folder : IDisposable
    {
        private readonly string _root = Path.GetFullPath(Environment.GetEnvironmentVariable("DBLAB_TEST_ROOT") ?? Path.GetTempPath());
        public string PathValue { get; }
        public Folder() { Directory.CreateDirectory(_root); PathValue = Path.Combine(_root, "dblab-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(PathValue); }
        public void Dispose()
        {
            var full = Path.GetFullPath(PathValue);
            if (Path.GetDirectoryName(full) != _root.TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(full).StartsWith("dblab-tests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected test cleanup path.");
            Directory.Delete(full, recursive: true);
        }
    }
    [Fact] public async Task SeparateWorkerRunsEverySqliteScenarioAndExportsVerifiedReports()
    {
        using var folder = new Folder();
        var config = new BenchmarkConfig { DatasetRows = [100], Clients = [1, 2], Repeats = 1, DurationSeconds = .1, WarmupSeconds = .03, BatchSize = 20 };
        var result = await new ExperimentRunner().RunAsync(config, folder.PathValue, CancellationToken.None);
        Assert.Equal("completed", result.Report.Status); Assert.Empty(result.Report.Failures);
        Assert.Equal(20, result.Report.Results.Count); Assert.All(result.Report.Results, r => Assert.True(r.Verified));
        Assert.All(result.Report.Results, r => Assert.Null(r.Server));
        foreach (var row in result.Report.Results.Where(r => r.ServiceLatency is not null))
        { Assert.Equal(row.SuccessfulOperations, row.ServiceLatency!.Count); Assert.NotNull(row.Client); }
        var saved = await File.ReadAllTextAsync(Path.Combine(result.Folder, "report.json"));
        Assert.DoesNotContain("password", saved, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("completed", JsonSerializer.Deserialize<BenchmarkReport>(saved, Json.Options)!.Status);
        Assert.Equal(21, (await File.ReadAllLinesAsync(Path.Combine(result.Folder, "results.csv"))).Length);
        Assert.Empty(Directory.GetFiles(result.Folder, "*.sqlite3*")); Assert.Empty(Directory.GetFiles(result.Folder, "*.tmp"));
    }
    [Fact]
    public async Task SpikeKeepsOneDatabaseAndSeparatesBaselineFromRecovery()
    {
        using var folder = new Folder();
        var config = new BenchmarkConfig
        {
            DatasetRows = [100], Clients = [2], Scenarios = [Scenario.Insert], Repeats = 1, WarmupSeconds = 0,
            Plan = LoadPlan.Spike(1, 2, .2, .2, .2)
        };
        var (report, path) = await new ExperimentRunner().RunAsync(config, folder.PathValue, CancellationToken.None);
        Assert.Equal("completed", report.Status);
        Assert.Single(report.Engines);
        Assert.Single(report.Results, row => row.Phase == "bulk_load");
        var stageRows = report.Results.Where(row => row.Stage is not null).ToArray();
        Assert.Equal(new[] { 1, 2, 1 }, stageRows.Select(row => row.Clients));
        Assert.Equal(new[] { 1, 2, 3 }, stageRows.Select(row => row.Stage!.Number));
        Assert.All(stageRows, row => { Assert.True(row.Verified); Assert.True(row.SuccessfulRows > 0); });
        Assert.Equal(3, report.Summary.Count(row => row.Stage is not null));

        var recoveredSlice = new ReportSlice(nameof(Scenario.Insert), 100, 1, 3);
        var recovery = Assert.Single(ReportAnalysis.Compare(report, report.Results, recoveredSlice, AnalysisMetric.Rate));
        Assert.Equal(stageRows[2].OperationsPerSecond, recovery.Median);
        var pathPoints = Assert.Single(ReportAnalysis.StageTrends(report.Results, recoveredSlice, AnalysisMetric.Rate)).Points;
        Assert.Equal(new double[] { 1, 2, 3 }, pathPoints.Select(point => point.X));
        Assert.Contains("stage_number", (await File.ReadAllLinesAsync(Path.Combine(path, "results.csv")))[0]);
    }

    [Fact]
    public async Task FixedRatePlanAccountsForEachScheduledOperation()
    {
        using var folder = new Folder();
        var config = new BenchmarkConfig { DatasetRows = [100], Repeats = 1, WarmupSeconds = 0,
            Plan = LoadPlan.Single(LoadProfile.FixedRate, 2, .4, 250) };
        var result = await new ExperimentRunner().RunAsync(config, folder.PathValue, CancellationToken.None);
        var measured = Assert.Single(result.Report.Results, row => row.Stage is not null);
        Assert.Equal(100, measured.PlannedOperations);
        Assert.Equal(250, measured.Stage!.OperationsPerSecond);
        Assert.Equal(100, measured.SuccessfulOperations + measured.Errors + measured.Dropped);
        Assert.NotNull(measured.EndToEndLatency?.P99Ms);
    }

    [Fact]
    public void GeneratedProfilesRespectLimitsAndPlannedDuration()
    {
        var ramp = LoadPlan.Ramp(2, 32, 5, 10);
        Assert.Equal(new[] { 2, 10, 17, 24, 32 }, ramp.Stages.Select(stage => stage.Clients));
        Assert.Equal(50, ramp.Seconds);
        Assert.Throws<ArgumentException>(() => LoadPlan.Ramp(1, 3, 5, 1));
        Assert.Throws<ArgumentException>(() => LoadPlan.Spike(4, 4, 1, 1, 1));
        var soak = LoadPlan.Soak(4, 600, 6);
        soak.Validate();
        Assert.Equal(600, soak.Seconds);
        Assert.All(soak.Stages, stage => { Assert.Equal(4, stage.Clients); Assert.Equal(100, stage.Seconds); });
    }

    [Fact]
    public void GraphCompilesCurvesAndRejectsInconsistentStages()
    {
        var graph = new LoadGraph([new(0, 4, 20, LoadCurve.EaseIn, 4), new(20, 20, 100), new(25, 4)], 10);
        var plan = LoadPlan.FromGraph(graph);
        Assert.Equal(new[] { 4, 5, 8, 13, 20, 4 }, plan.Stages.Select(stage => stage.Clients));
        Assert.Equal(new int?[] { 20, 25, 40, 65, 100, null }, plan.Stages.Select(stage => stage.OperationsPerSecond));
        Assert.Equal(35, plan.Seconds);
        Assert.Throws<ArgumentException>(() => (plan with { Graph = graph with { FinalSeconds = 9 } }).Validate());
        Assert.Throws<ArgumentException>(() => LoadPlan.FromGraph(new([new(0, 1), new(0, 2)], 1)));
        Assert.Throws<ArgumentException>(() => LoadPlan.FromGraph(new([new(0, 1, Curve: LoadCurve.Linear), new(.1, 2)], 1)));
    }

    [Fact]
    public async Task CustomGraphRunsCompiledStagesAndPersistsEditablePoints()
    {
        using var folder = new Folder();
        var graph = new LoadGraph([new(0, 1, 60, LoadCurve.Linear, 2), new(.4, 3, 100), new(.6, 1)], .2);
        var config = new BenchmarkConfig { DatasetRows = [100], Repeats = 1, WarmupSeconds = 0, Plan = LoadPlan.FromGraph(graph) };
        var result = await new ExperimentRunner().RunAsync(config, folder.PathValue, CancellationToken.None);
        Assert.Equal("completed", result.Report.Status);
        var measured = result.Report.Results.Where(row => row.Stage is not null).ToArray();
        Assert.Equal(new[] { 1, 2, 3, 1 }, measured.Select(row => row.Clients));
        Assert.Equal(new int?[] { 60, 80, 100, null }, measured.Select(row => row.Stage!.OperationsPerSecond));
        Assert.Equal(new long[] { 12, 16, 20 }, measured.Take(3).Select(row => row.PlannedOperations));
        var saved = JsonSerializer.Deserialize<BenchmarkReport>(await File.ReadAllTextAsync(Path.Combine(result.Folder, "report.json")), Json.Options)!;
        saved.Config.Validate();
        Assert.Equal(graph.Points.ToArray(), saved.Config.Plan!.Graph!.Points.ToArray());
        Assert.Equal(graph.FinalSeconds, saved.Config.Plan.Graph.FinalSeconds);
    }

    [Fact]
    public void CustomGraphAllowsMoreThanTwentyFourTransitionSteps()
    {
        var graph = new LoadGraph([new(0, 4, Curve: LoadCurve.Linear, Steps: 48), new(24, 52)], 1);
        var plan = LoadPlan.FromGraph(graph);
        Assert.Equal(49, plan.Stages.Count);
        Assert.Equal(Enumerable.Range(4, 49), plan.Stages.Select(stage => stage.Clients));
        Assert.Equal(25, plan.Seconds);
        var tooShort = graph with { Points = [graph.Points[0] with { Steps = 481 }, graph.Points[1]] };
        Assert.Contains("0,05", Assert.Throws<ArgumentException>(() => LoadPlan.FromGraph(tooShort)).Message);
    }

    [Fact]
    public async Task CustomGraphRunsAndSavesMoreThanTwentyFourPoints()
    {
        using var folder = new Folder();
        var graph = new LoadGraph(Enumerable.Range(0, 30).Select(i => new LoadPoint(i * .05, 1)).ToArray(), .05);
        var config = new BenchmarkConfig { DatasetRows = [100], Repeats = 1, WarmupSeconds = 0, Plan = LoadPlan.FromGraph(graph) };
        var result = await new ExperimentRunner().RunAsync(config, folder.PathValue, CancellationToken.None);
        Assert.Equal("completed", result.Report.Status);
        var measured = result.Report.Results.Where(row => row.Stage is not null).ToArray();
        Assert.Equal(30, measured.Length);
        Assert.Equal(Enumerable.Range(1, 30), measured.Select(row => row.Stage!.Number));
        Assert.All(measured, row => Assert.True(row.Verified));
        var saved = JsonSerializer.Deserialize<BenchmarkReport>(await File.ReadAllTextAsync(Path.Combine(result.Folder, "report.json")), Json.Options)!;
        saved.Config.Validate();
        Assert.Equal(graph.Points.ToArray(), saved.Config.Plan!.Graph!.Points.ToArray());
    }
    [Fact] public async Task CancellationSavesPartialReportAndCleansOwnedDatabase()
    {
        using var folder = new Folder(); using var cancel = new CancellationTokenSource();
        var runner = new ExperimentRunner(u => { if (u.Type == "result") cancel.Cancel(); });
        var config = new BenchmarkConfig { DatasetRows = [100], Clients = [1], Repeats = 1, DurationSeconds = .5, WarmupSeconds = 0 };
        var result = await runner.RunAsync(config, folder.PathValue, cancel.Token);
        Assert.Equal("cancelled", result.Report.Status); Assert.NotEmpty(result.Report.Results);
        Assert.True(File.Exists(Path.Combine(result.Folder, "report.json"))); Assert.Empty(Directory.GetFiles(result.Folder, "*.sqlite3*"));
    }
    [Fact] public async Task UnsupportedProtocolVersionFailsExplicitly()
    {
        var info = ProcessUtil.Info(ProcessUtil.Dotnet(), [ProcessUtil.WorkerPath()]); info.RedirectStandardInput = true;
        using var process = Process.Start(info)!;
        await process.StandardInput.WriteLineAsync("{\"protocolVersion\":99,\"type\":\"run\"}"); process.StandardInput.Close();
        var output = await process.StandardOutput.ReadToEndAsync(); await process.WaitForExitAsync();
        Assert.NotEqual(0, process.ExitCode); Assert.Contains("\"type\":\"failed\"", output);
    }
}
