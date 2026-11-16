using System.Diagnostics;
using System.Text;

namespace BenchForge.Coordinator;

public static class ProcessUtil
{
    public static ProcessStartInfo Info(string executable, IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment = null)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        if (environment is not null) foreach (var (key, value) in environment) info.Environment[key] = value;
        return info;
    }
    public static async Task<string> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken token,
        int timeoutSeconds = 30, IReadOnlyDictionary<string, string>? environment = null)
    {
        using var process = Process.Start(Info(executable, arguments, environment)) ?? throw new InvalidOperationException("Не удалось запустить процесс.");
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); throw; }
        var text = await output; var error = await errors;
        if (process.ExitCode != 0)
        { var message = string.IsNullOrWhiteSpace(error) ? text : error; throw new InvalidOperationException(message.Length > 2000 ? message[^2000..] : message); }
        return text.Trim();
    }
    public static string Dotnet()
    {
        var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (configured is not null && File.Exists(configured)) return configured;
        if (OperatingSystem.IsWindows())
        { var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"); if (File.Exists(path)) return path; }
        if (OperatingSystem.IsMacOS())
            foreach (var path in new[] { "/usr/local/share/dotnet/dotnet", "/usr/local/bin/dotnet" }) if (File.Exists(path)) return path;
        if (OperatingSystem.IsLinux() && File.Exists("/usr/share/dotnet/dotnet")) return "/usr/share/dotnet/dotnet";
        return "dotnet";
    }
    public static string Docker()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var path in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DockerDesktop", "resources", "bin", "docker.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "resources", "bin", "docker.exe") })
                if (File.Exists(path)) return path;
        }
        if (OperatingSystem.IsMacOS())
            foreach (var path in new[] { "/usr/local/bin/docker", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".docker", "bin", "docker"),
                "/Applications/Docker.app/Contents/Resources/bin/docker" }) if (File.Exists(path)) return path;
        return "docker";
    }
    public static string WorkerPath()
    {
        var configured = Environment.GetEnvironmentVariable("DBLAB_CSHARP_WORKER");
        if (configured is not null && File.Exists(configured)) return Path.GetFullPath(configured);
        var shipped = Path.Combine(AppContext.BaseDirectory, "worker", "BenchForge.Worker.CSharp.dll");
        if (File.Exists(shipped)) return shipped;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "BenchForge.slnx"))) continue;
            foreach (var configuration in new[] { "Release", "Debug" })
            { var candidate = Path.Combine(directory.FullName, "src", "BenchForge.Worker.CSharp", "bin", configuration, "net10.0", "BenchForge.Worker.CSharp.dll"); if (File.Exists(candidate)) return candidate; }
        }
        throw new FileNotFoundException("Не найден C#-движок. Сначала соберите BenchForge.slnx в Release или задайте DBLAB_CSHARP_WORKER.");
    }
}
