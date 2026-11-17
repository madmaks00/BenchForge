using System.Security.Cryptography;
using System.Text.Json;
using BenchForge.Contracts;

namespace BenchForge.Coordinator;

public sealed record RemoteServerInfo(string Root, string Hostname, string Architecture, string Os = "Linux", string Kernel = "");
public sealed class RemoteReportMissingException() : IOException("Серверный агент завершился без отчёта.");

public sealed class RemoteAgentClient(IRemoteTransport? transport = null)
{
    private readonly IRemoteTransport _transport = transport ?? new SshTransport();
    public static string Control(RemoteServerInfo info, ServerAgentPackage package, string action, params string[] arguments) =>
        PosixShell.Command(new[] { "sh", info.Root + "/packages/" + package.SourceHash + "/server/agentctl.sh", action, info.Root }.Concat(arguments).ToArray());

    public async Task<RemoteServerInfo> ProbeAsync(RemoteServerOptions server, CancellationToken token)
    {
        server.Validate();
        const string command = "set -eu; unset DOCKER_CONTEXT; test \"$(uname -s)\" = Linux; test -S /var/run/docker.sock; " +
            "docker -H unix:///var/run/docker.sock info --format '{{.OSType}}' | grep -qx linux; " +
            "for tool in flock sha256sum tar tail stat; do command -v \"$tool\" >/dev/null; done; " +
            "os=Linux; if [ -r /etc/os-release ]; then . /etc/os-release; os=${PRETTY_NAME:-Linux}; fi; " +
            "printf 'BENCHFORGE_SERVER_V1\\n%s\\n%s\\n%s\\n%s\\n%s\\n' \"$HOME\" \"$(hostname)\" \"$(uname -m)\" \"$os\" \"$(uname -sr)\"";
        var lines = (await _transport.RunAsync(server, command, token)).Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        if (lines.Length != 6 || lines[0] != "BENCHFORGE_SERVER_V1" || !lines[1].StartsWith('/') || lines.Skip(1).Any(line => line.Any(char.IsControl)) ||
            lines[3] is not ("x86_64" or "aarch64" or "arm64"))
            throw new InvalidDataException("Нужен Linux-сервер x64/ARM64 с локальным Docker Engine и доступом пользователя к Docker.");
        return new(lines[1].TrimEnd('/') + "/.local/share/benchforge", lines[2], lines[3], lines[4], lines[5]);
    }

    public async Task PrepareAsync(RemoteServerOptions server, IReadOnlyCollection<string> engines, Action<string> log, CancellationToken token)
    {
        log("Проверка SSH и Docker на сервере…");
        var info = await ProbeAsync(server, token);
        var package = await ServerAgentBundle.CreateAsync(engines, token);
        var archive = info.Root + "/sources-" + package.SourceHash + ".tar.gz";
        var source = info.Root + "/packages/" + package.SourceHash;
        await _transport.RunAsync(server, "umask 077; mkdir -p -- " + PosixShell.Command(info.Root, source), token);
        log("Передача исходников агента; SSH-ключи остаются на этом компьютере…");
        await _transport.UploadAsync(server, package.Archive, archive, token);
        string expected;
        await using (var uploaded = File.OpenRead(package.Archive)) expected = Convert.ToHexString(await SHA256.HashDataAsync(uploaded, token));
        var actual = (await _transport.RunAsync(server, "sha256sum -- " + PosixShell.Quote(archive), token)).Split(' ')[0];
        if (!expected.Equals(actual, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Исходники переданы не полностью: SHA-256 не совпал.");
        await _transport.RunAsync(server, "tar -xzf " + PosixShell.Quote(archive) + " -C " + PosixShell.Quote(source), token);
        log("Сборка агента и выбранных языков на сервере. Первый запуск может занять несколько минут…");
        await _transport.FollowAsync(server, Control(info, package, "prepare", package.Image, package.SourceHash, package.Engines) + " 2>&1",
            line => { log(line); return Task.CompletedTask; }, token);
        await CheckAsync(server, engines, token);
        log("Сервер готов. Измерения будут выполняться на нём.");
    }

    public async Task<(RemoteServerInfo Info, ServerAgentPackage Package)> CheckAsync(RemoteServerOptions server, IReadOnlyCollection<string> engines, CancellationToken token)
    {
        var info = await ProbeAsync(server, token);
        var package = await ServerAgentBundle.CreateAsync(engines, token);
        try { await _transport.RunAsync(server, Control(info, package, "check-image", package.Image, package.SourceHash, package.Engines), token, 180); }
        catch (IOException error) when (error is not SshConnectionException)
        { throw new IOException("Подготовьте сервер для выбранных движков кнопкой «Подготовить сервер». " + error.Message, error); }
        return (info, package);
    }

    public async Task StartAsync(RemoteServerOptions server, RemoteServerInfo info, ServerAgentPackage package, string job, BenchmarkConfig config, string localFolder, CancellationToken token)
    {
        ValidateJob(job);
        var remote = info.Root + "/jobs/" + job;
        await _transport.RunAsync(server, "umask 077; mkdir -p -- " + PosixShell.Quote(remote), token);
        var path = Path.Combine(localFolder, "server-config.json.tmp");
        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config with { RemoteServer = null, VirtualMachine = null }, Json.Pretty), token);
            await _transport.UploadAsync(server, path, remote + "/config.json", token);
        }
        finally { File.Delete(path); }
        await _transport.RunAsync(server, Control(info, package, "start", job, package.Image), token, 90);
    }

    public Task FollowAsync(RemoteServerOptions server, RemoteServerInfo info, ServerAgentPackage package, string job, long offset, Func<string, Task> receive, CancellationToken token) =>
        _transport.FollowAsync(server, Control(info, package, "follow", job, offset.ToString(System.Globalization.CultureInfo.InvariantCulture)), receive, token);
    public Task<string> StateAsync(RemoteServerOptions server, RemoteServerInfo info, ServerAgentPackage package, string job, CancellationToken token) =>
        _transport.RunAsync(server, Control(info, package, "state", job), token);
    public Task<string> CancelAsync(RemoteServerOptions server, RemoteServerInfo info, ServerAgentPackage package, string job, CancellationToken token) =>
        _transport.RunAsync(server, Control(info, package, "cancel", job), token);
    public Task<string> ErrorAsync(RemoteServerOptions server, RemoteServerInfo info, ServerAgentPackage package, string job, CancellationToken token) =>
        _transport.RunAsync(server, Control(info, package, "error", job), token);
    public Task<string> CleanupAsync(RemoteServerOptions server, RemoteServerInfo info, ServerAgentPackage package, string job, CancellationToken token) =>
        _transport.RunAsync(server, Control(info, package, "cleanup", job), token, 120);

    public async Task<string> DownloadAsync(RemoteServerOptions server, RemoteServerInfo info, ServerAgentPackage package, string job, string folder, CancellationToken token)
    {
        var expected = (await _transport.RunAsync(server, Control(info, package, "collect", job), token, 300)).Split(' ')[0];
        if (expected == "NO_REPORT") throw new RemoteReportMissingException();
        var archive = Path.Combine(folder, "server-report.tar.gz");
        await _transport.DownloadAsync(server, info.Root + "/jobs/" + job + "/report.tar.gz", archive, token);
        await using var file = File.OpenRead(archive);
        if (!Convert.ToHexString(await SHA256.HashDataAsync(file, token)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Архив отчёта сервера передан не полностью: SHA-256 не совпал.");
        return archive;
    }

    public static void ValidateJob(string job)
    { if (!Guid.TryParseExact(job, "N", out _)) throw new ArgumentException("Некорректный ID серверного задания."); }
}
