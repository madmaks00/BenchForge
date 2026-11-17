using System.Globalization;
using BenchForge.Contracts;

namespace BenchForge.Coordinator;

public enum ComparisonState { NoData, SingleSeries, ConditionsDiffer, Incomplete, Preliminary, Variable, Observed }
public sealed record ComparisonFinding(string Code, string Message, string NextStep, bool BlocksConclusion = true);
public sealed record ComparisonReview(ComparisonState State, string Label, string Summary, ComparisonFinding[] Findings, bool Preliminary);

public static class ComparisonQuality
{
    // Practical review thresholds, not tests of statistical significance.
    public const int MinimumRepeats = 3;
    public const double MinimumQuerySeconds = 60;
    public const double SpreadWarningPercent = 10;

    public static ComparisonReview Assess(BenchmarkReport report, IReadOnlyList<PhaseResult> results, ReportSlice slice, AnalysisMetric metric) =>
        Assess(report, results, slice, metric, ReportAnalysis.Compare(report, results, slice, metric));

    internal static ComparisonReview Assess(BenchmarkReport report, IReadOnlyList<PhaseResult> results, ReportSlice slice,
        AnalysisMetric metric, SeriesAnalysis[] series)
    {
        var findings = new List<ComparisonFinding>();
        void Add(string code, string message, string nextStep, bool blocksConclusion = true) => findings.Add(new(code, message, nextStep, blocksConclusion));
        var observed = series.Where(item => item.Median.HasValue).ToArray();
        var matched = results.Where(row => ReportAnalysis.Matches(row, slice)).ToArray();
        var acceptsErrors = metric is AnalysisMetric.ErrorPercent or AnalysisMetric.DropPercent;
        var measured = matched.Where(row => !row.Cancelled && (acceptsErrors || ReportAnalysis.Valid(row)))
            .Where(row => ReportAnalysis.Value(row, metric) is { } value && double.IsFinite(value) && value >= 0).ToArray();
        if (observed.Select(item => item.Engine).Distinct().Count() > 1)
            Add("engine_comparison", "Сравниваются реализации языка вместе с драйвером и моделью параллельной работы.",
                "Результат не измеряет язык отдельно от его библиотек. Смотрите версии, транспорт, число соединений и настройки в метаданных.", false);

        if (observed.Length > 0 && observed.Min(item => item.ValidRepeats) < MinimumRepeats)
        {
            var count = observed.Min(item => item.ValidRepeats);
            Add("few_repeats", count == 1 ? "Только один повтор: разброс результатов пока неизвестен."
                : "Меньше трёх повторов: наблюдений мало для оценки устойчивости разницы.",
                "Сделайте хотя бы три повтора каждого варианта; этого порога недостаточно для доказательства статистической значимости.");
        }
        if (!ReportAnalysis.SetupPhase(slice.Phase) && measured.Any(row => row.ElapsedSeconds < MinimumQuerySeconds))
            Add("short_run", "Короткий прогон: есть измерения меньше минуты на сценарий.",
                "Для серьёзного сравнения увеличьте измерение до нескольких минут и повторите тест.");
        if (report.Status != "completed")
            Add("unfinished", report.Status == "running" ? "Эксперимент ещё идёт: получена только часть результатов."
                : "Эксперимент завершён не полностью или с ошибками.", "Проверьте ход работы и завершите корректные повторы.");
        if (matched.Any(row => row.Errors > 0 || !row.Verified || row.Cancelled))
            Add("invalid_trials", "Есть ошибки, отменённые или непроверенные результаты. Скорость рассчитывается по проверенным прогонам без ошибок.",
                "Откройте измерения, устраните причину ошибок и повторите проблемные прогоны.");
        if (matched.Any(row => row.Dropped > 0))
            Add("dropped_load", "Часть запланированной нагрузки пропущена.",
                "Смотрите фактическую скорость вместе с долей пропусков и задержкой с ожиданием.");
        if (series.Any(item => !item.Median.HasValue && ReportAnalysis.Supported(item.Target, slice.Phase)
            && !((metric is AnalysisMetric.ServerMemory or AnalysisMetric.ServerCpu) && Catalog.Embedded(item.Target))))
            Add("missing_series", "Для части выбранных вариантов нет измерений этого показателя.",
                "Не считайте отсутствующие значения нулём; проверьте измерения и сбор метрик.");
        if (observed.Any(item => item.ValidRepeats < report.Config.Repeats))
            Add("missing_repeats", "По этому показателю сохранено меньше корректных повторов, чем запланировано.",
                "Завершите недостающие повторы и проверьте, почему часть измерений отсутствует.");

        var profiles = observed.Select(item => Profile(item.Target)).Distinct().ToArray();
        if (profiles.Length > 1)
            Add("storage_profiles", "Сравниваются разные профили: " + string.Join(", ", profiles) + ". Способы доступа и гарантии записи могут различаться.",
                "Сравнивайте одинаковую задачу с учётом профиля хранения; для записи проверьте гарантии подтверждения.");
        if (metric == AnalysisMetric.Rate && measured.Select(row => row.Unit).Distinct().Count() > 1)
            Add("different_units", "У измерений одной операции различаются единицы скорости.",
                "Не объединяйте операции в секунду и записи в секунду в одном сравнении.");
        if (measured.Select(row => (row.Stage?.TargetSeconds, row.Stage?.OperationsPerSecond)).Distinct().Count() > 1)
            Add("different_load", "В выбранном этапе сохранены разные длительности или целевые частоты нагрузки.",
                "Повторите сравнение с одинаковыми настройками этапа для всех вариантов.");

        foreach (var item in observed)
        {
            var metadata = MetadataFor(report, results, item).ToArray();
            if (metadata.Length == 0)
                Add("metadata_unavailable", item.Name + ": версии и лимиты не удалось связать с измерениями этого отчёта.",
                    "Проверка использует сохранённые значения. Отсутствие предупреждения об изменениях не подтверждает одинаковые настройки.", false);
            if (metadata.Any(entry => entry.Settings.GetValueOrDefault("build", "").StartsWith("Debug", StringComparison.OrdinalIgnoreCase)))
                Add("debug_build", item.Name + ": движок нагрузки собран в Debug.",
                    "Для оценки производительности соберите приложение и движок в Release и повторите тест.");
            var changed = new List<string>();
            void Check(string name, IEnumerable<string?> values)
            { if (values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.Trim()).Distinct(StringComparer.Ordinal).Count() > 1) changed.Add(name); }
            Check("версия БД", metadata.Select(entry => entry.ServerVersion));
            Check("драйвер", metadata.Select(entry => entry.Driver));
            Check("среда выполнения", metadata.Select(entry => entry.Runtime));
            foreach (var (key, title) in new[] { ("image_id", "образ БД"), ("cpu_limit", "лимит CPU базы"), ("memory_limit_mb", "лимит RAM базы"),
                ("vm_cpus", "CPU виртуальной машины"), ("vm_memory_mb", "RAM виртуальной машины") })
                Check(title, metadata.Select(entry => Normalize(entry.Settings.GetValueOrDefault(key), key != "image_id")));
            if (changed.Count > 0)
                Add("environment_changed", "В отчёте для " + item.Name + " различаются: " + string.Join(", ", changed) + ".",
                    "Закрепите версии и ресурсы, затем повторите измерения с одинаковыми настройками.");

            var spread = SpreadPercent(item);
            if (spread > SpreadWarningPercent || item.Values.Length > 1 && item.Median == 0 && item.Maximum > 0)
                Add("repeat_spread", item.Name + ": большой разброс повторов" + (spread is { } value && double.IsFinite(value)
                    ? $" — {value.ToString("N1", CultureInfo.GetCultureInfo("ru-RU"))}% от медианы." : "; есть нулевые и ненулевые значения."),
                    "Проверьте фоновые процессы, увеличьте время и число повторов. Порог разброса 10% — подсказка приложения, не статистический тест.");
        }
        var direction = ReportAnalysis.Describe(metric, slice.Phase).Direction;
        if (observed.Length > 1 && metric is AnalysisMetric.Rate or AnalysisMetric.P99 or AnalysisMetric.EndToEndP99)
        {
            var leading = (direction == MetricDirection.Higher ? observed.OrderByDescending(item => item.Median) : observed.OrderBy(item => item.Median)).Take(2).ToArray();
            if (leading.All(item => item.ValidRepeats > 1) && leading[0].Minimum <= leading[1].Maximum && leading[1].Minimum <= leading[0].Maximum
                && (leading[0].Minimum != leading[0].Maximum || leading[1].Minimum != leading[1].Maximum))
                Add("overlapping_ranges", "Диапазоны повторов у двух ведущих значений пересекаются.",
                    "Процент показывает разницу медиан. Пересечение диапазонов само по себе не доказывает ни наличие, ни отсутствие статистически значимой разницы.");
        }

