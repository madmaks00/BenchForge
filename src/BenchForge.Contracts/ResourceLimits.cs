using System.Globalization;
using System.Text.Json.Serialization;

namespace BenchForge.Contracts;

public sealed record ResourceLimits(double CpuLimit, int MemoryMb)
{
    [JsonIgnore] public string Label => $"{CpuLimit.ToString("0.##", CultureInfo.InvariantCulture)} CPU · {MemoryMb:N0} МБ";
    public override string ToString() => Label;
}
