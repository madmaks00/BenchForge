using System.Text.Json;
using BenchForge.Contracts;

namespace BenchForge.Coordinator;

public static class WorkerConformance
{
    public static readonly double[] Latencies = [.1, 1, 1.1, 127.5, 128, 129.2, 1024, 4294967297];
    public static object Vectors()
    {
        var random = new PortableRandom(0);
        var histogram = new LatencyHistogram(); foreach (var value in Latencies) histogram.RecordMicroseconds(value);
        return new { generator = DataGenerator.Version, random = Enumerable.Range(0, 3).Select(_ => random.Next().ToString("x16")).ToArray(),
            rows = new[] { DataGenerator.Create(1, 42, 16), DataGenerator.Create(1000, 42, 16), DataGenerator.Create(7, ulong.MaxValue, 16) },
            histogram = histogram.Snapshot() };
    }

    public static void Verify(string json)
    {
        using var expected = JsonDocument.Parse(JsonSerializer.Serialize(Vectors(), Json.Options));
        using var actual = JsonDocument.Parse(json.Trim().TrimStart('\uFEFF'));
        if (!Same(expected.RootElement, actual.RootElement)) throw new InvalidDataException("Движок не прошёл проверку одинаковых данных и гистограмм.");
    }

    private static bool Same(JsonElement expected, JsonElement actual) => expected.ValueKind switch
    {
        JsonValueKind.Object => actual.ValueKind == JsonValueKind.Object && expected.EnumerateObject().All(item => actual.TryGetProperty(item.Name, out var value) && Same(item.Value, value)),
        JsonValueKind.Array => actual.ValueKind == JsonValueKind.Array && expected.GetArrayLength() == actual.GetArrayLength()
            && expected.EnumerateArray().Zip(actual.EnumerateArray()).All(pair => Same(pair.First, pair.Second)),
        JsonValueKind.Number => actual.ValueKind == JsonValueKind.Number && Math.Abs(expected.GetDouble() - actual.GetDouble()) <= Math.Max(1e-9, Math.Abs(expected.GetDouble()) * 1e-12),
        _ => expected.ValueKind == actual.ValueKind && expected.ToString() == actual.ToString()
    };
}
