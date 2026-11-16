using System.Diagnostics;
using BenchForge.Contracts;

namespace BenchForge.Coordinator;

public static class WorkerPreparation
{
    public static async Task PrepareLocalAsync(IReadOnlyCollection<string> engines, Action<string> log, CancellationToken token)
    {
        if (engines.Count == 0 || engines.Any(id => !Engines.All.Any(engine => engine.Id == id)))
            throw new ArgumentException("Выберите известные движки.");
        var root = WorkerCatalog.SourceDirectory();
        var selected = string.Join(',', engines.Where(id => id != "csharp"));
        if (selected.Length == 0) { log("C# готов после сборки решения."); return; }
        var info = OperatingSystem.IsWindows()
            ? ProcessUtil.Info("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(root, "prepare.ps1"), "-Engines", selected])
            : ProcessUtil.Info("bash", [Path.Combine(root, "prepare.sh"), selected]);
        info.WorkingDirectory = root;
        info.RedirectStandardInput = true;
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Не удалось запустить подготовку движков.");
        process.StandardInput.Close();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromHours(1));
        var stdout = ReadAsync(process.StandardOutput);
        var stderr = ReadAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(stdout, stderr);
            if (process.ExitCode != 0) throw new InvalidOperationException("Подготовка движков не завершилась. Подробности выше.");
            foreach (var engine in engines) await WorkerCatalog.CheckAsync(engine, token);
            log("Выбранные движки подготовлены и проверены.");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
        }
        async Task ReadAsync(StreamReader reader)
        { while (await reader.ReadLineAsync() is { } line) if (!string.IsNullOrWhiteSpace(line)) log(line.Length > 1000 ? line[^1000..] : line); }
    }
}
