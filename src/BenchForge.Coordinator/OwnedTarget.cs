using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using BenchForge.Contracts;

namespace BenchForge.Coordinator;

public sealed class OwnedTarget(TargetKind target, BenchmarkConfig config, string caseId, string folder, Action<string> log) : IAsyncDisposable
{
    private readonly string _docker = ProcessUtil.Docker();
    private string? _container;
    private string? _file;
    public string? Container => _container;
    public Endpoint Endpoint { get; private set; } = null!;
    public string? ImageId { get; private set; }

    public async Task StartAsync(CancellationToken token)
    {
        if (target == TargetKind.Sqlite)
        {
            _file = Path.Combine(folder, "scratch-" + caseId + ".sqlite3");
            Endpoint = new("", 0, "", "", "", _file); return;
        }
        var image = config.Image(target); var info = Catalog.Get(target);
        var peakClients = config.CaseClients().Max();
        try { await ProcessUtil.RunAsync(_docker, ["image", "inspect", image], token, 15); }
        catch (InvalidOperationException)
        { log("Загрузка образа " + image); await ProcessUtil.RunAsync(_docker, ["pull", image], token, 1800); }
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var environment = new Dictionary<string, string>();
        _container = "dblab-" + target.ToString().ToLowerInvariant() + "-" + caseId[..12];
        var args = new List<string> { "run", "--detach", "--rm", "--name", _container, "--label", "dblab.case=" + caseId,
            "--cpus", config.CpuLimit.ToString(CultureInfo.InvariantCulture), "--memory", config.MemoryMb + "m", "--memory-swap", config.MemoryMb + "m",
            "--pids-limit", Math.Max(1024, peakClients * 4 + 128).ToString(CultureInfo.InvariantCulture), "-p", $"127.0.0.1::{info.Port}" };
        var command = new List<string>();
        void Env(string name, string value) { environment[name] = value; args.Add("-e"); args.Add(name); }
        switch (target)
        {
            case TargetKind.PostgreSql:
                Env("POSTGRES_DB", "dblab"); Env("POSTGRES_USER", "dblab"); Env("POSTGRES_PASSWORD", password);
                command.AddRange(["postgres", "-c", "max_connections=" + (peakClients + 16)]); break;
            case TargetKind.MySql: case TargetKind.MariaDb:
                var prefix = target == TargetKind.MySql ? "MYSQL_" : "MARIADB_";
                Env(prefix + "DATABASE", "dblab"); Env(prefix + "USER", "dblab"); Env(prefix + "PASSWORD", password);
                Env(prefix + "ROOT_PASSWORD", Convert.ToHexString(RandomNumberGenerator.GetBytes(24)));
                command.Add("--max-connections=" + (peakClients + 16)); break;
            case TargetKind.MongoDb:
                Env("MONGO_INITDB_ROOT_USERNAME", "dblab"); Env("MONGO_INITDB_ROOT_PASSWORD", password); break;
            case TargetKind.Redis: case TargetKind.Valkey:
                Env("DBLAB_PASSWORD", password);
                var binary = target == TargetKind.Redis ? "redis-server" : "valkey-server";
                command.AddRange(["sh", "-c", "exec " + binary + " --save '' --appendonly no --requirepass \"$DBLAB_PASSWORD\" --maxmemory "
                    + (config.MemoryMb * 70 / 100).ToString(CultureInfo.InvariantCulture) + "mb --maxmemory-policy noeviction"]); break;
            case TargetKind.ClickHouse:
                Env("CLICKHOUSE_DB", "dblab"); Env("CLICKHOUSE_USER", "dblab"); Env("CLICKHOUSE_PASSWORD", password);
                Env("CLICKHOUSE_DEFAULT_ACCESS_MANAGEMENT", "1");
                args.AddRange(["--ulimit", "nofile=262144:262144"]); break;
        }
        args.Add(image); args.AddRange(command);
        await ProcessUtil.RunAsync(_docker, args, token, 60, environment);
        var binding = await ProcessUtil.RunAsync(_docker, ["port", _container, info.Port + "/tcp"], token);
        var port = int.Parse(binding.Split('\n')[0].Trim().Split(':')[^1], CultureInfo.InvariantCulture);
        Endpoint = new("127.0.0.1", port, "dblab", password, "dblab");
        ImageId = await ProcessUtil.RunAsync(_docker, ["inspect", "--format", "{{.Image}}", _container], token);
        log("Ожидание готовности " + info.Name);
        var deadline = DateTimeOffset.UtcNow.AddMinutes(4);
        while (DateTimeOffset.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var socket = new TcpClient();
                await socket.ConnectAsync(Endpoint.Host, Endpoint.Port, token).AsTask().WaitAsync(TimeSpan.FromSeconds(2), token);
                return;
            }
            catch (Exception exception) when (exception is SocketException or TimeoutException)
            {
                var state = await ProcessUtil.RunAsync(_docker, ["inspect", "--format", "{{.State.Running}}", _container], token);
                if (state != "true") throw new InvalidOperationException(info.Name + " остановилась при запуске.");
                await Task.Delay(500, token);
            }
        }
        throw new TimeoutException(info.Name + " не стала доступна за 4 минуты.");
    }
    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            string owner;
            try { owner = await ProcessUtil.RunAsync(_docker, ["inspect", "--format", "{{index .Config.Labels \"dblab.case\"}}", _container], CancellationToken.None); }
            catch (InvalidOperationException exception) when (exception.Message.Contains("No such", StringComparison.OrdinalIgnoreCase)) { return; }
            if (owner != caseId) throw new InvalidOperationException("Метка контейнера не совпала; очистка остановлена.");
            await ProcessUtil.RunAsync(_docker, ["rm", "--force", "--volumes", _container], CancellationToken.None);
        }
        else if (_file is not null)
        {
            var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(_file).StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Путь временной базы вышел за папку отчёта.");
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(_file + suffix);
        }
    }
}
