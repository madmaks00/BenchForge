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
        if (Catalog.Embedded(target))
        {
            _file = Path.Combine(folder, "scratch-" + caseId + (target == TargetKind.DuckDb ? ".duckdb" : ".sqlite3"));
            Endpoint = new("", 0, "", "", "", _file); return;
        }
        var image = config.Image(target); var info = Catalog.Get(target);
        if (target == TargetKind.SqlServer)
        {
            var architecture = await ProcessUtil.RunAsync(_docker, ["info", "--format", "{{.Architecture}}"], token, 15);
            if (architecture is not ("x86_64" or "amd64"))
                throw new InvalidOperationException("SQL Server требует Docker на Linux x64. Для ARM-компьютера выберите Linux-сервер x64 по SSH.");
        }
        var peakClients = config.CaseClients().Max();
        try { await ProcessUtil.RunAsync(_docker, ["image", "inspect", image], token, 15); }
        catch (InvalidOperationException)
        { log("Загрузка образа " + image); await ProcessUtil.RunAsync(_docker, ["pull", image], token, 1800); }
        // go-ora does not advertise Oracle's long-password logon capability.
        // Keep the same 144-bit random password profile for every Oracle engine.
        var password = target == TargetKind.Oracle
            ? "Bf!a" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_')
            : "Bf!a" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var environment = new Dictionary<string, string>();
        _container = "dblab-" + target.ToString().ToLowerInvariant() + "-" + caseId[..12];
        var args = new List<string> { "run", "--detach", "--rm", "--name", _container, "--label", "dblab.case=" + caseId,
            "--cpus", config.CpuLimit.ToString(CultureInfo.InvariantCulture), "--memory", config.MemoryMb + "m", "--memory-swap", config.MemoryMb + "m",
            "--pids-limit", Math.Max(1024L, (long)peakClients * 4 + 128).ToString(CultureInfo.InvariantCulture), "-p", $"127.0.0.1::{info.Port}" };
        var command = new List<string>();
        if (Environment.GetEnvironmentVariable("BENCHFORGE_REMOTE_JOB") is { Length: > 0 } job)
        {
            RemoteAgentClient.ValidateJob(job);
            args.AddRange(["--label", "benchforge.job=" + job]);
        }
        void Env(string name, string value) { environment[name] = value; args.Add("-e"); args.Add(name); }
        switch (target)
        {
            case TargetKind.PostgreSql:
                Env("POSTGRES_DB", "dblab"); Env("POSTGRES_USER", "dblab"); Env("POSTGRES_PASSWORD", password);
                command.AddRange(["postgres", "-c", "max_connections=" + ((long)peakClients + 16)]); break;
            case TargetKind.MySql: case TargetKind.MariaDb:
                var prefix = target == TargetKind.MySql ? "MYSQL_" : "MARIADB_";
                Env(prefix + "DATABASE", "dblab"); Env(prefix + "USER", "dblab"); Env(prefix + "PASSWORD", password);
                Env(prefix + "ROOT_PASSWORD", Convert.ToHexString(RandomNumberGenerator.GetBytes(24)));
                command.Add("--max-connections=" + ((long)peakClients + 16)); break;
            case TargetKind.SqlServer:
                Env("ACCEPT_EULA", "Y"); Env("MSSQL_PID", "Developer"); Env("MSSQL_SA_PASSWORD", password);
                Env("MSSQL_MEMORY_LIMIT_MB", (config.MemoryMb * 80 / 100).ToString(CultureInfo.InvariantCulture)); break;
            case TargetKind.Oracle:
                Env("ORACLE_PWD", password); Env("ORACLE_CHARACTERSET", "AL32UTF8");
                args.AddRange(["--shm-size", "1g"]); break;
            case TargetKind.CockroachDb:
                command.AddRange(["start-single-node", "--insecure", "--listen-addr=:26257", "--advertise-addr=localhost:26257", "--cache=25%", "--max-sql-memory=25%"]); break;
            case TargetKind.Elasticsearch: case TargetKind.OpenSearch:
                Env("discovery.type", "single-node");
                Env("node.store.allow_mmap", "false");
                var heapMb = Math.Max(256, config.MemoryMb / 2);
                Env(target == TargetKind.Elasticsearch ? "ES_JAVA_OPTS" : "OPENSEARCH_JAVA_OPTS", $"-Xms{heapMb}m -Xmx{heapMb}m");
                if (target == TargetKind.Elasticsearch) { Env("xpack.security.enabled", "false"); Env("xpack.ml.enabled", "false"); }
                else { Env("DISABLE_SECURITY_PLUGIN", "true"); Env("DISABLE_INSTALL_DEMO_CONFIG", "true"); }
                // Anonymous volumes live only for this case and are removed with its container.
                args.AddRange(["--ulimit", "nofile=65536:65536", "-v", target == TargetKind.Elasticsearch ? "/usr/share/elasticsearch/data" : "/usr/share/opensearch/data"]); break;
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
        Endpoint = new("127.0.0.1", port, target switch { TargetKind.SqlServer => "sa", TargetKind.CockroachDb => "root", _ => "dblab" },
            password, target == TargetKind.Oracle ? "FREEPDB1" : "dblab");
        ImageId = await ProcessUtil.RunAsync(_docker, ["inspect", "--format", "{{.Image}}", _container], token);
        log("Ожидание готовности " + info.Name);
        var readyMinutes = target == TargetKind.Oracle ? 10 : 4;
        var deadline = DateTimeOffset.UtcNow.AddMinutes(readyMinutes);
        while (DateTimeOffset.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (target == TargetKind.Oracle)
                {
                    var state = await ProcessUtil.RunAsync(_docker, ["inspect", "--format", "{{.State.Running}} {{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}", _container], token);
                    if (!state.StartsWith("true ", StringComparison.Ordinal))
                        throw new InvalidOperationException(info.Name + " остановилась при запуске.");
                    if (state is not ("true healthy" or "true none")) { await Task.Delay(1000, token); continue; }
                }
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
        throw new TimeoutException(info.Name + $" не стала доступна за {readyMinutes} минут.");
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
            foreach (var suffix in new[] { "", "-wal", "-shm", ".wal" }) File.Delete(_file + suffix);
        }
    }
}
