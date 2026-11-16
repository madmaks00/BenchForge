using System.Text.Json;
using System.Text.Json.Serialization;

namespace BenchForge.Contracts;

public enum TargetKind { PostgreSql, MySql, MariaDb, MongoDb, Redis, Valkey, ClickHouse, Sqlite }
public enum Scenario { PointRead, RangeRead, Aggregate, AnalyticalScan, Update, Mixed, Insert, BatchInsert, Delete }
public enum LoadModel { ClosedLoop, ConstantRate }

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    public static readonly JsonSerializerOptions Pretty = new(Options) { WriteIndented = true };
}

public sealed record TargetInfo(TargetKind Kind, string Name, string Family, string Image, int Port, Scenario[] Scenarios);

public static class Catalog
{
    private static readonly Scenario[] Oltp = [Scenario.PointRead, Scenario.RangeRead, Scenario.Aggregate, Scenario.Update,
        Scenario.Mixed, Scenario.Insert, Scenario.BatchInsert, Scenario.Delete];
    private static readonly Scenario[] Cache = [Scenario.PointRead, Scenario.Update, Scenario.Mixed, Scenario.Insert,
        Scenario.BatchInsert, Scenario.Delete];
    public static readonly TargetInfo[] Targets =
    [
        new(TargetKind.PostgreSql, "PostgreSQL", "Реляционная", "postgres:18", 5432, Oltp),
        new(TargetKind.MySql, "MySQL", "Реляционная", "mysql:8.4", 3306, Oltp),
        new(TargetKind.MariaDb, "MariaDB", "Реляционная", "mariadb:11.8", 3306, Oltp),
        new(TargetKind.MongoDb, "MongoDB", "Документная", "mongo:8.0", 27017, Oltp),
        new(TargetKind.Redis, "Redis", "Ключи / память", "redis:8", 6379, Cache),
        new(TargetKind.Valkey, "Valkey", "Ключи / память", "valkey/valkey:9", 6379, Cache),
        new(TargetKind.ClickHouse, "ClickHouse", "Аналитика", "clickhouse/clickhouse-server:26.8", 8123,
            [Scenario.RangeRead, Scenario.Aggregate, Scenario.AnalyticalScan, Scenario.BatchInsert]),
        new(TargetKind.Sqlite, "SQLite", "Встраиваемая", "", 0, Oltp)
    ];
    public static TargetInfo Get(TargetKind kind) => Targets.Single(x => x.Kind == kind);
    public static string Name(Scenario scenario) => scenario switch
    {
        Scenario.PointRead => "Чтение по ID", Scenario.RangeRead => "Диапазон до 100 записей",
        Scenario.Aggregate => "Агрегация по группе", Scenario.AnalyticalScan => "Аналитический диапазон",
        Scenario.Update => "Атомарное обновление", Scenario.Mixed => "80% чтения / 20% обновлений",
        Scenario.Insert => "Вставка одной записи", Scenario.BatchInsert => "Пакетная вставка", Scenario.Delete => "Удаление по ID", _ => scenario.ToString()
    };
}

