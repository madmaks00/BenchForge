using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BenchForge.Contracts;

public enum WorkloadMode { Mix, Separate, SeparateAndMix }
public enum SqlValueType { Int64, Int32, Decimal, Double, Text, Boolean, DateTime, Guid }
public enum ValueGenerator { Sequence, Integer, Decimal, Text, Choice, Reference, Guid, Timestamp, Boolean, Constant, Key, Client, Sample }
public enum ImportFormat { Csv, JsonLines }
public enum SqlResultMode { Rows, NonQuery, Scalar }
public enum CheckComparison { Equal, AtLeast, AtMost }
public enum SqlIsolation { Default, ReadCommitted, RepeatableRead, Serializable }

public sealed record ValueDefinition
{
    public string Name { get; init; } = "id";
    public SqlValueType Type { get; init; } = SqlValueType.Int64;
    public ValueGenerator Generator { get; init; } = ValueGenerator.Sequence;
    public decimal Min { get; init; } = 1;
    public decimal Max { get; init; } = 1000;
    public int Length { get; init; } = 32;
    public int Scale { get; init; } = 2;
    public string Prefix { get; init; } = "";
    public List<string> Choices { get; init; } = [];
    public string? Reference { get; init; }
    public string SampleColumn { get; init; } = "id";
    public int SampleSize { get; init; } = 10000;
    public string? Constant { get; init; }
    public double NullPercent { get; init; }
    public bool Nullable { get; init; }
    public bool PrimaryKey { get; init; }
    public bool Unique { get; init; }
}

public sealed record ImportedData(string File, ImportFormat Format, string Sha256, long Rows, char Delimiter = ',');

public sealed record SqlDataset
{
    public string Name { get; init; } = "items";
    public List<ValueDefinition> Columns { get; init; } = [];
    public long? FixedRows { get; init; }
    public int RowsMultiplier { get; init; } = 1;
    public ImportedData? Import { get; init; }
    public long RowCount(long baseRows) => Import?.Rows ?? FixedRows ?? checked(baseRows * RowsMultiplier);
}

public sealed record SqlText
{
    public string Sql { get; init; } = "";
    public Dictionary<TargetKind, string> Overrides { get; init; } = [];
    public string For(TargetKind target) => Overrides.GetValueOrDefault(target, Sql);
}

public sealed record SqlStep
{
    public SqlText Command { get; init; } = new();
    public SqlResultMode Result { get; init; } = SqlResultMode.Rows;
    public long? MinRows { get; init; }
    public long? MaxRows { get; init; }
    public string? ExpectedScalar { get; init; }
    public string? CaptureAs { get; init; }
    public SqlValueType CaptureType { get; init; } = SqlValueType.Int64;
}

public sealed record SqlOperation
{
    public string Id { get; init; } = "read";
    public string Name { get; init; } = "Мой запрос";
    public bool Enabled { get; init; } = true;
    public int Weight { get; init; } = 1;
    public bool ReadOnly { get; init; } = true;
    public bool Transaction { get; init; }
    public SqlIsolation Isolation { get; init; }
    public List<ValueDefinition> Parameters { get; init; } = [];
    public List<SqlStep> Steps { get; init; } = [];
}

public sealed record SqlCheck
{
    public string Name { get; init; } = "Проверка";
    public SqlText Command { get; init; } = new();
    public CheckComparison Comparison { get; init; }
    // A number, "baseRows", or "rows:dataset_name".
    public string Expected { get; init; } = "0";
    public bool Before { get; init; } = true;
    public bool After { get; init; } = true;
}

