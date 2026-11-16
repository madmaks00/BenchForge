using System.Text.Json;
using BenchForge.Contracts;

namespace BenchForge.Coordinator;

public sealed class VirtualMachineTemplate(IVmCommandClient? commands = null)
{
    private readonly IVmCommandClient _commands = commands ?? new MultipassClient();
    private const string Marker = "benchforge-template-v1";
    private const string Bootstrap = """
        set -eu
        export DEBIAN_FRONTEND=noninteractive
        apt-get update
        apt-get install -y docker.io curl ca-certificates libicu74
        systemctl enable --now docker
        curl --fail --location --retry 3 https://dot.net/v1/dotnet-install.sh -o /tmp/benchforge-dotnet-install.sh
        bash /tmp/benchforge-dotnet-install.sh --channel 10.0 --runtime dotnet --install-dir /opt/benchforge-dotnet
        ln -sf /opt/benchforge-dotnet/dotnet /usr/local/bin/dotnet
        systemctl disable --now apt-daily.timer apt-daily-upgrade.timer
        printf '%s\n' benchforge-template-v1 > /etc/benchforge-template
        """;

    public async Task CheckAsync(VirtualMachineOptions options, CancellationToken token)
    {
        options.Validate();
        _ = ProcessUtil.VmAgentDirectory();
        await _commands.RunAsync(["help", "clone"], token);
        var info = await InstanceAsync(options.Template, token);
        if (info is null) throw new InvalidOperationException("Подготовьте шаблон VM кнопкой «Подготовить шаблон».");
        if (info.Value.GetProperty("state").GetString() != "Stopped")
            throw new InvalidOperationException("Шаблон VM должен быть выключен перед клонированием.");
        var manifest = ManifestPath(options.Template);
        if (!File.Exists(manifest) || JsonSerializer.Deserialize<VirtualMachineOptions>(await File.ReadAllTextAsync(manifest, token), Json.Options) != options)
            throw new InvalidOperationException("Подготовьте шаблон с выбранными CPU, памятью и диском. Для другого шаблона задайте новое имя.");
    }

    public async Task PrepareAsync(VirtualMachineOptions options, IEnumerable<string> images, Action<string> log, CancellationToken token)
    {
        options.Validate();
        await _commands.RunAsync(["help", "clone"], token);
        if (await InstanceAsync(options.Template, token) is not null)
        {
            await CheckAsync(options, token);
            log("Шаблон уже подготовлен.");
            return;
        }
        var folder = Path.Combine(Path.GetTempPath(), "benchforge-template-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var created = false;
        try
        {
            var setup = Path.Combine(folder, "setup.sh");
            await File.WriteAllTextAsync(setup, Bootstrap.Replace("\r\n", "\n"), token);
            var cloudInit = Path.Combine(folder, "cloud-init.yaml");
            await File.WriteAllTextAsync(cloudInit, "#cloud-config\ntimezone: Etc/UTC\n", token);
            log("Загрузка Ubuntu 24.04 и создание шаблона…");
            // Finish creation before honouring cancellation, so the new disk can be cleaned up.
            await _commands.RunAsync(["launch", "24.04", "--name", options.Template, "--cpus", options.Cpus.ToString(),
                "--memory", options.MemoryMb + "M", "--disk", options.DiskGb + "G", "--cloud-init", cloudInit, "--timeout", "600"], CancellationToken.None, 900);
            created = true;
            token.ThrowIfCancellationRequested();
            await _commands.RunAsync(["exec", options.Template, "--", "sudo", "-n", "cloud-init", "status", "--wait", "--long"], token, 600);
            await _commands.RunAsync(["transfer", setup, options.Template + ":/home/ubuntu/setup.sh"], token);
            log("Установка Docker и .NET 10 в шаблон…");
            await _commands.RunAsync(["exec", options.Template, "--", "sudo", "-n", "bash", "/home/ubuntu/setup.sh"], token, 1800);
            foreach (var image in images.Distinct())
            {
                log("Подготовка образа " + image);
                await _commands.RunAsync(["exec", options.Template, "--", "sudo", "-n", "docker", "pull", image], token, 1800);
            }
            await _commands.RunAsync(["exec", options.Template, "--", "sudo", "-n", "sync"], token);
            await _commands.RunAsync(["stop", options.Template], token);
            var manifest = ManifestPath(options.Template);
            Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
            await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(options, Json.Pretty), token);
            log("Шаблон подготовлен и выключен.");
        }
        catch
        {
            if (created) await DeleteAsync(options.Template, CancellationToken.None);
            throw;
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    internal async Task<JsonElement?> InstanceAsync(string name, CancellationToken token)
    {
        using var list = JsonDocument.Parse(await _commands.RunAsync(["list", "--format", "json"], token));
        foreach (var item in list.RootElement.GetProperty("list").EnumerateArray())
            if (item.GetProperty("name").GetString() == name) return item.Clone();
        return null;
    }

    internal async Task DeleteAsync(string name, CancellationToken token)
    {
        var instance = await InstanceAsync(name, token);
        if (instance is null) return;
        var state = instance.Value.GetProperty("state").GetString();
        if (state is not ("Stopped" or "Deleted")) await _commands.RunAsync(["stop", name], token);
        var help = await _commands.RunAsync(["help", "delete"], token);
        await _commands.RunAsync(["delete", help.Contains("--purge", StringComparison.Ordinal) ? "--purge" : "--force", name], token);
    }

    internal static string TemplateMarker => Marker;
    private static string ManifestPath(string name)
    {
        var root = Environment.GetEnvironmentVariable("BENCHFORGE_VM_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BenchForge", "virtual-machines");
        return Path.Combine(root, name + ".json");
    }
}
