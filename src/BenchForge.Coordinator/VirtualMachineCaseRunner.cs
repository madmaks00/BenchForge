using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using BenchForge.Contracts;

namespace BenchForge.Coordinator;

public interface IVirtualMachineCaseRunner
{
    Task CheckAsync(VirtualMachineOptions options, CancellationToken token);
    Task CheckEnginesAsync(VirtualMachineOptions options, IReadOnlyCollection<string> engines, CancellationToken token) => Task.CompletedTask;
    Task<BenchmarkReport> RunAsync(BenchmarkConfig config, TargetKind target, long rows, int clients, string caseId,
        string reportFolder, Func<RunUpdate, Task> receive, CancellationToken token);
}

public sealed class VirtualMachineCleanupException(string message, Exception inner) : Exception(message, inner);

public sealed class VirtualMachineCaseRunner(IVmCommandClient? commands = null) : IVirtualMachineCaseRunner
{
    private readonly IVmCommandClient _commands = commands ?? new MultipassClient();
    private const string GuestRoot = "/home/ubuntu/benchforge";

    public async Task CheckAsync(VirtualMachineOptions options, CancellationToken token)
    {
        await new VirtualMachineTemplate(_commands).CheckAsync(options, token);
        await VirtualMachineWorkers.CheckStorageAsync(options, token);
    }
    public Task CheckEnginesAsync(VirtualMachineOptions options, IReadOnlyCollection<string> engines, CancellationToken token) => VirtualMachineWorkers.CheckAsync(options, engines, token);

