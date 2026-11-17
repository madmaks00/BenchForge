using System.Text.Json;
using BenchForge.Contracts;
using BenchForge.Coordinator;
using Xunit;

namespace BenchForge.Tests;

public sealed class ResourceSeriesTests
{
    private static BenchmarkConfig Config => new() { Targets = [TargetKind.DuckDb], DatasetRows = [100], Clients = [1], Repeats = 1,
        WarmupSeconds = 0, DurationSeconds = .05, Scenarios = [Scenario.PointRead], ResourceSeries = [new(1, 512), new(2, 512)] };
    [Fact] public void EachProfileIsValidatedAgainstDatabaseAndVmRequirements()
    {
        Config.Validate();
        Assert.Throws<ArgumentException>(() => (Config with { ResourceSeries = [new(0, 512)] }).Validate());
        Assert.Throws<ArgumentException>(() => (Config with { ResourceSeries = [new(1, 512), new(1, 512)] }).Validate());
        Assert.Throws<ArgumentException>(() => (Config with { Targets = [TargetKind.Oracle], ResourceSeries = [new(2, 2048)] }).Validate());
        Assert.Throws<ArgumentException>(() => (Config with { Targets = [TargetKind.PostgreSql], VirtualMachine = new() { Cpus = 1 }, ResourceSeries = [new(2, 512)] }).Validate());
        Assert.Throws<ArgumentException>(() => (Config with { Targets = [TargetKind.Sqlite] }).Validate());
    }
    [Fact] public void LegacyConfigurationStillHasOneUnstampedCase()
    {
        var config = Config with { ResourceSeries = [] };
        Assert.Null(Assert.Single(config.ResourceCases())); Assert.Same(config, config.ForResource(null));
    }
    [Fact] public void ProfileRoundTripAndSingleCaseKeepOriginalConfigurationUnchanged()
    {
        var config = JsonSerializer.Deserialize<BenchmarkConfig>(JsonSerializer.Serialize(Config, Json.Options), Json.Options)!;
        Assert.Equal(Config.ResourceSeries, config.ResourceSeries);
        var single = config.ForResource(new(4, 4096));
        Assert.Empty(single.ResourceSeries); Assert.Equal(4, single.CpuLimit); Assert.Equal(4096, single.MemoryMb); Assert.Equal(2, config.ResourceSeries.Count);
    }
    private static PhaseResult Row(ResourceLimits resource, double rate, int repeat = 1) => new() { Target = TargetKind.DuckDb, Phase = "PointRead", DatasetRows = 100,
        Clients = 1, Repeat = repeat, Resource = resource, Verified = true, SuccessfulOperations = 10, OperationsPerSecond = rate, ElapsedSeconds = 1 };
    [Fact] public void DifferentResourcesNeverMergeIntoOneSummaryOrComparison()
    {
        var rows = new[] { Row(new(1, 512), 10), Row(new(2, 512), 80) };
        Assert.Equal(2, ReportStore.Summarize(rows).Count);
        var slice = new ReportSlice("PointRead", 100, 1, Resource: new(1, 512));
        Assert.True(ReportAnalysis.Matches(rows[0], slice)); Assert.False(ReportAnalysis.Matches(rows[1], slice));
        var series = Assert.Single(ReportAnalysis.Compare(new() { Config = Config }, rows, slice, AnalysisMetric.Rate)); Assert.Equal(10, series.Median);
    }
    [Fact] public void CpuAndMemoryCurvesKeepTheOtherResourceConstant()
    {
        var rows = new[] { Row(new(1, 512), 10), Row(new(2, 512), 20), Row(new(1, 1024), 30), Row(new(2, 1024), 40) };
        var slice = new ReportSlice("PointRead", 100, 1);
        var cpu = ReportAnalysis.ResourceTrends(rows, slice, AnalysisMetric.Rate, false);
        Assert.Equal(2, cpu.Length); Assert.All(cpu, line => Assert.Equal(new[] { 1d, 2d }, line.Points.Select(point => point.X)));
        var memory = ReportAnalysis.ResourceTrends(rows, slice, AnalysisMetric.Rate, true);
        Assert.Equal(2, memory.Length); Assert.All(memory, line => Assert.Equal(new[] { 512d, 1024d }, line.Points.Select(point => point.X)));
    }
    [Fact] public async Task RunnerExecutesEveryProfileAndStampsItsActualLimits()
    {
        var folder = Path.Combine(Path.GetTempPath(), "benchforge-resource-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var worker = new FakeWorker();
            var result = await new ExperimentRunner(workerHost: worker).RunAsync(Config, folder, CancellationToken.None);
            Assert.Equal("completed", result.Report.Status); Assert.Equal(2, worker.Runs.Count);
            Assert.Equal(new[] { 1d, 2d }, worker.Runs.Select(run => run.Config.CpuLimit));
            Assert.All(worker.Runs, run => Assert.Empty(run.Config.ResourceSeries));
            Assert.Equal(Config.ResourceSeries, result.Report.Results.Select(row => row.Resource));
            Assert.Equal(2, result.Report.Summary.Count);
            var csv = await File.ReadAllTextAsync(Path.Combine(result.Folder, "results.csv"));
            Assert.Contains("cpu_limit,memory_limit_mb", csv);
        }
        finally { Directory.Delete(folder, true); }
    }
    private sealed class FakeWorker : IWorkerHost
    {
        public List<RunCase> Runs { get; } = [];
        public async Task RunAsync(RunCase run, Func<WireEvent, Task> receive, CancellationToken token)
        {
            Runs.Add(run);
            await receive(new(1, "result", run.RunId, JsonSerializer.SerializeToElement(Row(new(99, 9999), 10) with { Resource = null }, Json.Options)));
        }
    }
}
