using System.Globalization;
using BenchForge.Contracts;

namespace BenchForge.Coordinator;

public enum AnalysisMetric { Rate, P99, ServerMemory, ClientMemory, ServerCpu, ClientCpu, GcPause, ErrorPercent, DropPercent, EndToEndP99 }
public enum MetricDirection { Higher, Lower, Context }
public sealed record ReportSlice(string Phase, long Rows, int Clients, int? StageNumber = null, string? Engine = null, TargetKind? Target = null, ResourceLimits? Resource = null);
public sealed record MetricDescription(string Title, string Unit, MetricDirection Direction, string Explanation);
public sealed record SeriesAnalysis(string Id, string Engine, TargetKind Target, string Name, double? Median, double? Minimum,
    double? Maximum, double[] Values, int ValidRepeats, int TotalRepeats, long Errors, long Dropped, string MissingReason);
public sealed record ReportNarrative(string Headline, string Explanation, string[] Limitations, bool Preliminary,
    ComparisonReview Review, string? ObservedDifference = null);
public sealed record TrendPoint(double X, double Median, double Minimum, double Maximum, int Repeats);
public sealed record TrendSeries(string Id, TargetKind Target, string Name, TrendPoint[] Points);

public static class ReportAnalysis
{
    public static readonly AnalysisMetric[] Metrics = Enum.GetValues<AnalysisMetric>();
    public static readonly string[] KnownPhases = new[] { "bulk_load", "index_build" }.Concat(Enum.GetNames<Scenario>()).ToArray();
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    public static string PhaseName(string phase, SqlWorkload? workload = null) => phase.StartsWith("custom/", StringComparison.Ordinal) ? WorkloadPhases.Name(phase, workload) : phase switch
    {
        "bulk_load" => "Загрузка данных", "index_build" => "Создание индекса",
        _ => Enum.TryParse<Scenario>(phase, out var value) ? Catalog.Name(value) : phase
    };
    public static string PhaseExplanation(string phase) => phase switch
    {
        "bulk_load" => "Как быстро создан и загружен исходный набор. Скорость включает генерацию, упаковку и запись данных.",
        "index_build" => "Сколько заняло создание вторичного индекса и, для SQL, обновление статистики. Здесь меньше секунд — быстрее подготовка.",
        nameof(Scenario.PointRead) => "Каждый запрос находит одну запись по её ID и получает все поля. Скорость показывает число успешных чтений за секунду.",
        nameof(Scenario.RangeRead) => "Один запрос получает до 100 записей по порядку ID. Одна операция здесь возвращает больше данных, чем чтение по ID.",
        nameof(Scenario.Aggregate) => "Запрос считает записи и сумму значения в одной группе. Результат относится к этому виду агрегации.",
        nameof(Scenario.AnalyticalScan) => "Аналитический запрос считает и суммирует значения в диапазоне ключей. Аналитический профиль ClickHouse и DuckDB.",
        nameof(Scenario.FullTextSearch) => "Поиск по текстовому индексу: запрос 16-символьного термина исходного текста, до 10 найденных документов. Термин проверяется в ответах. Это профиль Elasticsearch/OpenSearch.",
        nameof(Scenario.Update) => "Каждая операция атомарно увеличивает счётчик версии одной записи.",
        nameof(Scenario.Mixed) => "В среднем 80% операций читают запись, 20% обновляют её. Показана скорость всей смеси.",
        nameof(Scenario.Insert) => "Одна операция вставляет новую запись и ждёт подтверждения выбранной СУБД.",
        nameof(Scenario.BatchInsert) => "Каждая операция записывает пакет. Скорость показана в записях за секунду; транспорты и гарантии записи различаются между СУБД.",
        nameof(Scenario.Delete) => "Удаляются существующие исходные записи по ID. Если они закончились, этап может завершиться раньше заданного времени.",
        _ => phase.StartsWith("custom/", StringComparison.Ordinal) ? "Одна операция включает все SQL-шаги, получение ответа, проверки и фиксацию транзакции. Параметры создаются внутри измеряемого вызова. Подготовка подключений и контрольные запросы целостности выполняются вне времени запросов." : "Измерения выбранного этапа эксперимента."
    };
    public static bool SetupPhase(string phase) => phase is "bulk_load" or "index_build" || WorkloadPhases.Setup(phase);
    public static bool Valid(PhaseResult row) => row.Verified && !row.Cancelled && row.Errors == 0;
    public static double? Rate(PhaseResult row) => row.Unit == "seconds" ? row.ElapsedSeconds : row.Unit == "rows/s" ? row.RowsPerSecond : row.OperationsPerSecond;
    public static string RateUnit(string phase) => phase == "index_build" || WorkloadPhases.Duration(phase) ? "с" : phase is "bulk_load" or nameof(Scenario.BatchInsert) || WorkloadPhases.Setup(phase) && phase.EndsWith("/load", StringComparison.Ordinal) ? "зап./с" : "оп./с";
    public static MetricDescription Describe(AnalysisMetric metric, string phase) => metric switch
    {
        AnalysisMetric.Rate => new(phase == "index_build" || WorkloadPhases.Duration(phase) ? "Время подготовки" : "Скорость выполнения", RateUnit(phase),
            phase == "index_build" || WorkloadPhases.Duration(phase) ? MetricDirection.Lower : MetricDirection.Higher,
            phase == "index_build" || WorkloadPhases.Duration(phase) ? "Меньше — подготовка завершилась быстрее. Это время этапа, не скорость запросов."
                : phase is "bulk_load" or nameof(Scenario.BatchInsert) ? "Больше — записано больше строк за секунду. Размер пакета и способ записи смотрите в условиях отчёта."
                : "Больше — выполнено больше успешных операций за секунду. Сравнивайте один сценарий при одинаковой нагрузке."),
        AnalysisMetric.P99 => new("Время ответа для 99% запросов", "мс", MetricDirection.Lower,
            "Около 99% успешных запросов уложились в это время. Меньше — быстрее отклик у медленной части запросов. Это не максимальная задержка."),
        AnalysisMetric.EndToEndP99 => new("Задержка с ожиданием p99", "мс", MetricDirection.Lower,
            "В режиме заданной частоты считается от запланированного старта до результата. Пропуски расписания показаны отдельно."),
        AnalysisMetric.ServerMemory => new("Память базы данных", "МБ", MetricDirection.Context,
            "Максимум RAM контейнера среди выборок. Меньшая память сама по себе не означает более быструю базу: кеш тоже использует RAM."),
        AnalysisMetric.ClientMemory => new("Память генератора нагрузки", "МБ", MetricDirection.Context,
            "Память отдельного движка, который выдаёт запросы. Это ресурсы клиента, не сервера базы."),
        AnalysisMetric.ServerCpu => new("Загрузка CPU базы", "%", MetricDirection.Context,
            "Средняя выборочная загрузка контейнера. 100% соответствует одному логическому ядру; значение может быть больше 100%. Смотрите вместе со скоростью."),
        AnalysisMetric.ClientCpu => new("Загрузка CPU генератора", "%", MetricDirection.Context,
            "CPU процесса, который выдаёт запросы. Высокая загрузка — повод проверить предел генератора; сама по себе она не доказывает причину ограничения."),
        AnalysisMetric.GcPause => new("Паузы управления памятью движка", "мс", MetricDirection.Context,
            "Суммарная длительность пауз сборщика мусора в генераторе за этап. Это не паузы СУБД и не задержка одного запроса."),
        AnalysisMetric.ErrorPercent => new("Доля ошибочных запросов", "%", MetricDirection.Lower,
            "Неуспешные операции / все выполненные попытки. Ошибочные прогоны здесь показаны, а не исключены из расчёта."),
        AnalysisMetric.DropPercent => new("Доля пропущенной нагрузки", "%", MetricDirection.Lower,
            "Пропущенные слоты / запланированные операции. В режиме заданной частоты показывает, какая часть расписания не была выполнена."),
        _ => throw new ArgumentOutOfRangeException(nameof(metric))
    };
    public static string MetricName(AnalysisMetric metric) => Describe(metric, nameof(Scenario.PointRead)).Title;
    public static double? Value(PhaseResult row, AnalysisMetric metric) => metric switch
    {
        AnalysisMetric.Rate => Rate(row), AnalysisMetric.P99 => row.ServiceLatency?.P99Ms,
        AnalysisMetric.EndToEndP99 => row.EndToEndLatency?.P99Ms,
        AnalysisMetric.ServerMemory => row.Server?.MemorySampledMaxMb, AnalysisMetric.ClientMemory => row.Client?.RssSampledMaxMb,
        AnalysisMetric.ServerCpu => row.Server?.CpuMeanPercent, AnalysisMetric.ClientCpu => row.Client?.CpuPercent,
        AnalysisMetric.GcPause => row.Client?.GcPauseMs,
        AnalysisMetric.ErrorPercent => row.SuccessfulOperations + row.Errors > 0 && !SetupPhase(row.Phase)
            ? row.Errors * 100.0 / (row.SuccessfulOperations + row.Errors) : null,
        AnalysisMetric.DropPercent => row.PlannedOperations > 0 && !SetupPhase(row.Phase) ? row.Dropped * 100.0 / row.PlannedOperations : null,
        _ => null
    };
    public static string Number(double? value, string unit = "", bool compact = false)
    {
        if (value is null || !double.IsFinite(value.Value)) return "Нет данных";
        var x = value.Value;
        var digits = Math.Abs(x) < 1 && x != 0 ? "N3" : Math.Abs(x) < 100 ? "N2" : "N0";
        var text = compact && Math.Abs(x) >= 1_000_000 ? (x / 1_000_000).ToString("N1", Russian) + " млн"
            : compact && Math.Abs(x) >= 10_000 ? (x / 1000).ToString("N1", Russian) + " тыс."
            : x.ToString(digits, Russian);
        return text + (unit.Length > 0 ? " " + unit : "");
    }
    public static string Count(long value) => value.ToString("N0", Russian);
    internal static bool Supported(TargetKind target, string phase) => phase.StartsWith("custom/", StringComparison.Ordinal) && SqlWorkload.Supports(target) || phase == "bulk_load" || phase == "index_build"
        && target is not (TargetKind.Redis or TargetKind.Valkey or TargetKind.ClickHouse or TargetKind.Elasticsearch or TargetKind.OpenSearch or TargetKind.DuckDb)
        || Enum.TryParse<Scenario>(phase, out var scenario) && Catalog.Get(target).Scenarios.Contains(scenario);

