using System.Diagnostics;
using System.Text.Json;
using BenchForge.Contracts;

namespace BenchForge.Coordinator;

public interface IWorkerHost
{
    Task CheckAsync(string engine, CancellationToken token) => Task.CompletedTask;
    Task RunAsync(RunCase run, Func<WireEvent, Task> receive, CancellationToken token);
}

public sealed class WorkerProcess : IWorkerHost
{
    public Task CheckAsync(string engine, CancellationToken token) => WorkerCatalog.CheckAsync(engine, token);
    public async Task RunAsync(RunCase run, Func<WireEvent, Task> receive, CancellationToken token)
    {
        using var compiler = new NativeSqlCompiler();
        if (run.Config.CustomWorkload is { } workload && run.Engine != "csharp")
        {
            var program = await compiler.CompileAsync(run, token);
            var settings = workload with { Schema = new(), AfterLoad = new(), Datasets = [], Operations = [], Checks = [] };
            run = run with { SqlProgram = program, Config = run.Config with { CustomWorkload = settings } };
        }
        var launch = WorkerCatalog.Resolve(run.Engine);
        var info = ProcessUtil.Info(launch.Executable, launch.Arguments);
        info.WorkingDirectory = launch.Directory;
        WorkerCatalog.ConfigureNativeLibraries(info, launch);
        info.RedirectStandardInput = true;
        var command = JsonSerializer.Serialize(new WorkerCommand(1, "run", run), Json.Options);
        if (System.Text.Encoding.UTF8.GetByteCount(command) > 64 * 1024 * 1024)
            throw new InvalidDataException("Настройки запуска занимают больше 64 МБ. Сократите SQL-скрипты сценария.");
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Не удалось запустить движок " + run.Engine);
        var stderr = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteLineAsync(command);
        await process.StandardInput.FlushAsync();
        using var lifetime = new CancellationTokenSource();
        using var registration = token.Register(() => _ = Task.Run(async () =>
        {
            try
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new WorkerCommand(1, "cancel"), Json.Options));
                await process.StandardInput.FlushAsync();
                await Task.Delay(TimeSpan.FromSeconds(60), lifetime.Token);
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (OperationCanceledException) { }
            catch { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        }));
        var completed = false;
        string? failure = null;
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.Length > 4 * 1024 * 1024) throw new InvalidDataException("Движок превысил размер сообщения.");
                var message = JsonSerializer.Deserialize<WireEvent>(line.TrimStart('\uFEFF'), Json.Options) ?? throw new InvalidDataException("Пустое сообщение движка.");
                if (message.ProtocolVersion != 1 || message.Type != "hello" && message.RunId != run.RunId) throw new InvalidDataException("Неверная версия протокола или ID запуска.");
                if (message.Type == "hello" && message.Data.GetProperty("engine").GetString() != run.Engine)
                    throw new InvalidDataException("Запущен другой движок.");
                if (message.Type == "completed") completed = true;
                if (message.Type == "failed") failure = message.Data.GetProperty("message").GetString();
                await receive(message);
            }
            await process.WaitForExitAsync(CancellationToken.None);
            if (token.IsCancellationRequested) return;
            if (failure is not null) throw new InvalidOperationException(failure);
            if (!completed || process.ExitCode != 0)
            { var error = await stderr; throw new InvalidOperationException("Движок завершился без корректного результата. " + error[..Math.Min(error.Length, 1000)]); }
        }
        finally
        {
            lifetime.Cancel();
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
        }
    }
}