public sealed record SqlWorkload
{
    public int Version { get; init; } = 1;
    public string Name { get; init; } = "Свой SQL-сценарий";
    public string Description { get; init; } = "";
    public WorkloadMode Mode { get; init; } = WorkloadMode.Mix;
    public bool ResetBetweenOperations { get; init; } = true;
    public bool PrepareCommands { get; init; } = true;
    public int CommandTimeoutSeconds { get; init; } = 10;
    // Empty schema generates DDL from dataset columns, including keys and references.
    public SqlText Schema { get; init; } = new();
    public SqlText AfterLoad { get; init; } = new();
    public List<SqlDataset> Datasets { get; init; } = [];
    public List<SqlOperation> Operations { get; init; } = [];
    public List<SqlCheck> Checks { get; init; } = [];
    [JsonIgnore] public string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, Json.Options)))).ToLowerInvariant();
    [JsonIgnore] public IEnumerable<SqlOperation> ActiveOperations => Operations.Where(operation => operation.Enabled);
    public static bool Supports(TargetKind target) => target is TargetKind.PostgreSql or TargetKind.MySql or TargetKind.MariaDb or TargetKind.Sqlite;
    public static bool Identifier(string? name) => name is not null && Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]{0,62}$");

    public void Validate(BenchmarkConfig? config = null)
    {
        void Require(bool condition, string message) { if (!condition) throw new ArgumentException(message); }
        Require(Version == 1 && !string.IsNullOrWhiteSpace(Name) && Name.Length <= 120 && Description.Length <= 4000, "Укажите имя SQL-сценария; поддерживается версия 1.");
        Require(Enum.IsDefined(Mode) && CommandTimeoutSeconds is >= 1 and <= 3600, "Проверьте режим сценария и таймаут команд (1–3600 с).");
        Require(Datasets is { Count: > 0 and <= 64 } && Operations is { Count: > 0 and <= 64 } && Checks is { Count: <= 128 }, "Сценарию нужны таблицы и операции (до 64 каждого вида).");
        Require(Datasets.Select(table => table.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == Datasets.Count, "Имена таблиц должны различаться.");
        Require(Datasets.Where(table => table.Import is not null).Select(table => table.Import!).GroupBy(import => import.File).All(group => group.Select(import => (import.Sha256.ToLowerInvariant(), import.Format, import.Rows)).Distinct().Count() == 1), "Один входной файл должен иметь одинаковые SHA-256, формат и число строк.");
        foreach (var table in Datasets)
        {
            Require(Identifier(table.Name) && table.Columns is { Count: > 0 and <= 128 }, "Для таблицы нужны имя и столбцы (до 128).");
            Require(table.RowsMultiplier is >= 1 and <= 10000 && table.FixedRows is not < 0, "Проверьте число строк и множитель таблицы " + table.Name);
            Require(table.Columns.Count(column => column.PrimaryKey) <= 1, "Автоматическая схема поддерживает один столбец первичного ключа; составной ключ задайте в SQL схемы.");
            ValidateValues(table.Columns);
            Require(table.Columns.All(column => column.Generator != ValueGenerator.Sample), "Sample используется в параметрах запросов, а не при создании таблицы.");
            if (table.Import is { } import)
                Require(SafeFile(import.File) && import.File != "workload.json" && Enum.IsDefined(import.Format) && import.Delimiter is ',' or ';' or '\t' && import.Rows >= 0 && Regex.IsMatch(import.Sha256 ?? "", "^[A-Fa-f0-9]{64}$"), "Проверьте файл, разделитель, число строк и SHA-256 импорта таблицы " + table.Name);
            foreach (var column in table.Columns.Where(column => column.Generator == ValueGenerator.Reference))
                Require(Datasets.TakeWhile(other => other != table).Any(other => other.Name == column.Reference && other.Columns.Any(key => key.PrimaryKey && key.Type is SqlValueType.Int64 or SqlValueType.Int32 && (other.Import is not null || key.Generator == ValueGenerator.Sequence && key.Min == 1))),
                    "Ссылка должна вести к предшествующей таблице с числовым ключом 1..N: " + column.Name);
            if (config is not null) foreach (var rows in config.DatasetRows) { var count = table.RowCount(rows); Require(count <= 1_000_000_000, "Для одной таблицы допускается до миллиарда строк."); }
        }
        Require(Operations.Select(operation => operation.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == Operations.Count && ActiveOperations.Any(), "Нужны уникальные ID и хотя бы одна включённая операция.");
        foreach (var operation in Operations)
        {
            Require(Identifier(operation.Id) && !string.IsNullOrWhiteSpace(operation.Name) && operation.Name.Length <= 120 && operation.Weight is >= 1 and <= 1000000,
                "Проверьте ID, название и вес операции.");
            Require(Enum.IsDefined(operation.Isolation) && operation.Steps is { Count: > 0 and <= 64 }, "Операции нужны SQL-шаги (до 64).");
            ValidateValues(operation.Parameters);
            var names = operation.Parameters.Select(parameter => parameter.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var builtin in new[] { "key", "sequence", "client", "baseRows", "seed", "completed" }) Require(!names.Contains(builtin), "Зарезервированный параметр: " + builtin);
            var available = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            available.UnionWith(["key", "sequence", "client", "baseRows", "seed", "completed"]);
            available.UnionWith(Datasets.Select(table => "rows_" + table.Name));
            available.UnionWith(Operations.Select(operation => "completed_" + operation.Id));
            foreach (var step in operation.Steps)
            {
                ValidateSql(step.Command, false);
                foreach (var target in config?.Targets ?? [TargetKind.PostgreSql, TargetKind.MySql, TargetKind.MariaDb, TargetKind.Sqlite])
                    foreach (var parameter in SqlWorkloadText.Parameters(step.Command.For(target))) Require(available.Contains(parameter), "Не задан параметр @" + parameter + " в операции «" + operation.Name + "». Scalar должен быть сохранён предыдущим шагом.");
                Require(Enum.IsDefined(step.Result) && Enum.IsDefined(step.CaptureType) && step.MinRows is not < 0 && step.MaxRows is not < 0 && !(step.MinRows > step.MaxRows), "Проверьте ожидаемый результат SQL-шага.");
                Require(step.CaptureAs is null || Identifier(step.CaptureAs) && step.Result == SqlResultMode.Scalar && names.Add(step.CaptureAs), "Сохранение результата требует Scalar и уникальное имя параметра.");
                if (step.CaptureAs is { } capture) available.Add(capture);
            }
            Require(config?.Targets.Contains(TargetKind.Sqlite) != true || !operation.Transaction || operation.Isolation is SqlIsolation.Default or SqlIsolation.Serializable, "Для SQLite выберите Default или Serializable.");
        }
        foreach (var check in Checks)
        {
            ValidateSql(check.Command, false); Require(Enum.IsDefined(check.Comparison) && !string.IsNullOrWhiteSpace(check.Name) && check.Expected.Length <= 100, "Проверьте контрольный запрос.");
            Require(check.Expected == "baseRows" || check.Expected.StartsWith("rows:", StringComparison.Ordinal) && Datasets.Any(table => table.Name == check.Expected[5..]) || decimal.TryParse(check.Expected, NumberStyles.Number, CultureInfo.InvariantCulture, out _), "Ожидание проверки: число, baseRows или rows:таблица.");
        }
        ValidateSql(Schema, true); ValidateSql(AfterLoad, true);
        Require(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(this, Json.Options)) <= 512 * 1024, "Описание сценария больше 512 КБ; данные подключайте отдельными CSV/JSONL-файлами.");
        if (config is not null)
        {
            Require(config.EngineIds.SequenceEqual(["csharp"]), "Свои SQL-сценарии сейчас выполняются движком C#. Другие языки доступны для стандартных тестов.");
            Require(config.Targets.All(Supports), "Свой SQL-сценарий поддерживает PostgreSQL, MySQL, MariaDB и SQLite.");
        }

        void ValidateValues(List<ValueDefinition> values)
        {
            Require(values.Select(value => value.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == values.Count, "Имена полей/параметров должны различаться.");
            foreach (var value in values)
            {
                Require(Identifier(value.Name) && Enum.IsDefined(value.Type) && Enum.IsDefined(value.Generator) && (value.Generator is not (ValueGenerator.Integer or ValueGenerator.Decimal or ValueGenerator.Timestamp) || value.Min <= value.Max) &&
                    value.Min >= long.MinValue && value.Min <= long.MaxValue && value.Max >= long.MinValue && value.Max <= long.MaxValue &&
                    value.Length is >= 0 and <= 65536 && value.Scale is >= 0 and <= 12 && double.IsFinite(value.NullPercent) && value.NullPercent is >= 0 and <= 100 && (value.Nullable || value.NullPercent == 0), "Проверьте генератор поля " + value.Name);
                Require(value.Generator != ValueGenerator.Choice || value.Choices is { Count: > 0 and <= 1000 }, "Для выбора из списка нужны значения: " + value.Name);
                Require(!value.PrimaryKey || !value.Nullable && value.NullPercent == 0, "Первичный ключ не может допускать NULL: " + value.Name);
                Require(value.Generator is not (ValueGenerator.Integer or ValueGenerator.Sequence or ValueGenerator.Timestamp) || value.Min == decimal.Truncate(value.Min) && value.Max == decimal.Truncate(value.Max), "Для целого генератора нужны целые границы: " + value.Name);
                Require(value.Generator != ValueGenerator.Timestamp || value.Min >= -62135596800m && value.Max <= 253402300799m, "Диапазон Timestamp должен помещаться в UTC-годы 1–9999: " + value.Name);
                Require(value.Generator != ValueGenerator.Reference || Datasets.Any(table => table.Name == value.Reference), "Не найдена таблица для ссылки: " + value.Name);
                Require(value.Generator != ValueGenerator.Sample || value.SampleSize >= 1 && Datasets.Any(table => table.Name == value.Reference && table.Columns.Any(column => column.Name == value.SampleColumn)), "Для Sample нужны таблица, столбец и положительный размер выборки: " + value.Name);
            }
        }
        void ValidateSql(SqlText text, bool allowEmpty)
        {
            Require(text is not null && text.Sql is not null && text.Sql.Length <= 200000 && text.Overrides.All(pair => Supports(pair.Key) && pair.Value is { Length: <= 200000 }), "Проверьте SQL и варианты диалектов.");
            if (!allowEmpty && config is not null) Require(config.Targets.All(target => !string.IsNullOrWhiteSpace(text!.For(target))), "Не задан SQL для одной из выбранных БД.");
            else if (!allowEmpty) Require(!string.IsNullOrWhiteSpace(text!.Sql) || text.Overrides.Values.Any(value => !string.IsNullOrWhiteSpace(value)), "SQL-шаг не может быть пустым.");
        }
    }
    public static bool SafeFile(string? file) => file is { Length: > 0 and < 150 } && file.Split('/').All(IdentifierPart) && !file.Contains('\\');
    private static bool IdentifierPart(string part) => part is not ("" or "." or "..") && Regex.IsMatch(part, "^[A-Za-z0-9_.-]+$");
}

public static class WorkloadPhases
{
    public const string Mix = "custom/mix";
    public static bool Setup(string phase) => phase.StartsWith("custom/setup/", StringComparison.Ordinal);
    public static bool Duration(string phase) => Setup(phase) && !phase.EndsWith("/load", StringComparison.Ordinal);
    public static string Query(string id) => "custom/query/" + id;
    public static IEnumerable<(string Phase, string? Operation)> Units(SqlWorkload workload)
    {
        if (workload.Mode != WorkloadMode.Mix) foreach (var operation in workload.ActiveOperations) yield return (Query(operation.Id), operation.Id);
        if (workload.Mode != WorkloadMode.Separate) yield return (Mix, null);
    }
    public static IEnumerable<string> All(SqlWorkload workload) => Units(workload).SelectMany(unit =>
        new[] { "schema", "load", "indexes", "before", "after" }.Select(part => "custom/setup/" + (unit.Operation ?? "mix") + "/" + part).Append(unit.Phase));
    public static string Name(string phase, SqlWorkload? workload)
    {
        if (phase == Mix) return "Смесь своих запросов";
        var parts = phase.Split('/');
        var id = parts.ElementAtOrDefault(2);
        var name = workload?.Operations.FirstOrDefault(operation => operation.Id == id)?.Name ?? (id == "mix" ? "Смесь" : id ?? "Свой сценарий");
        if (!Setup(phase)) return name;
        return (parts.Last() switch { "schema" => "Схема", "load" => "Данные", "indexes" => "Индексы", "before" => "Проверка до", _ => "Проверка после" }) + " · " + name;
    }
}

public sealed record WorkloadOperationResult(string Id, string Name, long SuccessfulOperations, long SuccessfulRows, long Errors,
    HistogramData ServiceLatency, HistogramData EndToEndLatency);