public sealed record BenchmarkConfig
{
    public List<TargetKind> Targets { get; init; } = [TargetKind.Sqlite];
    public List<long> DatasetRows { get; init; } = [10_000];
    public List<int> Clients { get; init; } = [1, 8];
    public LoadPlan? Plan { get; init; }
    public List<Scenario> Scenarios { get; init; } = [];
    public int Repeats { get; init; } = 2;
    public double DurationSeconds { get; init; } = 5;
    public double WarmupSeconds { get; init; } = 2;
    public int PayloadBytes { get; init; } = 256;
    public int BatchSize { get; init; } = 1000;
    public ulong Seed { get; init; } = 42;
    public bool Hotspot { get; init; }
    public LoadModel LoadModel { get; init; } = LoadModel.ClosedLoop;
    public int TargetOpsPerSecond { get; init; } = 1000;
    public double MaxStartLagMs { get; init; } = 20;
    public double CpuLimit { get; init; } = 2;
    public int MemoryMb { get; init; } = 2048;
    public Dictionary<TargetKind, string> ImageOverrides { get; init; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VirtualMachineOptions? VirtualMachine { get; init; }

    public void Validate()
    {
        VirtualMachine?.Validate();
        if (VirtualMachine is { } vm && Targets.Any(target => target != TargetKind.Sqlite) && CpuLimit > vm.Cpus)
            throw new ArgumentException("Лимит CPU базы должен помещаться в число CPU виртуальной машины.");
        if (Targets.Count == 0 || Targets.Distinct().Count() != Targets.Count || Targets.Any(t => !Enum.IsDefined(t)))
            throw new ArgumentException("Выберите СУБД без повторов.");
        if (DatasetRows.Count == 0 || DatasetRows.Count > 16 || DatasetRows.Distinct().Count() != DatasetRows.Count || DatasetRows.Any(n => n is < 100 or > 100_000_000))
            throw new ArgumentException("Объёмы данных: уникальные числа от 100 до 100 000 000 (до 16 значений).");
        if (Clients.Count == 0 || Clients.Count > 16 || Clients.Distinct().Count() != Clients.Count || Clients.Any(n => n < 1))
            throw new ArgumentException("Число клиентов: положительные целые числа без повторов (до 16 значений).");
        Plan?.Validate();
        if (Scenarios.Any(s => !Enum.IsDefined(s)) || Scenarios.Distinct().Count() != Scenarios.Count)
            throw new ArgumentException("Неизвестный или повторяющийся сценарий.");
        if (Repeats is < 1 or > 20 || PayloadBytes is < 16 or > 65_536 || BatchSize is < 1 or > 10_000)
            throw new ArgumentException("Повторы: 1–20; payload: 16–65 536 байт; пакет: 1–10 000 записей.");
        if (!double.IsFinite(DurationSeconds) || DurationSeconds is < .05 or > 3600 || !double.IsFinite(WarmupSeconds) || WarmupSeconds is < 0 or > 300)
            throw new ArgumentException("Длительность: 0.05–3600 секунд; прогрев: 0–300 секунд.");
        if (!double.IsFinite(CpuLimit) || CpuLimit is < .5 or > 64 || MemoryMb is < 512 or > 131072)
            throw new ArgumentException("Лимиты: 0.5–64 CPU, 512–131 072 МБ RAM.");
        if (!Enum.IsDefined(LoadModel) || TargetOpsPerSecond is < 1 or > 10_000_000 || !double.IsFinite(MaxStartLagMs) || MaxStartLagMs is < 0 or > 10000)
            throw new ArgumentException("Проверьте модель нагрузки, целевую скорость и допустимое опоздание старта.");
        foreach (var image in ImageOverrides.Values)
            if (string.IsNullOrWhiteSpace(image) || image.StartsWith('-') || image.Any(char.IsWhiteSpace))
                throw new ArgumentException("Некорректное имя образа Docker.");
    }
    public int EffectiveBatchSize => Math.Min(BatchSize, Math.Max(1, 8 * 1024 * 1024 / (PayloadBytes + 128)));
    public IReadOnlyList<int> CaseClients() => Plan is { } plan ? [plan.PeakClients] : Clients;
    public IReadOnlyList<Scenario> Operations(TargetKind target)
    {
        var supported = Catalog.Get(target).Scenarios;
        if (Scenarios.Count > 0) return Scenarios.Intersect(supported).Order().ToArray();
        if (Plan is null) return supported.Order().ToArray();
        return [supported.Contains(Scenario.PointRead) ? Scenario.PointRead : Scenario.RangeRead];
    }
    public string Image(TargetKind target) => ImageOverrides.GetValueOrDefault(target, Catalog.Get(target).Image);
}

// Sent only through the private control pipe, never written to a benchmark report.
public sealed record Endpoint(string Host, int Port, string Username, string Password, string Database, string? FilePath = null);
public sealed record RunCase(string RunId, TargetKind Target, long Rows, int Clients, int Repeat, BenchmarkConfig Config, Endpoint Endpoint);
public sealed record WorkerCommand(int ProtocolVersion, string Type, RunCase? Run = null);
public sealed record WireEvent(int ProtocolVersion, string Type, string RunId, JsonElement Data);

public sealed record EngineDescriptor(string Id, string Language, string Status, string? Runtime = null);
public static class Engines
{
    public static readonly EngineDescriptor[] All =
    [new("csharp", "C# / .NET", "implemented"), new("java", "Java", "planned"), new("python", "Python", "planned"),
     new("rust", "Rust", "planned"), new("node", "Node.js / JavaScript", "planned"), new("go", "Go", "planned")];
}

public sealed record PhaseStart(string Phase, double TargetSeconds, LoadStageResult? Stage = null, int? Clients = null);
public sealed record EngineMetadata(string Engine, string Runtime, string Driver, string ServerVersion, Dictionary<string, string> Settings);
public sealed record ClientMetrics(double CpuPercent, double RssEndMb, double RssSampledMaxMb, long AllocatedBytes,
    int Gen0Collections, int Gen1Collections, int Gen2Collections, double GcPauseMs);
public sealed record ServerMetrics(double? MemorySampledMaxMb, double? CpuMeanPercent, double? ReadMb, double? WrittenMb,
    int Samples, string Source = "docker stats; approximate receive-window boundaries");

public sealed record PhaseResult
{
    public string Engine { get; init; } = "csharp";
    public TargetKind Target { get; init; }
    public long DatasetRows { get; init; }
    public int Clients { get; init; }
    public int Repeat { get; init; }
    public string Phase { get; init; } = "";
    public LoadStageResult? Stage { get; init; }
    public string Unit { get; init; } = "ops/s";
    public double ElapsedSeconds { get; init; }
    public long SuccessfulOperations { get; init; }
    public long SuccessfulRows { get; init; }
    public long Errors { get; init; }
    public long Dropped { get; init; }
    public long PlannedOperations { get; init; }
    public double? OperationsPerSecond { get; init; }
    public double? RowsPerSecond { get; init; }
    public double? GenerationSeconds { get; init; }
    public bool Verified { get; init; }
    public bool Cancelled { get; init; }
    public Dictionary<string, long> ErrorExamples { get; init; } = [];
    public HistogramData? ServiceLatency { get; init; }
    public HistogramData? EndToEndLatency { get; init; }
    public HistogramData? ErrorLatency { get; init; }
    public ClientMetrics? Client { get; init; }
    public ServerMetrics? Server { get; init; }
}

public sealed record SummaryRow(string Engine, TargetKind Target, long DatasetRows, int Clients, string Phase, string Unit,
    int ValidRepeats, double? MedianRate, double? MinRate, double? MaxRate, double? MedianP95Ms, double? MedianP99Ms,
    double? MedianClientRssMb, double? MedianServerRssMb, LoadStageResult? Stage = null);

public sealed class BenchmarkReport
{
    public int SchemaVersion { get; init; } = 1;
    public string RunId { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public string Status { get; set; } = "running";
    public BenchmarkConfig Config { get; init; } = new();
    public Dictionary<string, string> Environment { get; init; } = [];
    public Dictionary<string, EngineMetadata> Engines { get; init; } = [];
    public List<PhaseResult> Results { get; init; } = [];
    public List<SummaryRow> Summary { get; set; } = [];
    public List<string> Warnings { get; init; } = [];
    public List<string> Failures { get; init; } = [];
}