    public static SeriesAnalysis[] Compare(BenchmarkReport report, IReadOnlyList<PhaseResult> results, ReportSlice slice, AnalysisMetric metric)
    {
        string[] engines = slice.Engine is { } selectedEngine ? [selectedEngine] : report.Config.EngineIds.Concat(results.Select(r => r.Engine)).Where(x => !string.IsNullOrEmpty(x)).Distinct().ToArray();
        if (engines.Length == 0) engines = ["csharp"];
        var targets = (slice.Target is { } selectedTarget ? new[] { selectedTarget } : report.Config.Targets.Concat(results.Select(r => r.Target)).Distinct().Order().ToArray());
        var output = new List<SeriesAnalysis>();
        foreach (var engine in engines)
        foreach (var target in targets)
        {
            var all = results.Where(r => r.Engine == engine && r.Target == target && Matches(r, slice)).ToArray();
            var acceptsErrors = metric is AnalysisMetric.ErrorPercent or AnalysisMetric.DropPercent;
            var considered = all.Where(r => !r.Cancelled && (acceptsErrors || Valid(r))).ToArray();
            var measured = considered.Where(row => Value(row, metric) is { } value && double.IsFinite(value) && value >= 0).ToArray();
            var samples = measured.Select(row => Value(row, metric)!.Value).ToArray();
            string missing = "";
            if (samples.Length == 0)
                missing = !Supported(target, slice.Phase) ? "Не поддерживается этим профилем"
                    : all.Length == 0 ? report.Status == "running" ? "Ещё не измерено" : "Нет результата для этих условий"
                    : all.All(r => r.Cancelled) ? "Прогон остановлен"
                    : considered.Length == 0 ? "Нет корректного прогона без ошибок"
                    : (metric is AnalysisMetric.ServerMemory or AnalysisMetric.ServerCpu) && Catalog.Embedded(target) ? "Встраиваемая база работает внутри генератора"
                    : metric is AnalysisMetric.ServerMemory or AnalysisMetric.ServerCpu ? "Нет выборок ресурсов за этот этап"
                    : SetupPhase(slice.Phase) ? "Не измеряется на этапе подготовки" : "Показатель не был сохранён";
            var name = slice.Target is not null && engines.Length > 1 ? Engines.Name(engine) : Catalog.Get(target).Name + (engines.Length > 1 ? " · " + Engines.Name(engine) : "");
            output.Add(new(engine + ":" + target, engine, target, name, ReportStore.Median(samples.Select(x => (double?)x)),
                samples.Length == 0 ? null : samples.Min(), samples.Length == 0 ? null : samples.Max(), samples,
                measured.Select(x => x.Repeat).Distinct().Count(), all.Select(x => x.Repeat).Distinct().Count(),
                all.Sum(x => x.Errors), all.Sum(x => x.Dropped), missing));
        }
        return output.ToArray();
    }

