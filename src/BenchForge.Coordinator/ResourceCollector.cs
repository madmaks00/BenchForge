using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using BenchForge.Contracts;

namespace BenchForge.Coordinator;

public sealed record ResourceSample(long TimestampTicks, double MemoryMb, double CpuPercent, double ReadMb, double WrittenMb);
public sealed class ResourceCollector : IAsyncDisposable
{
    private readonly List<ResourceSample> _samples = [];
    private readonly object _lock = new();
    private Process? _process;
    private Task? _reader;
    public string? Warning { get; private set; }
    public IReadOnlyList<ResourceSample> Samples { get { lock (_lock) return _samples.ToArray(); } }
    public void Start(string? container)
    {
        if (container is null) return;
        _process = Process.Start(ProcessUtil.Info(ProcessUtil.Docker(), ["stats", "--format", "{{json .}}", container]));
        _reader = Task.Run(async () =>
        {
            try
            {
                while (await _process!.StandardOutput.ReadLineAsync() is { } line)
                {
                    var sample = ParseSample(line, Stopwatch.GetTimestamp());
                    if (sample is not null) lock (_lock) _samples.Add(sample);
                }
            }
            catch (Exception exception) { Warning = "Сбор ресурсов: " + exception.Message; }
        });
    }
    public static ResourceSample? ParseSample(string line, long timestamp)
    {
        line = Regex.Replace(line, @"\x1B\[[0-?]*[ -/]*[@-~]", "");
        var jsonStart = line.IndexOf('{');
        if (jsonStart < 0) return null;
        using var json = JsonDocument.Parse(line[jsonStart..]); var root = json.RootElement;
        var io = root.GetProperty("BlockIO").GetString()!.Split('/');
        var memory = root.GetProperty("MemUsage").GetString()!.Split('/')[0];
        var cpu = double.Parse(root.GetProperty("CPUPerc").GetString()!.TrimEnd('%'), CultureInfo.InvariantCulture);
        return new(timestamp, Bytes(memory) / 1048576, cpu, Bytes(io[0]) / 1048576, Bytes(io[1]) / 1048576);
    }
    public static double Bytes(string text)
    {
        text = text.Trim(); var index = 0; while (index < text.Length && (char.IsDigit(text[index]) || text[index] == '.')) index++;
        var value = double.Parse(text[..index], CultureInfo.InvariantCulture);
        return text[index..].Trim() switch
        {
            "" or "B" => value, "kB" or "KB" => value * 1000, "MB" => value * 1e6, "GB" => value * 1e9,
            "KiB" => value * 1024, "MiB" => value * 1048576, "GiB" => value * 1073741824,
            _ => throw new FormatException("Неизвестная единица Docker stats: " + text)
        };
    }
    public ServerMetrics Summary(long start, long end)
    {
        lock (_lock)
        {
            var relevant = _samples.Where(s => s.TimestampTicks >= start && s.TimestampTicks <= end).ToArray();
            var before = _samples.LastOrDefault(s => s.TimestampTicks <= start);
            return new(relevant.Length == 0 ? null : relevant.Max(s => s.MemoryMb), relevant.Length == 0 ? null : relevant.Average(s => s.CpuPercent),
                before is null || relevant.Length == 0 ? null : Math.Max(0, relevant[^1].ReadMb - before.ReadMb),
                before is null || relevant.Length == 0 ? null : Math.Max(0, relevant[^1].WrittenMb - before.WrittenMb), relevant.Length);
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (_process is not null)
        {
            if (!_process.HasExited) _process.Kill();
            await _process.WaitForExitAsync(CancellationToken.None);
            if (_reader is not null) await _reader;
            _process.Dispose();
            lock (_lock) if (_samples.Count == 0 && Warning is null) Warning = "Docker не вернул выборки ресурсов; поля оставлены пустыми.";
        }
    }
}