    public async Task<BenchmarkReport> RunAsync(BenchmarkConfig config, TargetKind target, long rows, int clients, string caseId,
        string reportFolder, Func<RunUpdate, Task> receive, CancellationToken token)
    {
        var options = config.VirtualMachine ?? throw new ArgumentException("Не заданы параметры VM.");
        if (!Guid.TryParseExact(caseId, "N", out _)) throw new ArgumentException("Некорректный ID прогона VM.");
        var template = new VirtualMachineTemplate(_commands);
        var name = "benchforge-case-" + caseId;
        if (await template.InstanceAsync(name, token) is not null) throw new InvalidOperationException("Имя тестовой VM уже занято.");
        var temp = Path.Combine(Path.GetTempPath(), name);
        Directory.CreateDirectory(temp);
        var attempted = false;
        try
        {
            await receive(new("log", "Создание чистой VM из шаблона " + options.Template));
            attempted = true;
            // Complete the clone RPC before cancellation so its disk can always be removed.
            await _commands.RunAsync(["clone", options.Template, "--name", name], CancellationToken.None, 600);
            token.ThrowIfCancellationRequested();
            await _commands.RunAsync(["start", name, "--timeout", "300"], token, 360);
            await receive(new("log", "Ожидание завершения настройки гостевой ОС"));
            await _commands.RunAsync(["exec", name, "--", "sudo", "-n", "cloud-init", "status", "--wait", "--long"], token, 600);
            var marker = await _commands.RunAsync(["exec", name, "--", "cat", "/etc/benchforge-template"], token);
            if (marker != VirtualMachineTemplate.TemplateMarker) throw new InvalidDataException("VM создана из неподготовленного шаблона.");
            var bootId = await _commands.RunAsync(["exec", name, "--", "cat", "/proc/sys/kernel/random/boot_id"], token);
            if (!Guid.TryParse(bootId, out _)) throw new InvalidDataException("Не удалось определить ID загрузки гостевой ОС.");
            await _commands.RunAsync(["exec", name, "--", "mkdir", "-p", GuestRoot + "/agent", GuestRoot + "/reports"], token);
            var archive = Path.Combine(temp, "agent.tar.gz");
            await using (var file = File.Create(archive))
            await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
            {
                var agent = ProcessUtil.VmAgentDirectory();
                using var tar = new TarWriter(gzip, leaveOpen: true);
                foreach (var source in Directory.EnumerateFiles(agent, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(agent, source).Replace('\\', '/');
                    // Linux bundles are already prepared in the stopped template.
                    if (relative.StartsWith("workers/", StringComparison.Ordinal)) continue;
                    tar.WriteEntry(source, relative);
                }
            }
            var guestConfig = config with { VirtualMachine = null, Targets = [target], DatasetRows = [rows], Clients = [clients], Repeats = 1 };
            var configFile = Path.Combine(temp, "config.json");
            await File.WriteAllTextAsync(configFile, JsonSerializer.Serialize(guestConfig, Json.Pretty), token);
            await _commands.RunAsync(["transfer", archive, configFile, name + ":" + GuestRoot + "/"], token, 300);
            await _commands.RunAsync(["exec", name, "--", "tar", "-xzf", GuestRoot + "/agent.tar.gz", "-C", GuestRoot + "/agent"], token);
            string? guestFolder = null;
            var cancelFile = GuestRoot + "/cancel";
            var exit = await _commands.RunGuestAsync(["exec", name, "--", "sudo", "-n", "dotnet", GuestRoot + "/agent/BenchForge.Cli.dll",
                "--config", GuestRoot + "/config.json", "--reports", GuestRoot + "/reports", "--events", "--cancel-file", cancelFile], async update =>
            {
                if (update.Type == "started" && update.Folder is { } folder)
                {
                    if (!folder.StartsWith(GuestRoot + "/reports/", StringComparison.Ordinal) || folder.Contains("..", StringComparison.Ordinal))
                        throw new InvalidDataException("Агент VM вернул некорректную папку отчёта.");
                    guestFolder = folder;
                }
                await receive(update);
            }, async () => { await _commands.RunAsync(["exec", name, "--", "touch", cancelFile], CancellationToken.None, 30); }, token);
            if (guestFolder is null)
            {
                token.ThrowIfCancellationRequested();
                throw new InvalidDataException("Агент VM завершился без отчёта. Код " + exit);
            }
            // Collect a partial report even after cancellation, before deleting the VM.
            var collected = Path.Combine(temp, "collected");
            Directory.CreateDirectory(collected);
            var reportArchive = GuestRoot + "/report.tar.gz";
            await _commands.RunAsync(["exec", name, "--", "sh", "-c", "cd \"$1\" && tar -czf \"$2\" -- report.json resources-*.json", "--", guestFolder, reportArchive], CancellationToken.None, 300);
            var download = Path.Combine(temp, "report.tar.gz");
            try { await _commands.RunAsync(["transfer", name + ":" + reportArchive, download], CancellationToken.None, 300); }
            catch (InvalidOperationException error) when (OperatingSystem.IsWindows() && File.Exists(download) &&
                error.Message.Contains("cannot set permissions for local file", StringComparison.Ordinal))
            {
                // Multipass can copy all bytes and then fail applying Unix modes on Windows.
                // Accept that specific error only after checking the downloaded bytes below.
            }
            var digest = await _commands.RunAsync(["exec", name, "--", "sha256sum", reportArchive], CancellationToken.None);
            byte[] actualDigest;
            await using (var file = File.OpenRead(download)) actualDigest = await SHA256.HashDataAsync(file);
            if (!string.Equals(Convert.ToHexString(actualDigest), digest.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Архив отчёта VM передан не полностью: контрольная сумма не совпала.");
            await using (var file = File.OpenRead(download))
            await using (var gzip = new GZipStream(file, CompressionMode.Decompress))
                await TarFile.ExtractToDirectoryAsync(gzip, collected, overwriteFiles: false);
            var guestCopy = collected;
            var guestReport = JsonSerializer.Deserialize<BenchmarkReport>(await File.ReadAllTextAsync(Path.Combine(guestCopy, "report.json")), Json.Options)
                ?? throw new InvalidDataException("Пустой отчёт VM.");
            if (guestReport.Config.Targets.Count != 1 || guestReport.Config.Targets[0] != target || guestReport.Results.Any(row => row.Target != target || row.DatasetRows != rows))
                throw new InvalidDataException("VM вернула результаты другого теста.");
            foreach (var file in Directory.EnumerateFiles(guestCopy, "resources-*.json"))
                File.Copy(file, Path.Combine(reportFolder, Path.GetFileName(file)), overwrite: false);
            foreach (var metadata in guestReport.Engines.Values)
            {
                metadata.Settings["isolation"] = "fresh virtual machine per case";
                metadata.Settings["vm_template"] = options.Template;
                metadata.Settings["vm_instance"] = name;
                metadata.Settings["vm_boot_id"] = bootId;
                metadata.Settings["vm_cpus"] = options.Cpus.ToString();
                metadata.Settings["vm_memory_mb"] = options.MemoryMb.ToString();
            }
            return guestReport;
        }
        finally
        {
            try
            {
                if (attempted)
                {
                    await receive(new("log", "Удаление тестовой VM " + name));
                    await template.DeleteAsync(name, CancellationToken.None);
                }
            }
            catch (Exception error)
            {
                throw new VirtualMachineCleanupException("Не удалось удалить тестовую VM " + name + ". Эксперимент остановлен, чтобы избежать влияния оставшейся машины.", error);
            }
            finally { Directory.Delete(temp, recursive: true); }
        }
    }
}
