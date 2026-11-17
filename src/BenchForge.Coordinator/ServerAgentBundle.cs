using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using BenchForge.Contracts;

namespace BenchForge.Coordinator;

public sealed record ServerAgentPackage(string Archive, string SourceHash, string Engines, string Image);

public static class ServerAgentBundle
{
    public const string Version = "server-v1";

    public static string SourceDirectory()
    {
        var candidates = new List<string> { Environment.GetEnvironmentVariable("BENCHFORGE_AGENT_SOURCE") ?? "" };
        for (var parent = new DirectoryInfo(AppContext.BaseDirectory); parent is not null; parent = parent.Parent)
            if (File.Exists(Path.Combine(parent.FullName, "BenchForge.slnx"))) { candidates.Add(parent.FullName); break; }
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "server-agent", "source"));
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(Path.Combine(path, "server", "Agent.Dockerfile")))
            ?? throw new DirectoryNotFoundException("Не найден комплект серверного агента. Соберите BenchForge.slnx.");
    }

    public static IReadOnlyList<string> Files(string root)
    {
        var output = new List<string>();
        void Add(string relative)
        {
            var full = Path.GetFullPath(Path.Combine(root, relative));
            if (!full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Путь вне исходников агента.");
            if (!File.Exists(full)) throw new FileNotFoundException("В комплекте агента отсутствует " + relative);
            if (File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Символические ссылки в комплекте агента не поддерживаются: " + relative);
            output.Add(relative.Replace('\\', '/'));
        }
        foreach (var file in new[] { "Directory.Build.props", "Directory.Build.targets", "global.json", "NuGet.Config",
            "server/Agent.Dockerfile", "server/agentctl.sh", "server/entrypoint.sh", "workers/prepare.sh", "workers/install-linux.sh", "workers/native-libraries.py" }) Add(file);
        foreach (var name in new[] { "BenchForge.Contracts", "BenchForge.Coordinator", "BenchForge.Cli", "BenchForge.Worker.CSharp" })
        {
            var directory = Path.Combine(root, "src", name);
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file);
                if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "bin" or "obj")) continue;
                if (Path.GetExtension(file) is ".cs" or ".csproj" || Path.GetFileName(file) == "packages.lock.json") Add(relative);
            }
        }
        foreach (var language in new[] { "java", "python", "rust", "node", "go" })
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "workers", language), "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file);
            if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "target" or "node_modules" or "__pycache__" or ".venv")) continue;
            if (Path.GetExtension(file) is ".java" or ".py" or ".rs" or ".mjs" or ".go" ||
                Path.GetFileName(file) is "pom.xml" or "requirements.lock.txt" or "Cargo.toml" or "Cargo.lock" or "go.mod" or "go.sum" or "package.json" or "package-lock.json") Add(relative);
        }
        return output.Distinct().Order(StringComparer.Ordinal).ToArray();
    }

    public static async Task<ServerAgentPackage> CreateAsync(IReadOnlyCollection<string> engines, CancellationToken token, string? source = null, string? cache = null)
    {
        if (engines.Count == 0 || engines.Any(id => !Engines.All.Any(engine => engine.Id == id))) throw new ArgumentException("Неизвестный движок агента.");
        var selected = string.Join(',', Engines.All.Where(engine => engines.Contains(engine.Id)).Select(engine => engine.Id));
        var root = source ?? SourceDirectory(); var files = Files(root);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var relative in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(relative + "\0"));
            var content = await File.ReadAllBytesAsync(Path.Combine(root, relative), token);
            hash.AppendData(BitConverter.GetBytes(content.LongLength)); hash.AppendData(content);
        }
        var digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        var languages = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(selected))).ToLowerInvariant()[..8];
        var folder = cache ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BenchForge", "server-bundles");
        Directory.CreateDirectory(folder);
        var archive = Path.Combine(folder, digest + ".tar.gz");
        if (!File.Exists(archive))
        {
            var temporary = archive + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var file = File.Create(temporary))
                await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
                using (var tar = new TarWriter(gzip, leaveOpen: true))
                    foreach (var relative in files) { token.ThrowIfCancellationRequested(); tar.WriteEntry(Path.Combine(root, relative), relative); }
                File.Move(temporary, archive, overwrite: true);
            }
            finally { File.Delete(temporary); }
        }
        return new(archive, digest, selected, "benchforge-agent:v1-" + digest + "-" + languages);
    }
}
