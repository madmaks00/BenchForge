using BenchForge.Contracts;
using BenchForge.Coordinator;
using Xunit;

namespace BenchForge.Tests;

public sealed class ComparisonQualityTests
{
    private static readonly ReportSlice Slice = new(nameof(Scenario.PointRead), 1000, 1);
    private static BenchmarkReport Report(params TargetKind[] targets) => new()
    { Status = "completed", Config = new() { Targets = targets.ToList(), DatasetRows = [1000], Clients = [1], DurationSeconds = 120, Repeats = 3 } };
    private static PhaseResult[] Trials(TargetKind target, params double[] speeds) => speeds.Select((speed, index) => new PhaseResult
    {
        Target = target, DatasetRows = 1000, Clients = 1, Repeat = index + 1, Phase = Slice.Phase,
        OperationsPerSecond = speed, SuccessfulOperations = 1000, Verified = true, ElapsedSeconds = 120
    }).ToArray();
    private static EngineMetadata Metadata(string image, string version = "1", params (string Key, string Value)[] settings)
    {
        var values = settings.ToDictionary(item => item.Key, item => item.Value);
        values["image_tag"] = image;
        values["build"] = "Release";
        return new("csharp", ".NET 10", "driver 1", version, values);
    }

    [Fact]
    public void OverlappingRepeatRangesDoNotProduceAnUnqualifiedSpeedHeadline()
    {
        var rows = Trials(TargetKind.MySql, 100, 104, 108).Concat(Trials(TargetKind.PostgreSql, 103, 107, 111)).ToArray();
        var narrative = ReportAnalysis.Explain(Report(TargetKind.MySql, TargetKind.PostgreSql), rows, Slice, AnalysisMetric.Rate);
        Assert.Equal(ComparisonState.Variable, narrative.Review.State);
        Assert.True(narrative.Preliminary);
        Assert.Contains(narrative.Review.Findings, finding => finding.Code == "overlapping_ranges");
        Assert.Contains("разброс", narrative.Headline);
        Assert.Contains("больше за секунду", narrative.ObservedDifference);
        Assert.Contains(narrative.Limitations, note => note.Contains("не доказывает"));
    }

    [Fact]
    public void WideRangesAreFlaggedEvenIfTheTwoDatabasesNeverOverlap()
    {
        var rows = Trials(TargetKind.MySql, 80, 100, 120).Concat(Trials(TargetKind.PostgreSql, 200, 200, 200)).ToArray();
        var review = ComparisonQuality.Assess(Report(TargetKind.MySql, TargetKind.PostgreSql), rows, Slice, AnalysisMetric.Rate);
        Assert.Equal(ComparisonState.Variable, review.State);
        var finding = Assert.Single(review.Findings, finding => finding.Code == "repeat_spread");
        Assert.Contains("40,0%", finding.Message);
        Assert.DoesNotContain(review.Findings, finding => finding.Code == "overlapping_ranges");
    }

    [Fact]
    public void ConsistentLongTrialsKeepTheObservedDifferenceWithoutClaimingSignificance()
    {
        var report = Report(TargetKind.MySql, TargetKind.PostgreSql);
        var rows = Trials(TargetKind.MySql, 100, 101, 102).Concat(Trials(TargetKind.PostgreSql, 120, 121, 122)).ToArray();
        var narrative = ReportAnalysis.Explain(report, rows, Slice, AnalysisMetric.Rate);
        Assert.Equal(ComparisonState.Observed, narrative.Review.State);
        Assert.False(narrative.Preliminary);
        Assert.Null(narrative.ObservedDifference);
        Assert.Contains("больше за секунду", narrative.Headline);
        Assert.Contains(narrative.Limitations, note => note.Contains("не статистическая гарантия"));
    }

    [Fact]
    public void EmbeddedAndServerResultsExplicitlyDescribeDifferentConditions()
    {
        var rows = Trials(TargetKind.Sqlite, 10000, 10000, 10000).Concat(Trials(TargetKind.PostgreSql, 100, 100, 100)).ToArray();
        var narrative = ReportAnalysis.Explain(Report(TargetKind.Sqlite, TargetKind.PostgreSql), rows, Slice, AnalysisMetric.Rate);
        Assert.Equal(ComparisonState.ConditionsDiffer, narrative.Review.State);
        Assert.Contains("условия", narrative.Headline);
        Assert.Contains("SQLite", narrative.ObservedDifference);
        Assert.Contains(narrative.Review.Findings, finding => finding.Code == "storage_profiles");
    }

    [Fact]
    public void MissingTargetsDoNotCreateAProfileMismatchWithoutObservedData()
    {
        var rows = Trials(TargetKind.MySql, 100, 100, 100).Concat(Trials(TargetKind.PostgreSql, 120, 120, 120)).ToArray();
        var review = ComparisonQuality.Assess(Report(TargetKind.MySql, TargetKind.PostgreSql, TargetKind.Redis), rows, Slice, AnalysisMetric.Rate);
        Assert.Equal(ComparisonState.Incomplete, review.State);
        Assert.Contains(review.Findings, finding => finding.Code == "missing_series");
        Assert.DoesNotContain(review.Findings, finding => finding.Code == "storage_profiles");
    }

    [Fact]
    public void RepeatCountsRequireAnActualFiniteValueForTheSelectedMetric()
    {
        var rows = Trials(TargetKind.MySql, 100, 100, 100);
        rows[0] = rows[0] with { Server = new(120, 20, 0, 0, 1) };
        var report = Report(TargetKind.MySql);
        var series = Assert.Single(ReportAnalysis.Compare(report, rows, Slice, AnalysisMetric.ServerMemory));
        Assert.Equal(1, series.ValidRepeats);
        Assert.Equal(3, series.TotalRepeats);
        var review = ComparisonQuality.Assess(report, rows, Slice, AnalysisMetric.ServerMemory);
        Assert.True(review.Preliminary);
        Assert.Contains(review.Findings, finding => finding.Code == "few_repeats");
        Assert.Contains(review.Findings, finding => finding.Code == "missing_repeats");
    }