    public static ReportNarrative Explain(BenchmarkReport report, IReadOnlyList<PhaseResult> results, ReportSlice slice, AnalysisMetric metric)
    {
        var series = Compare(report, results, slice, metric);
        var comparingEngines = slice.Target is not null && series.Select(item => item.Engine).Distinct().Count() > 1;
        var observed = series.Where(s => s.Median.HasValue).ToArray();
        var matched = results.Where(r => Matches(r, slice)).ToArray();
        var review = ComparisonQuality.Assess(report, results, slice, metric, series);
        var notes = review.Findings.Select(finding => finding.Message + " " + finding.NextStep).ToList();
        var description = Describe(metric, slice.Phase);
        if (observed.Length == 0) return new("Для этого показателя пока нет измерений", "Выберите другой сценарий или метрику. Отсутствующие данные не считаются нулевыми.", notes.ToArray(), true, review);
        if (observed.Length == 1)
            return new($"{observed[0].Name}: {Number(observed[0].Median, description.Unit)}", "Измерена одна связка при выбранных условиях. Для сравнения нужны результаты другого варианта или уровня нагрузки.", notes.ToArray(), review.Preliminary, review);
        if (description.Direction == MetricDirection.Context)
            return new("Ресурсы нужно читать вместе со скоростью", "Разница показана на графике. Меньшее потребление CPU или RAM само по себе не выбирает лучшую базу: проверьте скорость, задержки и ошибки при тех же условиях.", notes.ToArray(), review.Preliminary, review);
        var ordered = description.Direction == MetricDirection.Higher ? observed.OrderByDescending(s => s.Median).ToArray() : observed.OrderBy(s => s.Median).ToArray();
        var best = ordered[0]; var other = ordered[1];
        var delta = other.Median > 0 ? description.Direction == MetricDirection.Higher
            ? (best.Median!.Value / other.Median.Value - 1) * 100 : (other.Median.Value - best.Median!.Value) / other.Median.Value * 100 : (double?)null;
        var headline = (metric is AnalysisMetric.ErrorPercent or AnalysisMetric.DropPercent) && observed.All(s => s.Median == 0)
            ? metric == AnalysisMetric.ErrorPercent
                ? matched.All(r => r.Errors == 0) ? "В показанных прогонах ошибок нет" : "Медиана — 0%, но в отдельных повторах есть ошибки"
                : matched.All(r => r.Dropped == 0) ? "В показанных прогонах пропусков нет" : "Медиана — 0%, но в отдельных повторах есть пропуски"
            : delta is null ? $"На графике — наблюдавшиеся значения {observed.Length} вариантов"
            : delta < .05 ? $"Разница между {best.Name} и {other.Name} меньше 0,1%"
            : description.Direction == MetricDirection.Higher ? $"{best.Name}: на {delta.Value.ToString("N1", Russian)}% больше за секунду, чем {other.Name}"
            : metric is AnalysisMetric.ErrorPercent or AnalysisMetric.DropPercent ? $"{best.Name}: {Number(best.Median, "%")} против {Number(other.Median, "%")} у {other.Name}"
            : $"{best.Name}: {(slice.Phase == "index_build" || WorkloadPhases.Duration(slice.Phase) ? "время подготовки" : "задержка p99")} на {delta.Value.ToString("N1", Russian)}% меньше, чем у {other.Name}";
        if (observed.Length > 2) notes.Add("Процент в пояснении сравнивает два лучших наблюдавшихся значения выбранного показателя. Остальные варианты видны на графике.");
        if (best.ValidRepeats > 1 && other.ValidRepeats > 1 && !(best.Minimum <= other.Maximum && other.Minimum <= best.Maximum))
            notes.Add("В этих повторах диапазоны значений не пересекались. Это описание наблюдений, не статистическая гарантия.");
        var explanation = $"{PhaseName(slice.Phase, report.Config.CustomWorkload)} · {Count(slice.Rows)} базовых записей · {slice.Clients} клиентов. "
            + (metric is AnalysisMetric.ErrorPercent or AnalysisMetric.DropPercent ? "Сравниваются медианы долей по сохранённым прогонам. " : "Сравниваются медианы корректных повторов в этом отчёте. ")
            + (comparingEngines ? "Это наблюдение данного теста, а не общий рейтинг языков." : "Это наблюдение данного теста, а не общий рейтинг СУБД.");
        var observation = headline;
        if (metric is not (AnalysisMetric.ErrorPercent or AnalysisMetric.DropPercent))
            headline = review.State switch
            {
                ComparisonState.ConditionsDiffer => "Сначала проверьте условия сравнения",
                ComparisonState.Incomplete => "Сравнение пока неполное",
                ComparisonState.Preliminary => comparingEngines ? "Пока рано выбирать более быстрый движок" : "Пока рано выбирать более быструю базу",
                ComparisonState.Variable => "Процент нужно читать вместе с разбросом",
                _ => headline
            };
        return new(headline, explanation, notes.ToArray(), review.Preliminary, review, headline == observation ? null : observation);
    }