        bool Has(params string[] codes) => findings.Any(finding => codes.Contains(finding.Code));
        string Reason(params string[] codes) => codes.Select(code => findings.FirstOrDefault(finding => finding.Code == code))
            .First(finding => finding is not null)!.Message;
        var state = observed.Length == 0 ? ComparisonState.NoData
            : Has("storage_profiles", "different_units", "different_load", "environment_changed", "debug_build") ? ComparisonState.ConditionsDiffer
            : Has("unfinished", "invalid_trials", "dropped_load", "missing_series", "missing_repeats") ? ComparisonState.Incomplete
            : Has("few_repeats", "short_run") ? ComparisonState.Preliminary
            : Has("repeat_spread", "overlapping_ranges") ? ComparisonState.Variable
            : observed.Length == 1 ? ComparisonState.SingleSeries : ComparisonState.Observed;
        var (label, summary) = state switch
        {
            ComparisonState.NoData => ("Нет измерений", "Выберите показатель с сохранёнными корректными измерениями."),
            ComparisonState.ConditionsDiffer => ("Проверьте условия", Reason("debug_build", "environment_changed", "different_units", "different_load", "storage_profiles")),
            ComparisonState.Incomplete => ("Неполные данные", Reason("invalid_trials", "missing_series", "missing_repeats", "dropped_load", "unfinished")),
            ComparisonState.Preliminary => ("Пробные измерения", Has("few_repeats") && Has("short_run")
                ? "Увеличьте измерение до нескольких минут и выполните хотя бы три повтора каждого варианта."
                : Has("few_repeats") ? "Сделайте хотя бы три повтора каждого варианта."
                : "Есть прогоны короче минуты. Увеличьте измерение до нескольких минут."),
            ComparisonState.Variable => ("Есть разброс", Reason("overlapping_ranges", "repeat_spread")),
            ComparisonState.SingleSeries => ("Одна серия данных", "Для сравнения добавьте другой вариант или выберите график масштабирования."),
            _ => ("Наблюдение по повторам", "Базовые проверки не выявили ограничений. Это не доказательство статистической значимости.")
        };
        return new(state, label, summary, findings.ToArray(), findings.Any(finding => finding.BlocksConclusion) || observed.Length == 0);
    }

    public static double? SpreadPercent(SeriesAnalysis item) => item.Values.Length > 1 && item.Median > 0
        ? (item.Maximum - item.Minimum) / item.Median * 100 : null;

    public static string EnvironmentDescription(BenchmarkReport report) => report.Config.RemoteServer is { } server
        ? "Linux-сервер · " + server.DisplayName
        : report.Environment.GetValueOrDefault("execution_location") == "remote" ? "Linux-сервер · " + report.Environment.GetValueOrDefault("remote_label", "удалённое исполнение")
        : report.Config.VirtualMachine is { } vm
        ? $"Чистая VM · {vm.Cpus} CPU · {ReportAnalysis.Count(vm.MemoryMb)} МБ RAM"
        : report.Config.Targets.Any(target => !Catalog.Embedded(target)) ? "Этот компьютер · серверные БД в Docker"
        : "Этот компьютер · встраиваемые базы в процессе движка";

    private static string Profile(TargetKind target) => target switch
    { TargetKind.Sqlite => "встроенная БД", TargetKind.DuckDb => "встроенная аналитическая БД",
        TargetKind.Elasticsearch or TargetKind.OpenSearch => "поисковая БД", TargetKind.CockroachDb => "SQL без репликации (1 узел)", TargetKind.Oracle => "серверная БД в редакции Free", TargetKind.Redis or TargetKind.Valkey => "кеш в памяти", TargetKind.ClickHouse => "аналитическая БД", _ => "серверная БД" };

    private static IEnumerable<EngineMetadata> MetadataFor(BenchmarkReport report, IReadOnlyList<PhaseResult> results, SeriesAnalysis item)
    {
        var targets = report.Config.Targets.Concat(results.Select(row => row.Target)).Distinct().ToArray();
        string Image(TargetKind target) => Catalog.Embedded(target) ? "embedded" : report.Config.Image(target);
        var image = Image(item.Target);
        if (targets.Count(target => Image(target) == image) != 1) return [];
        return report.Engines.Values.Where(entry => entry.Engine == item.Engine && entry.Settings is not null &&
            (entry.Settings.GetValueOrDefault("image_tag") == image || targets.Length == 1 && !entry.Settings.ContainsKey("image_tag")));
    }

    private static string? Normalize(string? value, bool numeric) => numeric && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
        ? number.ToString("R", CultureInfo.InvariantCulture) : value;
}