    [Fact]
    public void VersionChangesAreDetectedWithinOneDatabase()
    {
        var report = Report(TargetKind.PostgreSql);
        report.Engines["first"] = Metadata("postgres:18", "18.1");
        report.Engines["second"] = Metadata("postgres:18", "18.2");
        var review = ComparisonQuality.Assess(report, Trials(TargetKind.PostgreSql, 100, 100, 100), Slice, AnalysisMetric.Rate);
        Assert.Equal(ComparisonState.ConditionsDiffer, review.State);
        Assert.Contains("версия БД", Assert.Single(review.Findings).Message);
    }

    [Fact]
    public void DifferentVmIdentitiesAndEquivalentNumericLimitsAreExpected()
    {
        var report = Report(TargetKind.PostgreSql, TargetKind.MySql);
        report.Engines["pg1"] = Metadata("postgres:18", "18.1", ("vm_boot_id", "boot-a"), ("vm_instance", "first"), ("cpu_limit", "2"));
        report.Engines["pg2"] = Metadata("postgres:18", "18.1", ("vm_boot_id", "boot-b"), ("vm_instance", "second"), ("cpu_limit", "2.0"));
        report.Engines["mysql"] = Metadata("mysql:8.4", "8.4.7", ("cpu_limit", "2"));
        var rows = Trials(TargetKind.PostgreSql, 100, 100, 100).Concat(Trials(TargetKind.MySql, 120, 120, 120)).ToArray();
        var review = ComparisonQuality.Assess(report, rows, Slice, AnalysisMetric.Rate);
        Assert.Equal(ComparisonState.Observed, review.State);
        Assert.Empty(review.Findings);
    }

    [Fact]
    public void ChangedMemoryLimitsPreventAnUnqualifiedComparison()
    {
        var report = Report(TargetKind.PostgreSql);
        report.Engines["first"] = Metadata("postgres:18", "18", ("memory_limit_mb", "2048"));
        report.Engines["second"] = Metadata("postgres:18", "18", ("memory_limit_mb", "1024"));
        var review = ComparisonQuality.Assess(report, Trials(TargetKind.PostgreSql, 100, 100, 100), Slice, AnalysisMetric.Rate);
        Assert.Contains(review.Findings, finding => finding.Code == "environment_changed" && finding.Message.Contains("RAM"));
    }

    [Fact]
    public void DebugBuildsAreFlaggedEvenWithLongStableRepeats()
    {
        var report = Report(TargetKind.PostgreSql);
        var metadata = Metadata("postgres:18");
        metadata.Settings["build"] = "Debug; not suitable for performance comparison";
        report.Engines["debug"] = metadata;
        var review = ComparisonQuality.Assess(report, Trials(TargetKind.PostgreSql, 100, 100, 100), Slice, AnalysisMetric.Rate);
        Assert.Equal(ComparisonState.ConditionsDiffer, review.State);
        Assert.Contains(review.Findings, finding => finding.Code == "debug_build" && finding.NextStep.Contains("Release"));
    }

    [Fact]
    public void OldReportsWithoutMetadataRemainReadableAndMissingMetricsStayMissing()
    {
        var report = Report(TargetKind.MySql);
        var rows = Trials(TargetKind.MySql, 100, 100, 100);
        var review = ComparisonQuality.Assess(report, rows, Slice, AnalysisMetric.ServerMemory);
        Assert.Equal(ComparisonState.NoData, review.State);
        Assert.Null(Assert.Single(ReportAnalysis.Compare(report, rows, Slice, AnalysisMetric.ServerMemory)).Median);
        Assert.DoesNotContain(review.Findings, finding => finding.Code == "environment_changed");
        var speed = ReportAnalysis.Explain(report, rows, Slice, AnalysisMetric.Rate);
        Assert.Equal(ComparisonState.SingleSeries, speed.Review.State);
        Assert.False(speed.Preliminary);
    }

    [Fact]
    public void ZeroMediansDoNotProduceAnInfiniteSpread()
    {
        var rows = Trials(TargetKind.MySql, 0, 0, 10);
        var report = Report(TargetKind.MySql);
        Assert.Null(ComparisonQuality.SpreadPercent(Assert.Single(ReportAnalysis.Compare(report, rows, Slice, AnalysisMetric.Rate))));
        var review = ComparisonQuality.Assess(report, rows, Slice, AnalysisMetric.Rate);
        Assert.Contains(review.Findings, finding => finding.Code == "repeat_spread" && finding.Message.Contains("нулевые"));
    }

    [Fact]
    public void SetupDurationAndUnselectedDatasetsDoNotTriggerShortRunWarnings()
    {
        var slice = Slice with { Phase = "bulk_load" };
        var rows = Trials(TargetKind.MySql, 100, 100, 100).Select(row => row with
            { Phase = "bulk_load", ElapsedSeconds = 1, Unit = "rows/s", RowsPerSecond = 100 }).ToArray();
        var review = ComparisonQuality.Assess(Report(TargetKind.MySql), rows.Concat(Trials(TargetKind.MySql, 10)
            .Select(row => row with { ElapsedSeconds = .1, DatasetRows = 10000 })).ToArray(), slice, AnalysisMetric.Rate);
        Assert.DoesNotContain(review.Findings, finding => finding.Code == "short_run");
    }
}
