using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using BenchForge.Contracts;

namespace BenchForge.Coordinator;

internal static class VirtualMachineWorkers
{
    private sealed record Prepared(string Version, string[] Engines, long DiskUsedBytes = 0);
    private static string Manifest(string template)
    {
        var root = Environment.GetEnvironmentVariable("BENCHFORGE_VM_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BenchForge", "virtual-machines");
        return Path.Combine(root, template + ".workers.json");
    }
    internal static async Task CheckAsync(VirtualMachineOptions options, IReadOnlyCollection<string> engines, CancellationToken token)
    {
        if (engines.All(id => id == "csharp")) return;
        var path = Manifest(options.Template);
        var prepared = File.Exists(path) ? JsonSerializer.Deserialize<Prepared>(await File.ReadAllTextAsync(path, token), Json.Options) : null;
        if (prepared?.Version != WorkerCatalog.BundleVersion || engines.Any(id => !prepared.Engines.Contains(id)))
            throw new InvalidOperationException("Для выбранных языков обновите шаблон VM кнопкой «Подготовить шаблон» в окружении тестирования.");
    }
    internal static void Invalidate(VirtualMachineOptions options)
    {
        try { File.Delete(Manifest(options.Template)); }
        catch (DirectoryNotFoundException) { /* A fresh installation has no cache to invalidate. */ }
    }

    internal static async Task CheckStorageAsync(VirtualMachineOptions options, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(Manifest(options.Template))) return;
        var prepared = JsonSerializer.Deserialize<Prepared>(await File.ReadAllTextAsync(Manifest(options.Template), token), Json.Options);
        if (prepared is not { DiskUsedBytes: > 0 }) return;
        var storage = Environment.GetEnvironmentVariable("MULTIPASS_STORAGE") ?? Environment.GetEnvironmentVariable("MULTIPASS_STORAGE", EnvironmentVariableTarget.Machine)
            ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(storage))!);
        var required = prepared.DiskUsedBytes * 2 + 1024L * 1024 * 1024;
        if (drive.AvailableFreeSpace < required)
            throw new IOException($"Для копирования шаблона на {drive.Name} мало места: свободно {drive.AvailableFreeSpace / 1073741824.0:F1} ГБ, оценка необходимого места — от {required / 1073741824.0:F1} ГБ. Multipass/Hyper-V создаёт временный экспорт и рабочую копию. Освободите место или выберите обычный запуск на этом компьютере.");
    }
    internal static async Task PrepareAsync(IVmCommandClient commands, VirtualMachineOptions options, string folder, Action<string> log, CancellationToken token)
    {
        var source = WorkerCatalog.SourceDirectory();
        var staged = Path.Combine(folder, "workers");
        Directory.CreateDirectory(staged);
        var excluded = new[] { "dist", "node_modules", "target", "__pycache__", ".venv" };
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => excluded.Contains(part)) || relative.EndsWith(".pyc", StringComparison.Ordinal)) continue;
            var destination = Path.Combine(staged, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (relative.EndsWith(".sh", StringComparison.Ordinal))
                await File.WriteAllTextAsync(destination, (await File.ReadAllTextAsync(file, token)).Replace("\r\n", "\n"), token);
            else File.Copy(file, destination);
        }
        var archive = Path.Combine(folder, "worker-sources.tar.gz");
        await using (var output = File.Create(archive))
        await using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
            TarFile.CreateFromDirectory(staged, gzip, includeBaseDirectory: false);
        log("Подготовка Java, Python, Rust, Node.js и Go в шаблоне VM…");
        await commands.RunAsync(["transfer", archive, options.Template + ":/home/ubuntu/worker-sources.tar.gz"], token, 300);
        await commands.RunAsync(["exec", options.Template, "--", "sudo", "-n", "mkdir", "-p", "/opt/benchforge-workers"], token);
        await commands.RunAsync(["exec", options.Template, "--", "sudo", "-n", "tar", "-xzf", "/home/ubuntu/worker-sources.tar.gz", "-C", "/opt/benchforge-workers"], token, 300);
        await commands.RunAsync(["exec", options.Template, "--", "sudo", "-n", "bash", "/opt/benchforge-workers/install-linux.sh"], token, 3600);
    }
    internal static async Task SaveAsync(VirtualMachineOptions options, long diskUsedBytes, CancellationToken token)
    {
        var path = Manifest(options.Template);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new Prepared(WorkerCatalog.BundleVersion, Engines.All.Select(engine => engine.Id).ToArray(), diskUsedBytes), Json.Pretty), token);
    }
}
