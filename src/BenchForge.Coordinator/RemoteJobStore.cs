using System.Text.Json;
using BenchForge.Contracts;

namespace BenchForge.Coordinator;

public sealed record RemoteJobTicket(string Job, RemoteServerOptions Server, RemoteServerInfo Info, ServerAgentPackage Package,
    BenchmarkConfig Config, string Folder, DateTimeOffset CreatedAt, string Status);

public sealed class RemoteJobStore(string? root = null)
{
    private readonly string _root = root ?? Environment.GetEnvironmentVariable("BENCHFORGE_REMOTE_HOME") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BenchForge", "remote-jobs");
    public async Task SaveAsync(RemoteJobTicket ticket)
    {
        RemoteAgentClient.ValidateJob(ticket.Job);
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, ticket.Job + ".json");
        await File.WriteAllTextAsync(file + ".tmp", JsonSerializer.Serialize(ticket, Json.Pretty));
        File.Move(file + ".tmp", file, overwrite: true);
    }
    public RemoteJobTicket? Latest(RemoteServerOptions server)
    {
        if (!Directory.Exists(_root)) return null;
        var tickets = new List<RemoteJobTicket>();
        foreach (var file in Directory.EnumerateFiles(_root, "*.json"))
            try
            {
                var ticket = JsonSerializer.Deserialize<RemoteJobTicket>(File.ReadAllText(file), Json.Options);
                if (ticket is not null && ticket.Server.Host == server.Host && ticket.Server.User == server.User && ticket.Server.Port == server.Port &&
                    ticket.Status is "starting" or "running" or "disconnected" or "cancel_requested" or "cleanup_pending") tickets.Add(ticket);
            }
            catch (IOException) { } catch (JsonException) { }
        return tickets.OrderByDescending(ticket => ticket.CreatedAt).FirstOrDefault();
    }
}
