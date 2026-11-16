namespace BenchForge.Contracts;

public sealed record DataRow(long Id, int GroupId, long AmountCents, long Version, string Payload);
public readonly record struct DatasetDigest(long Count, long SumIds, long SumAmounts);

// Version 1: unsigned 64-bit SplitMix64; unchecked wraparound; ASCII alphabet below.
// These exact operations are portable to every planned worker language.
public struct PortableRandom(ulong state)
{
    public ulong State = state;
    public ulong Next()
    {
        unchecked
        {
            State += 0x9e3779b97f4a7c15UL;
            var z = State;
            z = (z ^ (z >> 30)) * 0xbf58476d1ce4e5b9UL;
            z = (z ^ (z >> 27)) * 0x94d049bb133111ebUL;
            return z ^ (z >> 31);
        }
    }
    public long Key(long count, bool hotspot)
    {
        if (!hotspot) return (long)(Next() % (ulong)count) + 1;
        var cutoff = Math.Max(1, count / 5);
        return Next() % 100 < 80 ? (long)(Next() % (ulong)cutoff) + 1 : cutoff + (long)(Next() % (ulong)(count - cutoff)) + 1;
    }
}

public static class DataGenerator
{
    public const string Version = "splitmix64-ascii-v1";
    public const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    public static DataRow Create(long id, ulong seed, int payloadBytes)
    {
        var random = new PortableRandom(seed ^ unchecked((ulong)id * 0xd6e8feb86659fd93UL));
        var amount = (long)(random.Next() % 1_000_000UL) + 1;
        var payload = string.Create(payloadBytes, random, static (span, rng) =>
        {
            for (var index = 0; index < span.Length; index++) span[index] = Alphabet[(int)(rng.Next() % 62)];
        });
        return new(id, (int)((id - 1) % 100), amount, 0, payload);
    }
    public static DataRow[] Batch(long firstId, int count, ulong seed, int payloadBytes)
    {
        var result = new DataRow[count];
        for (var i = 0; i < count; i++) result[i] = Create(firstId + i, seed, payloadBytes);
        return result;
    }
}
