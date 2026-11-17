using System.ComponentModel;
using System.Diagnostics;
using BenchForge.Contracts;

namespace BenchForge.Coordinator;

public interface IDockerDesktopHost
{
    Task<string?> EngineOsAsync(CancellationToken token);
    Task<bool> IsLocalEndpointAsync(CancellationToken token);
    Task StartAsync(CancellationToken token);
}

/// <summary>Opt-in desktop preparation; CLI preflight and remote/VM runs stay independent.</summary>
public sealed class DockerDesktopStartup
{
    private readonly IDockerDesktopHost _host;
    private readonly TimeSpan _timeout, _pollInterval;

    public DockerDesktopStartup(IDockerDesktopHost? host = null, TimeSpan? timeout = null, TimeSpan? pollInterval = null)
    {
        _host = host ?? new DockerDesktopHost();
        _timeout = timeout ?? TimeSpan.FromMinutes(3);
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
        if (_timeout <= TimeSpan.Zero || _pollInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public async Task EnsureReadyAsync(BenchmarkConfig config, Action<string>? status, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (config.RemoteServer is not null || config.VirtualMachine is not null || config.Targets.All(Catalog.Embedded)) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(_timeout);
        var watch = Stopwatch.StartNew();
        try
        {
            status?.Invoke("Проверяем Docker");
            if (Ready(await _host.EngineOsAsync(deadline.Token))) return;
            if (!await _host.IsLocalEndpointAsync(deadline.Token))
                throw new InvalidOperationException("Выбранное подключение Docker недоступно. Проверьте его адрес и доступность. Автозапуск работает для Docker Desktop на этом компьютере.");
            status?.Invoke("Запускаем Docker Desktop");
            await _host.StartAsync(deadline.Token);
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                status?.Invoke($"Ожидаем готовности Docker · {(int)watch.Elapsed.TotalSeconds} с");
                if (Ready(await _host.EngineOsAsync(deadline.Token))) break;
                await Task.Delay(_pollInterval, deadline.Token);
            }
            status?.Invoke("Docker готов · подготовка эксперимента");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { throw new OperationCanceledException(token); }
        catch (OperationCanceledException)
        { throw new TimeoutException($"Docker не стал готов за {_timeout.TotalSeconds:0} с. Откройте Docker Desktop, проверьте состояние движка и повторите запуск теста."); }
    }

    private static bool Ready(string? os)
    {
        if (os is null) return false;
        if (os == "linux") return true;
        throw new InvalidOperationException("Docker работает в неподходящем режиме. Переключите Docker Desktop на Linux containers и повторите запуск теста.");
    }
}

public sealed class DockerDesktopHost : IDockerDesktopHost
{
    public async Task<string?> EngineOsAsync(CancellationToken token)
    {
        try { return await ProcessUtil.RunAsync(ProcessUtil.Docker(), ["info", "--format", "{{.OSType}}"], token, 5); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or IOException) { return null; }
    }

    public async Task<bool> IsLocalEndpointAsync(CancellationToken token)
    {
        try
        {
            var context = Environment.GetEnvironmentVariable("DOCKER_CONTEXT");
            var endpoint = Environment.GetEnvironmentVariable("DOCKER_HOST");
            // DOCKER_CONTEXT takes precedence over DOCKER_HOST in the Docker CLI.
            if (!string.IsNullOrWhiteSpace(context) || string.IsNullOrWhiteSpace(endpoint))
            {
                var args = new List<string> { "context", "inspect" };
                if (!string.IsNullOrWhiteSpace(context)) args.Add(context);
                args.AddRange(["--format", "{{.Endpoints.docker.Host}}"]);
                endpoint = await ProcessUtil.RunAsync(ProcessUtil.Docker(), args, token, 5);
            }
            return IsLocalEndpoint(endpoint);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new InvalidOperationException("Не удалось проверить подключение Docker. Проверьте установку и настройки Docker Desktop."); }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or IOException)
        { throw new InvalidOperationException("Не удалось проверить подключение Docker. Установите Docker Desktop или проверьте его настройки подключения.", error); }
    }

    public static bool IsLocalEndpoint(string? endpoint) => endpoint is not null &&
        (endpoint.StartsWith("unix:///", StringComparison.OrdinalIgnoreCase) ||
         endpoint.StartsWith("npipe:////./pipe/", StringComparison.OrdinalIgnoreCase));

    public async Task StartAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            await ProcessUtil.RunAsync(ProcessUtil.Docker(), ["desktop", "start", "--detach"], token, 15);
            return;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or IOException) { }

        token.ThrowIfCancellationRequested();
        // Older Desktop releases have no `docker desktop start`; use the installed application.
        if (OperatingSystem.IsWindows())
        {
            var candidates = new[] {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DockerDesktop", "Docker Desktop.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "Docker Desktop.exe") };
            var executable = candidates.FirstOrDefault(File.Exists);
            if (executable is null) throw new InvalidOperationException("Docker Desktop не найден. Установите его и повторите запуск теста.");
            try
            {
                using var process = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
                if (process is null) throw new InvalidOperationException("Не удалось открыть Docker Desktop. Запустите его вручную и повторите тест.");
            }
            catch (Win32Exception error)
            { throw new InvalidOperationException("Не удалось открыть Docker Desktop. Запустите его вручную и повторите тест.", error); }
            return;
        }
        if (OperatingSystem.IsMacOS())
        {
            var app = new[] { "/Applications/Docker.app", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications", "Docker.app") }
                .FirstOrDefault(Directory.Exists);
            if (app is null) throw new InvalidOperationException("Docker Desktop не найден. Установите его и повторите запуск теста.");
            await ProcessUtil.RunAsync("/usr/bin/open", ["-g", "-a", app], token, 15);
            return;
        }
        throw new InvalidOperationException("Не удалось запустить Docker Desktop. На Linux запустите Docker Engine или Docker Desktop вручную и повторите тест.");
    }
}
