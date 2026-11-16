using BenchForge.Contracts;
using BenchForge.Coordinator;
using Xunit;

namespace BenchForge.Tests;

public sealed class ReportAnalysisTests
{
    private static readonly ReportSlice Slice = new(nameof(Scenario.PointRead), 1000, 1);
    private static BenchmarkReport Report(params TargetKind[] targets) => new()
    { Status = "completed", Config = new() { Targets = targets.ToList(), DatasetRows = [1000], Clients = [1, 2], DurationSeconds = 1, Repeats = 1 } };
    private static PhaseResult Row(TargetKind target, double speed, int repeat = 1) => new()
    { Target = target, DatasetRows = 1000, Clients = 1, Repeat = repeat, Phase = Slice.Phase, Unit = "ops/s", OperationsPerSecond = speed,
        SuccessfulOperations = 100, Verified = true, ElapsedSeconds = 1 };

    [Fact] public void ShortSingleTrialDescribesObservedDifferenceAndLimits()
    {
        var report = Report(TargetKind.PostgreSql, TargetKind.MySql);
        var result = ReportAnalysis.Explain(report, [Row(TargetKind.PostgreSql, 1245.85), Row(TargetKind.MySql, 1169.01)], Slice, AnalysisMetric.Rate);
        Assert.Contains("6,6%", result.Headline); Assert.Contains("PostgreSQL", result.Headline);
        Assert.True(result.Preliminary); Assert.Contains(result.Limitations, n => n.Contains("один повтор"));
        Assert.Contains(result.Limitations, n => n.Contains("Короткий прогон"));
    }
    [Fact] public void MedianRangeAndErrorExclusionUseCorrectRepeats()
    {
        var report = Report(TargetKind.MySql);
        var rows = new[] { Row(TargetKind.MySql, 100, 1), Row(TargetKind.MySql, 300, 2), Row(TargetKind.MySql, 99999, 3) with { Errors = 1 },
            Row(TargetKind.MySql, 90000, 4) with { Verified = false }, Row(TargetKind.MySql, 80000, 5) with { Cancelled = true } };
        var series = Assert.Single(ReportAnalysis.Compare(report, rows, Slice, AnalysisMetric.Rate));
        Assert.Equal(200, series.Median); Assert.Equal(100, series.Minimum); Assert.Equal(300, series.Maximum);
        Assert.Equal(2, series.ValidRepeats); Assert.Equal(5, series.TotalRepeats); Assert.Equal(1, series.Errors);
    }
    [Fact] public void FailedAndMissingTargetsStayVisible()
    {
        var report = Report(TargetKind.PostgreSql, TargetKind.MySql, TargetKind.MongoDb);
        var series = ReportAnalysis.Compare(report, [Row(TargetKind.MySql, 10) with { Errors = 9 }], Slice, AnalysisMetric.Rate);
        Assert.Equal(3, series.Length); Assert.All(series, s => Assert.Null(s.Median));
        Assert.Contains("без ошибок", series.Single(x => x.Target == TargetKind.MySql).MissingReason);
        Assert.Contains("Нет результата", series.Single(x => x.Target == TargetKind.PostgreSql).MissingReason);
    }
    [Fact] public void ErrorAndDropFractionsIncludeFailedTrials()
    {
        var row = Row(TargetKind.MySql, 80) with { SuccessfulOperations = 80, Errors = 20, PlannedOperations = 125, Dropped = 25 };
        Assert.Equal(20, Assert.Single(ReportAnalysis.Compare(Report(TargetKind.MySql), [row], Slice, AnalysisMetric.ErrorPercent)).Median);
        Assert.Equal(20, Assert.Single(ReportAnalysis.Compare(Report(TargetKind.MySql), [row], Slice, AnalysisMetric.DropPercent)).Median);
    }
    [Fact] public void MissingResourceValuesAreNotZeroAndSqliteHasNoServer()
    {
        var result = ReportAnalysis.Compare(Report(TargetKind.Sqlite), [Row(TargetKind.Sqlite, 50)], Slice, AnalysisMetric.ServerMemory).Single();
        Assert.Null(result.Median); Assert.Empty(result.Values); Assert.Contains("внутри генератора", result.MissingReason);
        Assert.Null(ReportAnalysis.Value(Row(TargetKind.MySql, 20), AnalysisMetric.DropPercent));
    }
    [Fact] public void UnsupportedOperationsAreExplained()
    {
        var result = ReportAnalysis.Compare(Report(TargetKind.Redis), [], Slice with { Phase = nameof(Scenario.Aggregate) }, AnalysisMetric.Rate).Single();
        Assert.Null(result.Median); Assert.Contains("Не поддерживается", result.MissingReason);
    }
    [Fact] public void UnitsAndDirectionsRespectBatchAndPreparation()
    {
        var batch = Row(TargetKind.MySql, 10) with { Phase = nameof(Scenario.BatchInsert), Unit = "rows/s", RowsPerSecond = 1000 };
        Assert.Equal(1000, ReportAnalysis.Rate(batch)); Assert.Equal("зап./с", ReportAnalysis.RateUnit(batch.Phase));
        var index = batch with { Phase = "index_build", Unit = "seconds", ElapsedSeconds = 3.2 };
        Assert.Equal(3.2, ReportAnalysis.Rate(index));
        Assert.Equal(MetricDirection.Lower, ReportAnalysis.Describe(AnalysisMetric.Rate, index.Phase).Direction);
        Assert.Null(ReportAnalysis.Value(index, AnalysisMetric.ErrorPercent));
    }
    [Fact] public void LatencyPercentageUsesOtherLatencyAsDenominator()
    {
        var histogram = new LatencyHistogram(); histogram.RecordMicroseconds(10000); var slow = histogram.Snapshot();
        var fastHistogram = new LatencyHistogram(); fastHistogram.RecordMicroseconds(5000); var fast = fastHistogram.Snapshot();
        var result = ReportAnalysis.Explain(Report(TargetKind.MySql, TargetKind.PostgreSql),
            [Row(TargetKind.MySql, 10) with { ServiceLatency = slow }, Row(TargetKind.PostgreSql, 10) with { ServiceLatency = fast }], Slice, AnalysisMetric.P99);
        Assert.Contains("50,0% меньше", result.Headline);
    }
    [Fact] public void ResourceNarrativeDoesNotRankDatabasesByMemory()
    {
        var rows = new[] { Row(TargetKind.MySql, 100) with { Server = new(100, 15, 0, 0, 3) }, Row(TargetKind.PostgreSql, 200) with { Server = new(300, 30, 0, 0, 3) } };
        var result = ReportAnalysis.Explain(Report(TargetKind.MySql, TargetKind.PostgreSql), rows, Slice, AnalysisMetric.ServerMemory);
        Assert.Contains("вместе со скоростью", result.Headline); Assert.DoesNotContain("больше за секунду", result.Headline);
    }
    [Fact] public void TrendsFixOtherDimensionAndExcludeFailedLevels()
    {
        var report = Report(TargetKind.MySql);
        var rows = new[] { Row(TargetKind.MySql, 100), Row(TargetKind.MySql, 300) with { Clients = 2 },
            Row(TargetKind.MySql, 900) with { DatasetRows = 10000 }, Row(TargetKind.MySql, 99999) with { Clients = 8, Errors = 1 } };
        var clients = ReportAnalysis.Trends(report, rows, Slice, AnalysisMetric.Rate, false).Single();
        Assert.Equal(new[] { 1d, 2d }, clients.Points.Select(p => p.X)); Assert.Equal(new[] { 100d, 300d }, clients.Points.Select(p => p.Median));
        var volumes = ReportAnalysis.Trends(report, rows, Slice, AnalysisMetric.Rate, true).Single();
        Assert.Equal(new[] { 1000d, 10000d }, volumes.Points.Select(p => p.X));
    }
    [Fact] public void DifferentEnginesAreNeverMerged()
    {
        var rows = new[] { Row(TargetKind.MySql, 10), Row(TargetKind.MySql, 100) with { Engine = "go" } };
        var result = ReportAnalysis.Compare(Report(TargetKind.MySql), rows, Slice, AnalysisMetric.Rate);
        Assert.Equal(2, result.Length); Assert.Equal(2, result.Select(r => r.Id).Distinct().Count());
        Assert.All(result, r => Assert.Single(r.Values));
    }
    [Fact] public void ThreeLongTrialsRemoveShortSampleFlagWithoutClaimingConfidence()
    {
        var rows = Enumerable.Range(1, 3).SelectMany(n => new[] { Row(TargetKind.MySql, 100 + n, n) with { ElapsedSeconds = 60 }, Row(TargetKind.PostgreSql, 110 + n, n) with { ElapsedSeconds = 60 } }).ToArray();
        var result = ReportAnalysis.Explain(Report(TargetKind.MySql, TargetKind.PostgreSql), rows, Slice, AnalysisMetric.Rate);
        Assert.False(result.Preliminary); Assert.Contains(result.Limitations, n => n.Contains("не статистическая гарантия"));
        Assert.Contains("не общий рейтинг", result.Explanation);
    }
    [Fact] public void ZeroMedianDoesNotHideErrorsInIndividualRepeats()
    {
        var rows = Enumerable.Range(1, 3).SelectMany(n => new[] {
            Row(TargetKind.MySql, 100, n) with { Errors = n == 3 ? 1 : 0, Dropped = n == 3 ? 1 : 0, PlannedOperations = 100 },
            Row(TargetKind.PostgreSql, 100, n) with { PlannedOperations = 100 } }).ToArray();
        var report = Report(TargetKind.MySql, TargetKind.PostgreSql);
        var errors = ReportAnalysis.Explain(report, rows, Slice, AnalysisMetric.ErrorPercent);
        Assert.Contains("в отдельных повторах есть ошибки", errors.Headline); Assert.DoesNotContain("ошибок нет", errors.Headline);
        var drops = ReportAnalysis.Explain(report, rows, Slice, AnalysisMetric.DropPercent);
        Assert.Contains("в отдельных повторах есть пропуски", drops.Headline);
    }
}
