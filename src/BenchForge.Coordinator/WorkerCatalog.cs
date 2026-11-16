using System.Runtime.InteropServices;
using System.Text.Json;
using BenchForge.Contracts;

namespace BenchForge.Coordinator;

public sealed record WorkerLaunch(string Executable, string[] Arguments, string Directory);
public sealed record WorkerBundle(string Engine, string Executable, string[] Arguments);

public static class WorkerCatalog
{
    public const string BundleVersion = "native-workers-v1";
    public static string Platform => (OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux")
        + "-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

    public static string SourceDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("BENCHFORGE_WORKERS");
        var candidates = new List<string> { configured ?? "" };
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "BenchForge.slnx"))) { candidates.Add(Path.Combine(directory.FullName, "workers")); break; }
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "workers"));
        if (OperatingSystem.IsLinux()) candidates.Add("/opt/benchforge-workers");
        return candidates.FirstOrDefault(path => Directory.Exists(path)) ?? throw new DirectoryNotFoundException("Не найдены исходники движков. Соберите BenchForge.slnx.");
    }

    public static WorkerLaunch Resolve(string engine)
    {
        if (engine == "csharp") return new(ProcessUtil.Dotnet(), [ProcessUtil.WorkerPath()], AppContext.BaseDirectory);
        if (!Engines.All.Any(item => item.Id == engine)) throw new ArgumentException("Неизвестный движок: " + engine);
        var root = SourceDirectory();
        WorkerBundle? bundle = null; string directory = "";
        foreach (var candidate in new[] { root, OperatingSystem.IsLinux() ? "/opt/benchforge-workers" : "" }.Where(Directory.Exists).Distinct())
        {
            directory = Path.Combine(candidate, "dist", Platform);
            var manifest = Path.Combine(directory, "engines.json");
            var bundles = File.Exists(manifest) ? JsonSerializer.Deserialize<WorkerBundle[]>(File.ReadAllText(manifest), Json.Options) ?? [] : [];
            bundle = bundles.SingleOrDefault(item => item.Engine == engine);
            if (bundle is not null) break;
        }
        if (bundle is null) throw new InvalidOperationException($"{Engines.Name(engine)} не подготовлен для {Platform}. Выполните workers/prepare.ps1 (Windows) или workers/prepare.sh (Linux/macOS).");
        var executable = bundle.Executable switch { "python" => OperatingSystem.IsWindows() ? "python" : "python3", _ => bundle.Executable };
        if (executable.StartsWith("./", StringComparison.Ordinal)) executable = Path.GetFullPath(Path.Combine(directory, executable[2..]));
        return new(executable, bundle.Arguments, directory);
    }

    public static async Task CheckAsync(string engine, CancellationToken token)
    {
        if (engine == "csharp") { _ = Resolve(engine); return; }
        var launch = Resolve(engine);
        var info = ProcessUtil.Info(launch.Executable, launch.Arguments.Concat(["--self-test"]));
        info.WorkingDirectory = launch.Directory;
        info.RedirectStandardInput = true;
        using var process = System.Diagnostics.Process.Start(info) ?? throw new InvalidOperationException("Не удалось запустить " + engine);
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            process.StandardInput.Close();
            if (process.ExitCode != 0) throw new InvalidOperationException(Engines.Name(engine) + ": " + (await error)[..Math.Min((await error).Length, 1000)]);
            WorkerConformance.Verify(await output);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException(Engines.Name(engine) + ": проверка запуска заняла больше минуты. Повторите её после завершения подготовки окружения."); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); } }
    }
}
