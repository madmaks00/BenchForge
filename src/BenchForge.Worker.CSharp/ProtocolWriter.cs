using System.Text.Json;
using BenchForge.Contracts;

namespace BenchForge.Worker.CSharp;

public sealed class ProtocolWriter(TextWriter writer)
{
    private readonly SemaphoreSlim _gate = new(1);
    public async Task EmitAsync(string type, string runId, object data)
    {
        var message = new { protocolVersion = 1, type, runId, data };
        var json = JsonSerializer.Serialize(message, Json.Options);
        await _gate.WaitAsync();
        try { await writer.WriteLineAsync(json); await writer.FlushAsync(); }
        finally { _gate.Release(); }
    }
}
