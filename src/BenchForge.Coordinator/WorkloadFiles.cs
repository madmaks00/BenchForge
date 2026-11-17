using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BenchForge.Contracts;

namespace BenchForge.Coordinator;

public sealed record LoadedWorkload(SqlWorkload Workload, string Directory);
public sealed record ImportInspection(long Rows, string Sha256, List<ValueDefinition> Columns, char Delimiter = ',');

public static class WorkloadFiles
{
    public static IEnumerable<ImportedData> Imports(BenchmarkConfig config) => config.CustomWorkload?.Datasets.Where(table => table.Import is not null).Select(table => table.Import!).DistinctBy(import => import.File) ?? [];
    public static string SourcePath(BenchmarkConfig config, string file)
    {
        if (!SqlWorkload.SafeFile(file)) throw new ArgumentException("Некорректное имя файла сценария.");
        var root = Path.GetFullPath(config.WorkloadDirectory ?? throw new InvalidDataException("Укажите папку входных файлов сценария или загрузите пакет .bfworkload."));
        var path = Path.GetFullPath(Path.Combine(root, file));
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new IOException("Файл вне папки сценария.");
        for (var current = new FileInfo(path) as FileSystemInfo; current is not null && current.FullName != root; current = current is FileInfo item ? item.Directory : ((DirectoryInfo)current).Parent)
            if (current.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Ссылки на внешние файлы в пакете не поддерживаются.");
        return path;
    }
    public static async Task CheckAsync(BenchmarkConfig config, CancellationToken token)
    {
        foreach (var import in Imports(config))
        {
            await using var stream = File.OpenRead(SourcePath(config, import.File));
            if (!Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).Equals(import.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Файл изменён после импорта: " + import.File + ". Импортируйте его заново.");
        }
    }
    public static async Task<ImportInspection> InspectAsync(string path, ImportFormat format, CancellationToken token)
    {
        long count = 0; List<ValueDefinition>? columns = null; var delimiter = ',';
        if (format == ImportFormat.Csv)
        {
            using var reader = new StreamReader(path); var header = reader.ReadLine() ?? "";
            int Score(char candidate) { try { return WorkloadData.CsvRecords(new StringReader(header), token, candidate).FirstOrDefault()?.Length ?? 0; } catch (InvalidDataException) { return -1; } }
            delimiter = new[] { ',', ';', '\t' }.OrderByDescending(Score).First();
        }
        foreach (var row in WorkloadData.ReadFile(path, format, token, delimiter))
        {
            if (columns is null)
            {
                if (row.Count is < 1 or > 128 || row.Keys.Any(name => !SqlWorkload.Identifier(name))) throw new InvalidDataException("Для автоматического импорта нужны имена полей: латинские буквы, цифры и подчёркивания, без пробелов.");
                columns = row.Select(pair => new ValueDefinition { Name = pair.Key, Type = Infer(pair.Value), Generator = ValueGenerator.Constant, Nullable = true }).ToList();
            }
            if (++count > 1_000_000_000) throw new InvalidDataException("В одном файле допускается до миллиарда строк.");
            if (count <= 1000)
                for (var index = 0; index < columns.Count; index++)
                    if (row.TryGetValue(columns[index].Name, out var value) && Infer(value) != columns[index].Type) columns[index] = columns[index] with { Type = SqlValueType.Text };
        }
        if (columns is null) throw new InvalidDataException("В файле нет записей. Для пустой таблицы используйте генератор с фиксированным числом строк 0.");
        await using var stream = File.OpenRead(path);
        return new(count, Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant(), columns, delimiter);
    }
    private static SqlValueType Infer(object? value)
    {
        var text = value is JsonElement json ? json.ValueKind == JsonValueKind.String ? json.GetString() : json.GetRawText() : value?.ToString();
        if (long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _)) return SqlValueType.Int64;
        if (decimal.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)) return SqlValueType.Decimal;
        if (bool.TryParse(text, out _)) return SqlValueType.Boolean;
        return SqlValueType.Text;
    }
    public static async Task CreateArchiveAsync(BenchmarkConfig config, string archive, CancellationToken token)
    {
        await CheckAsync(config, token);
        await using var file = File.Create(archive);
        await using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        using var tar = new TarWriter(gzip, leaveOpen: true);
        foreach (var import in Imports(config)) { token.ThrowIfCancellationRequested(); await tar.WriteEntryAsync(SourcePath(config, import.File), import.File, token); }
    }
    public static async Task CopyInputsAsync(BenchmarkConfig config, string directory, CancellationToken token)
    {
        await CheckAsync(config, token);
        foreach (var import in Imports(config))
        {
            var source = SourcePath(config, import.File); var target = Path.Combine(Path.GetFullPath(directory), import.File);
            if (File.Exists(target))
            {
                await using var existing = File.OpenRead(target);
                if (Convert.ToHexString(await SHA256.HashDataAsync(existing, token)).Equals(import.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
                throw new IOException("В выбранной папке уже есть другой файл " + import.File + ". Сохраните настройки в новую папку или экспортируйте .bfworkload.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var created = false;
            try
            {
                await using var input = File.OpenRead(source); await using var output = new FileStream(target, FileMode.CreateNew);
                created = true;
                await input.CopyToAsync(output, token);
            }
            catch { if (created) File.Delete(target); throw; }
        }
    }
    public static async Task<LoadedWorkload> OpenAsync(string path, CancellationToken token)
    {
        if (!path.EndsWith(".bfworkload", StringComparison.OrdinalIgnoreCase))
        {
            var workload = JsonSerializer.Deserialize<SqlWorkload>(await File.ReadAllTextAsync(path, token), Json.Options) ?? throw new InvalidDataException("Пустой сценарий.");
            workload.Validate(); return new(workload, Path.GetDirectoryName(Path.GetFullPath(path))!);
        }
        string hash;
        await using (var stream = File.OpenRead(path)) hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BenchForge", "workloads", hash);
        var temporary = cache + "." + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(temporary);
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var metadata = zip.GetEntry("workload.json") ?? throw new InvalidDataException("В пакете нет workload.json.");
            if (metadata.Length > 512 * 1024) throw new InvalidDataException("Описание сценария больше 512 КБ.");
            SqlWorkload workload;
            await using (var stream = metadata.Open()) workload = await JsonSerializer.DeserializeAsync<SqlWorkload>(stream, Json.Options, token) ?? throw new InvalidDataException("Пустое описание сценария.");
            workload.Validate();
            var allowed = workload.Datasets.Where(table => table.Import is not null).Select(table => table.Import!.File).Append("workload.json").ToHashSet(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
            foreach (var entry in zip.Entries)
            {
                if (!allowed.Contains(entry.FullName) || !seen.Add(entry.FullName) || !SqlWorkload.SafeFile(entry.FullName) || (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000)
                    throw new InvalidDataException("Недопустимый или повторяющийся файл в пакете сценария.");
                if ((total += entry.Length) > 128L * 1024 * 1024 * 1024) throw new InvalidDataException("Пакет больше 128 ГБ. Подключите исходные файлы рядом с JSON-сценарием.");
                var destination = Path.Combine(temporary, entry.FullName); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var output = File.Create(destination); await using var input = entry.Open(); await input.CopyToAsync(output, token);
            }
            if (seen.Count != allowed.Count) throw new InvalidDataException("В пакете не хватает входных файлов.");
            await CheckAsync(new() { CustomWorkload = workload, WorkloadDirectory = temporary }, token);
            Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
            if (!Directory.Exists(cache)) Directory.Move(temporary, cache);
            return new(workload, cache);
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }
    public static async Task SavePackageAsync(SqlWorkload workload, string? directory, string destination, CancellationToken token)
    {
        workload.Validate(); var config = new BenchmarkConfig { CustomWorkload = workload, WorkloadDirectory = directory };
        await CheckAsync(config, token);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("workload.json");
                await using (var stream = entry.Open()) await JsonSerializer.SerializeAsync(stream, workload, Json.Pretty, token);
                foreach (var import in Imports(config))
                { token.ThrowIfCancellationRequested(); zip.CreateEntryFromFile(SourcePath(config, import.File), import.File, CompressionLevel.Fastest); }
            }
            File.Move(temporary, destination, true);
        }
        finally { File.Delete(temporary); }
    }
    public static async Task<string> ImportFileAsync(string path, string table, CancellationToken token, string? storageRoot = null)
    {
        if (!SqlWorkload.Identifier(table)) throw new ArgumentException("Сначала задайте имя таблицы.");
        var extension = Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase) ? ".csv" : ".jsonl";
        var directory = Path.Combine(storageRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BenchForge", "workloads"), "editor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "data"));
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!);
            if (drive.IsReady && drive.AvailableFreeSpace < new FileInfo(path).Length + 64L * 1024 * 1024) throw new IOException("Недостаточно места для копии данных. Выберите другое хранилище файлов на вкладке настроек сценария.");
            await using var input = File.OpenRead(path); await using var output = File.Create(Path.Combine(directory, "data", table + extension)); await input.CopyToAsync(output, token);
            return directory;
        }
        catch { Directory.Delete(directory, true); throw; }
    }
}