    public static TrendSeries[] Trends(BenchmarkReport report, IReadOnlyList<PhaseResult> results, ReportSlice slice, AnalysisMetric metric, bool byRows)
    {
        var eligible = results.Where(r => Valid(r) && r.Phase == slice.Phase && r.Stage?.Number == slice.StageNumber &&
            IdentityMatches(r, slice) && (byRows ? r.Clients == slice.Clients : r.DatasetRows == slice.Rows)).ToArray();
        return GroupTrend(eligible, metric, row => byRows ? row.DatasetRows : row.Clients);
    }

    public static TrendSeries[] ResourceTrends(IReadOnlyList<PhaseResult> results, ReportSlice slice, AnalysisMetric metric, bool byMemory)
    {
        var eligible = results.Where(row => row.Resource is not null && Valid(row) && row.Phase == slice.Phase && row.DatasetRows == slice.Rows &&
            row.Clients == slice.Clients && row.Stage?.Number == slice.StageNumber && IdentityMatches(row, slice with { Resource = row.Resource })).ToArray();
        return eligible.GroupBy(row => byMemory ? row.Resource!.CpuLimit : row.Resource!.MemoryMb).SelectMany(group =>
            GroupTrend(group.ToArray(), metric, row => byMemory ? row.Resource!.MemoryMb : row.Resource!.CpuLimit)
                .Select(series => series with { Id = series.Id + ":resource:" + group.Key,
                    Name = series.Name + (byMemory ? $" · {group.Key:g} CPU" : $" · {group.Key:N0} МБ") })).ToArray();
    }

