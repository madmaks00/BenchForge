using System.Diagnostics;
using System.Numerics;

namespace BenchForge.Contracts;

// Portable histogram v1: integer microseconds, 128 sub-buckets per power of two.
// Bucket width <=0.782%; small values have exact integer-us resolution.
public sealed class LatencyHistogram
{
    private readonly long[] _counts = new long[4096];
    private double _sumUs;
    private double _maxUs;
    private long _count;
    private long _overflow;
    public long Count => _count;

    public void RecordTicks(long ticks) => RecordMicroseconds(Math.Max(1, ticks * (1_000_000.0 / Stopwatch.Frequency)));
    public void RecordMicroseconds(double microseconds)
    {
        var value = (ulong)Math.Max(1, Math.Ceiling(microseconds));
        if (value >= 1UL << 32) { _overflow++; value = (1UL << 32) - 1; }
        var exponent = BitOperations.Log2(value);
        var offset = (int)(((value - (1UL << exponent)) << 7) >> exponent);
        _counts[(exponent << 7) + offset]++;
        _count++;
        _sumUs += microseconds;
        _maxUs = Math.Max(_maxUs, microseconds);
    }
    public void Merge(LatencyHistogram other)
    {
        for (var i = 0; i < _counts.Length; i++) _counts[i] += other._counts[i];
        _count += other._count; _sumUs += other._sumUs; _maxUs = Math.Max(_maxUs, other._maxUs); _overflow += other._overflow;
    }
    public HistogramData Snapshot()
    {
        var buckets = new Dictionary<int, long>();
        for (var i = 0; i < _counts.Length; i++) if (_counts[i] > 0) buckets[i] = _counts[i];
        return new(_count, _sumUs, _maxUs, _overflow, buckets);
    }
}

public sealed record HistogramData(long Count, double SumMicroseconds, double MaxMicroseconds, long Overflow, Dictionary<int, long> Buckets)
{
    public string Format => "binary128-us-v1";
    public double? MeanMs => Count == 0 ? null : SumMicroseconds / Count / 1000;
    public double? P50Ms => Percentile(.50);
    public double? P95Ms => Percentile(.95);
    public double? P99Ms => Percentile(.99);
    public double? Percentile(double quantile)
    {
        if (Count == 0) return null;
        var rank = Math.Max(1, (long)Math.Ceiling(Count * quantile));
        long seen = 0;
        foreach (var (index, count) in Buckets.OrderBy(x => x.Key))
        {
            seen += count;
            if (seen < rank) continue;
            var exponent = index >> 7;
            var basis = 1UL << exponent;
            var offset = (ulong)(index & 127);
            var upper = basis + ((offset + 1) * basis + 127) / 128 - 1;
            return upper / 1000.0;
        }
        throw new InvalidDataException("Сумма корзин гистограммы не совпадает с Count.");
    }
}
