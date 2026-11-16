using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using BenchForge.Contracts;

namespace BenchForge.Coordinator;

public interface IVmCommandClient
{
    Task<string> RunAsync(string[] arguments, CancellationToken token, int timeoutSeconds = 120);
    Task<int> RunGuestAsync(string[] arguments, Func<RunUpdate, Task> receive, CancellationToken token);
}

public sealed class MultipassClient : IVmCommandClient
{
    public async Task<string> RunAsync(string[] arguments, CancellationToken token, int timeoutSeconds = 120)
    {
        try { return await ProcessUtil.RunAsync(ProcessUtil.Multipass(), arguments, token, timeoutSeconds); }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3)
        { throw new InvalidOperationException("Установите Multipass версии 1.15 или новее для запуска чистых VM.", error); }
    }

    public async Task<int> RunGuestAsync(string[] arguments, Func<RunUpdate, Task> receive, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var info = ProcessUtil.Info(ProcessUtil.Multipass(), arguments);
        info.RedirectStandardInput = true;
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Не удалось запустить агент в VM.");
        var stderr = process.StandardError.ReadToEndAsync();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var cancel = Task.Delay(Timeout.Infinite, lifetime.Token);
        var exit = process.WaitForExitAsync(CancellationToken.None);
        var read = ReadEventsAsync();
        try
        {
            var first = await Task.WhenAny(exit, read, cancel);
            if (first == cancel)
            {
                try
                {
                    await process.StandardInput.WriteLineAsync("cancel");
                    await process.StandardInput.FlushAsync();
                }
                catch (IOException) { }
                if (await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(75))) != exit)
                    process.Kill(entireProcessTree: true);
            }
            else if (first == read) await read;
            await exit.WaitAsync(TimeSpan.FromSeconds(30));
            await read;
            var error = await stderr;
            if (process.ExitCode != 0 && !token.IsCancellationRequested && !string.IsNullOrWhiteSpace(error))
                await receive(new("log", error[..Math.Min(error.Length, 2000)]));
            return process.ExitCode;
        }
        finally
        {
            lifetime.Cancel();
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
        }

        async Task ReadEventsAsync()
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.Length > 4 * 1024 * 1024) throw new InvalidDataException("Агент VM превысил размер сообщения.");
                var update = JsonSerializer.Deserialize<RunUpdate>(line.TrimStart('\uFEFF'), Json.Options)
                    ?? throw new InvalidDataException("Пустое сообщение агента VM.");
                await receive(update);
            }
        }
    }
}