    public static bool Matches(PhaseResult result, ReportSlice slice) => result.Phase == slice.Phase && result.DatasetRows == slice.Rows &&
        result.Clients == slice.Clients && result.Stage?.Number == slice.StageNumber && IdentityMatches(result, slice);

    public static bool IdentityMatches(PhaseResult result, ReportSlice slice) => (slice.Engine is null || result.Engine == slice.Engine) && (slice.Target is null || result.Target == slice.Target) && result.Resource == slice.Resource;

    public static TrendSeries[] StageTrends(IReadOnlyList<PhaseResult> results, ReportSlice slice, AnalysisMetric metric)
    {
        // Include measured overload (including zero throughput). Comparison still requires verified results.
        var measured = results.Where(row => row.Stage is not null && row.Phase == slice.Phase && row.DatasetRows == slice.Rows && !row.Cancelled && IdentityMatches(row, slice)).ToArray();
        return GroupTrend(measured, metric, row => row.Stage!.Number);
    }

    private static TrendSeries[] GroupTrend(PhaseResult[] eligible, AnalysisMetric metric, Func<PhaseResult, double> position)
    {
        var severalEngines = eligible.Select(r => r.Engine).Distinct().Count() > 1;
        return eligible.GroupBy(r => (r.Engine, r.Target)).Select(group =>
        {
            var points = group.GroupBy(position).OrderBy(g => g.Key).Select(g =>
            {
                var values = g.Select(r => Value(r, metric)).Where(x => x.HasValue && double.IsFinite(x.Value) && x.Value >= 0).Select(x => x!.Value).ToArray();
                return values.Length == 0 ? null : new TrendPoint(g.Key, ReportStore.Median(values.Select(x => (double?)x))!.Value, values.Min(), values.Max(), g.Select(r => r.Repeat).Distinct().Count());
            }).OfType<TrendPoint>().ToArray();
            return new TrendSeries(group.Key.Engine + ":" + group.Key.Target, group.Key.Target, Catalog.Get(group.Key.Target).Name + (severalEngines ? " · " + Engines.Name(group.Key.Engine) : ""), points);
        }).Where(s => s.Points.Length > 0).ToArray();
    }
}
