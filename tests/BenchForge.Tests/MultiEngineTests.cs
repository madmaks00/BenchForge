using System.Text.Json;
using BenchForge.Contracts;
using BenchForge.Coordinator;
using Xunit;

namespace BenchForge.Tests;

public sealed class MultiEngineTests
{
    [Fact]
    public void StandardBenchmarksAllowTheCompleteDatabaseAndLanguageMatrix()
    {
        var config = new BenchmarkConfig { Targets = Catalog.Targets.Select(target => target.Kind).ToList(),
            EngineIds = Engines.All.Select(engine => engine.Id).ToList(), MemoryMb = 2560 };
        config.Validate();
        Assert.Equal(84, config.Targets.Count * config.EngineIds.Count);
        foreach (var target in config.Targets)
            Assert.NotEmpty(config.Operations(target));
    }
    [Fact]
    public void OldConfigurationsKeepCSharpAndInvalidSelectionsFailEarly()
    {
        var old = JsonSerializer.Deserialize<BenchmarkConfig>("{}", Json.Options)!;
        Assert.Equal(new[] { "csharp" }, old.EngineIds);
        foreach (var engines in new List<string>[] { [], ["csharp", "csharp"], ["unknown"], ["Go"] })
            Assert.Throws<ArgumentException>(() => (old with { EngineIds = engines }).Validate());
        (old with { EngineIds = Engines.All.Select(engine => engine.Id).ToList() }).Validate();
    }

    [Fact]
    public void ConformanceRejectsChangedDataAndHistogram()
    {
        var expected = JsonSerializer.Serialize(WorkerConformance.Vectors(), Json.Options);
        WorkerConformance.Verify(expected);
        Assert.Throws<InvalidDataException>(() => WorkerConformance.Verify(expected.Replace("e220a8397b1dcdaf", "0000000000000000")));
        Assert.Throws<InvalidDataException>(() => WorkerConformance.Verify(expected.Replace("\"count\":8", "\"count\":9")));
    }

    [Fact]
    public async Task EachLanguageAndRepeatUsesAFreshCaseAndOrderRotates()
    {
        using var folder = new Folder();
        var host = new RecordingHost();
        var config = new BenchmarkConfig { EngineIds = ["csharp", "go"], DatasetRows = [100], Clients = [1], Repeats = 2, WarmupSeconds = 0 };
        var outcome = await new ExperimentRunner(workerHost: host).RunAsync(config, folder.Path, CancellationToken.None);
        Assert.Equal("completed", outcome.Report.Status);
        Assert.Equal(new[] { "csharp", "go", "go", "csharp" }, host.Cases.Select(run => run.Engine));
        Assert.Equal(4, host.Cases.Select(run => run.RunId).Distinct().Count());
        Assert.Equal(4, host.Cases.Select(run => run.Endpoint.FilePath).Distinct().Count());
        Assert.Equal(new[] { 1, 1, 2, 2 }, outcome.Report.Results.Select(row => row.Repeat));
        Assert.Equal(4, outcome.Report.Engines.Count);
        Assert.All(host.Cases, run => Assert.False(File.Exists(run.Endpoint.FilePath)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WrongEngineIdentityCannotContaminateAReport(bool wrongMetadata)
    {
        using var folder = new Folder();
        var outcome = await new ExperimentRunner(workerHost: new RecordingHost(wrongMetadata, !wrongMetadata))
            .RunAsync(new() { EngineIds = ["go"], DatasetRows = [100], Clients = [1], Repeats = 1 }, folder.Path, CancellationToken.None);
        Assert.Equal("failed", outcome.Report.Status);
        Assert.Single(outcome.Report.Failures);
        Assert.Empty(outcome.Report.Results);
    }

    [Fact]
    public void ComparisonFacetsKeepDatabaseAndEngineDimensionsSeparate()
    {
        var report = new BenchmarkReport { Status = "completed", Config = new() { Targets = [TargetKind.Sqlite, TargetKind.PostgreSql], EngineIds = ["csharp", "go", "java"] } };
        var rows = new[] { Row("csharp", TargetKind.Sqlite, 10), Row("go", TargetKind.Sqlite, 20), Row("csharp", TargetKind.PostgreSql, 30), Row("go", TargetKind.PostgreSql, 40) };
        var slice = new ReportSlice(nameof(Scenario.PointRead), 1000, 1);
        var databases = ReportAnalysis.Compare(report, rows, slice with { Engine = "go" }, AnalysisMetric.Rate);
        Assert.Equal(new[] { 40d, 20d }, databases.Select(series => series.Median!.Value));
        Assert.All(databases, series => Assert.Equal("go", series.Engine));
        var engines = ReportAnalysis.Compare(report, rows, slice with { Target = TargetKind.Sqlite }, AnalysisMetric.Rate);
        Assert.Equal(3, engines.Length);
        Assert.Equal(10, engines.Single(series => series.Engine == "csharp").Median);
        Assert.Equal(20, engines.Single(series => series.Engine == "go").Median);
        Assert.Null(engines.Single(series => series.Engine == "java").Median);
        Assert.All(engines, series => Assert.Equal(TargetKind.Sqlite, series.Target));
        Assert.Equal(new[] { "C# / .NET", "Go", "Java" }, engines.Select(series => series.Name));
        Assert.All(ReportAnalysis.Trends(report, rows, slice with { Target = TargetKind.Sqlite }, AnalysisMetric.Rate, false), series => Assert.Equal(TargetKind.Sqlite, series.Target));
    }

    [Fact]
    public void UnmeasuredNativeGcIsMissingRatherThanZero()
    {
        var row = Row("rust", TargetKind.Sqlite, 100) with { Client = new(30, 20, 21, null, null, null, null, null) };
        Assert.Null(ReportAnalysis.Value(row, AnalysisMetric.GcPause));
        var saved = JsonSerializer.Deserialize<PhaseResult>(JsonSerializer.Serialize(row, Json.Options), Json.Options)!;
        Assert.Null(saved.Client!.AllocatedBytes);
        Assert.Null(saved.Client.GcPauseMs);
    }

    private static PhaseResult Row(string engine, TargetKind target, double rate) => new()
    { Engine = engine, Target = target, DatasetRows = 1000, Clients = 1, Repeat = 1, Phase = nameof(Scenario.PointRead),
        Verified = true, ElapsedSeconds = 1, SuccessfulOperations = 100, OperationsPerSecond = rate };

    private sealed class RecordingHost(bool wrongMetadata = false, bool wrongResult = false) : IWorkerHost
    {
        public List<RunCase> Cases { get; } = [];
        public async Task RunAsync(RunCase run, Func<WireEvent, Task> receive, CancellationToken token)
        {
            Cases.Add(run);
            await receive(new(1, "metadata", run.RunId, JsonSerializer.SerializeToElement(new EngineMetadata(wrongMetadata ? "python" : run.Engine, "runtime", "driver", "version", []), Json.Options)));
            await receive(new(1, "result", run.RunId, JsonSerializer.SerializeToElement(Row(wrongResult ? "python" : run.Engine, run.Target, 10) with
            { DatasetRows = run.Rows, Clients = run.Clients, Repeat = run.Repeat }, Json.Options)));
        }
    }

    private sealed class Folder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "benchforge-native-test-" + Guid.NewGuid().ToString("N"));
        public Folder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
