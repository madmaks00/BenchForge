using System.Globalization;
using System.Text;
using System.Text.Json;
using BenchForge.Contracts;

namespace BenchForge.Coordinator;

public static class ReportStore
{
    public static double? Median(IEnumerable<double?> values)
    {
        var data = values.Where(x => x.HasValue).Select(x => x!.Value).Order().ToArray();
        return data.Length == 0 ? null : data.Length % 2 == 1 ? data[data.Length / 2] : (data[data.Length / 2 - 1] + data[data.Length / 2]) / 2;
    }
    public static List<SummaryRow> Summarize(IEnumerable<PhaseResult> results) => results.Where(r => r.Verified && !r.Cancelled && r.Errors == 0)
        .GroupBy(r => (r.Engine, r.Target, r.DatasetRows, r.Clients, r.Phase, r.Unit, r.Stage)).Select(group =>
        {
            var rates = group.Select(r => r.Unit == "rows/s" ? r.RowsPerSecond : r.Unit == "seconds" ? r.ElapsedSeconds : r.OperationsPerSecond).Where(x => x.HasValue).ToArray();
            return new SummaryRow(group.Key.Engine, group.Key.Target, group.Key.DatasetRows, group.Key.Clients, group.Key.Phase, group.Key.Unit,
                group.Count(), Median(rates), rates.Length == 0 ? null : rates.Min(), rates.Length == 0 ? null : rates.Max(),
                Median(group.Select(r => r.ServiceLatency?.P95Ms)), Median(group.Select(r => r.ServiceLatency?.P99Ms)),
                Median(group.Select(r => r.Client?.RssSampledMaxMb)), Median(group.Select(r => r.Server?.MemorySampledMaxMb)), group.Key.Stage);
        }).ToList();

    public static async Task SaveAsync(string folder, BenchmarkReport report)
    {
        report.Summary = Summarize(report.Results);
        var temporary = Path.Combine(folder, "report.json.tmp");
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(report, Json.Pretty));
        File.Move(temporary, Path.Combine(folder, "report.json"), overwrite: true);
        var csv = new StringBuilder("engine,target,rows,clients,repeat,phase,unit,seconds,operations,rows_affected,ops_per_second,rows_per_second,p50_ms,p95_ms,p99_ms,end_to_end_p99_ms,errors,dropped,planned,verified,cancelled,client_cpu_percent,client_rss_mb,gc_pause_ms,allocated_bytes,server_rss_mb,server_cpu_percent,server_read_mb,server_written_mb,resource_samples,stage_number,stage,stage_target_seconds,target_ops_per_second\n");
        foreach (var r in report.Results)
        {
            var fields = new object?[] { r.Engine, r.Target, r.DatasetRows, r.Clients, r.Repeat, r.Phase, r.Unit, r.ElapsedSeconds,
                r.SuccessfulOperations, r.SuccessfulRows, r.OperationsPerSecond, r.RowsPerSecond, r.ServiceLatency?.P50Ms,
                r.ServiceLatency?.P95Ms, r.ServiceLatency?.P99Ms, r.EndToEndLatency?.P99Ms, r.Errors, r.Dropped, r.PlannedOperations,
                r.Verified, r.Cancelled, r.Client?.CpuPercent, r.Client?.RssSampledMaxMb, r.Client?.GcPauseMs, r.Client?.AllocatedBytes,
                r.Server?.MemorySampledMaxMb, r.Server?.CpuMeanPercent, r.Server?.ReadMb, r.Server?.WrittenMb, r.Server?.Samples,
                r.Stage?.Number, r.Stage?.Name, r.Stage?.TargetSeconds, r.Stage?.OperationsPerSecond };
            csv.AppendLine(string.Join(',', fields.Select(Value)));
        }
        await File.WriteAllTextAsync(Path.Combine(folder, "results.csv"), csv.ToString(), new UTF8Encoding(true));
        await File.WriteAllTextAsync(Path.Combine(folder, "summary.json"), JsonSerializer.Serialize(report.Summary, Json.Pretty));
    }
    private static string Value(object? value)
    {
        var text = value is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : value?.ToString() ?? "";
        return '"' + text.Replace("\"", "\"\"") + '"';
    }
}
