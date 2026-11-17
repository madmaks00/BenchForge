using System.Diagnostics;
using System.Text;
using BenchForge.Contracts;

namespace BenchForge.Coordinator;

public interface IRemoteTransport
{
    Task<string> RunAsync(RemoteServerOptions server, string command, CancellationToken token, int timeoutSeconds = 45);
    Task UploadAsync(RemoteServerOptions server, string localFile, string remoteFile, CancellationToken token);
    Task DownloadAsync(RemoteServerOptions server, string remoteFile, string localFile, CancellationToken token);
    Task FollowAsync(RemoteServerOptions server, string command, Func<string, Task> receive, CancellationToken token);
}

public sealed class SshConnectionException(string message) : IOException(message);

public static class PosixShell
{
    public static string Quote(string value)
    {
        if (value.Contains('\0')) throw new ArgumentException("Недопустимый нулевой символ в аргументе.");
        return "'" + value.Replace("'", "'\\''") + "'";
    }
    public static string Command(params string[] arguments) => string.Join(' ', arguments.Select(Quote));
}

public sealed class SshTransport : IRemoteTransport
{
    public static string[] Arguments(RemoteServerOptions server, string command)
    {
        server.Validate();
        var args = new List<string> { "-T", "-x", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes",
            "-o", "ForwardAgent=no", "-o", "ConnectTimeout=15", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=2",
            "-p", server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (server.User.Length > 0) args.AddRange(["-l", server.User]);
        if (server.IdentityFile is { } key) args.AddRange(["-i", Path.GetFullPath(key), "-o", "IdentitiesOnly=yes"]);
        if (server.KnownHostsFile is { } hosts)
        {
            var path = Path.GetFullPath(hosts);
            if (OperatingSystem.IsWindows()) path = path.Replace('\\', '/');
            args.AddRange(["-o", "UserKnownHostsFile=\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""]);
        }
        args.AddRange(["--", server.Host, "sh -c " + PosixShell.Quote(command)]);
        return args.ToArray();
    }

    private static Process Start(RemoteServerOptions server, string command)
    {
        foreach (var file in new[] { server.IdentityFile, server.KnownHostsFile }.OfType<string>())
            if (!File.Exists(file)) throw new FileNotFoundException("Не найден файл SSH: " + file);
        var executable = Environment.GetEnvironmentVariable("BENCHFORGE_SSH") ?? "ssh";
        var info = ProcessUtil.Info(executable, Arguments(server, command));
        info.RedirectStandardInput = true;
        try { return Process.Start(info) ?? throw new IOException("Не удалось запустить SSH."); }
        catch (System.ComponentModel.Win32Exception error)
        { throw new IOException("Установите клиент OpenSSH на этом компьютере.", error); }
    }

    private static async Task CompleteAsync(Process process, Task<string> errors, CancellationToken token)
    {
        await process.WaitForExitAsync(token);
        if (process.ExitCode == 0) return;
        var error = (await errors).Trim();
        if (error.Length > 2000) error = error[^2000..];
        if (process.ExitCode == 255)
            throw new SshConnectionException("SSH: " + error + " Проверьте адрес, доступ по ключу и доверие к ключу сервера в known_hosts. Первое подключение подтвердите в обычном SSH-клиенте.");
        throw new IOException("Сервер: " + error);
    }

    private static async Task StopAsync(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) when (process.HasExited) { }
        await process.WaitForExitAsync(CancellationToken.None);
    }

    public async Task<string> RunAsync(RemoteServerOptions server, string command, CancellationToken token, int timeoutSeconds = 45)
    {
        using var process = Start(server, command);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try { await CompleteAsync(process, error, timeout.Token); return (await output).Trim(); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new SshConnectionException("Сервер не ответил вовремя. Задание могло продолжить работу; используйте «Продолжить наблюдение»."); }
        finally { await StopAsync(process); }
    }

    public async Task UploadAsync(RemoteServerOptions server, string localFile, string remoteFile, CancellationToken token)
    {
        var temporary = remoteFile + ".upload";
        using var process = Start(server, "umask 077; cat > " + PosixShell.Quote(temporary) + " && mv -- " + PosixShell.Command(temporary, remoteFile));
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            await using var file = File.OpenRead(localFile);
            await file.CopyToAsync(process.StandardInput.BaseStream, timeout.Token);
            await process.StandardInput.BaseStream.FlushAsync(timeout.Token);
            process.StandardInput.Close();
            await CompleteAsync(process, error, timeout.Token);
            await output;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new SshConnectionException("Передача исходников превысила время ожидания. Повторите подготовку сервера."); }
        finally { await StopAsync(process); }
    }

    public async Task DownloadAsync(RemoteServerOptions server, string remoteFile, string localFile, CancellationToken token)
    {
        using var process = Start(server, "cat -- " + PosixShell.Quote(remoteFile));
        process.StandardInput.Close();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            await using (var file = File.Create(localFile))
            {
                var buffer = new byte[65536]; long total = 0; int count;
                while ((count = await process.StandardOutput.BaseStream.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    total += count;
                    if (total > 1024L * 1024 * 1024) throw new IOException("Архив отчёта больше 1 ГБ. Он остаётся на сервере для ручного скачивания.");
                    await file.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                }
            }
            await CompleteAsync(process, error, timeout.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new SshConnectionException("Передача отчёта превысила время ожидания. Отчёт остаётся на сервере."); }
        finally { await StopAsync(process); }
    }

    public async Task FollowAsync(RemoteServerOptions server, string command, Func<string, Task> receive, CancellationToken token)
    {
        using var process = Start(server, command);
        process.StandardInput.Close();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            while (await process.StandardOutput.ReadLineAsync(token) is { } line)
            {
                if (line.Length > 4 * 1024 * 1024) throw new InvalidDataException("Сервер превысил размер сообщения.");
                await receive(line);
            }
            await CompleteAsync(process, error, token);
        }
        finally { await StopAsync(process); }
    }
}
