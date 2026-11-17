using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using BenchForge.Contracts;
using BenchForge.Coordinator;

namespace BenchForge.Desktop;

public sealed record PhaseChoice(string Id, string Name) { public override string ToString() => Name; }
public sealed record MetricChoice(AnalysisMetric Id, string Name) { public override string ToString() => Name; }
internal sealed record EngineChoice(string Id) { public override string ToString() => Engines.Name(Id); }
internal sealed record TargetChoice(TargetKind Id) { public override string ToString() => Catalog.Get(Id).Name; }
internal sealed record StageChoice(int Number, LoadStage Stage)
{
    public override string ToString() => $"{Number}. {Stage.Name} · {Stage.Clients} кл.";
}
public sealed record MeasurementRow(PhaseResult Source, string Database, string Engine, string Operation, string Rows, string Clients, string Repeat,
    string Rate, string P99, string Errors, string State, string ClientRam, string ServerRam, string ClientCpu, string ServerCpu, string Gc, string Dropped, string Written);

public sealed class ReportDashboard : UserControl
{
    private BenchmarkReport _report = new();
    private PhaseResult[] _data = [];
    private readonly TextBlock _title = Ui.Label("Результаты", 16, Ui.Text, true);
    private readonly TextBlock _targets = Ui.Label("", 13);
    private readonly TextBlock _meta = Ui.Label("", 12);
    private readonly ComboBox _phase = new();
    private readonly ComboBox _rows = new();
    private readonly ComboBox _clients = new();
    private readonly ComboBox _stage = new();
    private readonly ComboBox _metric = new();
    private readonly ComboBox _axis = new() { ItemsSource = new[] { "Число клиентов", "Объём данных" }, SelectedIndex = 0 };
    private readonly ComboBox _comparison = new() { ItemsSource = new[] { "Базы данных", "Движки" }, SelectedIndex = 0 };
    private readonly ComboBox _engine = new();
    private readonly ComboBox _database = new();
    private readonly TransitioningContentControl _page = Ui.Presenter();
    private readonly Control _phaseFilter, _clientsFilter, _stageFilter, _metricFilter, _axisFilter, _comparisonFilter, _engineFilter, _databaseFilter;
    private readonly StackPanel _filters;
    private readonly Button[] _nav;
    private readonly ObservableCollection<MeasurementRow> _details = [];
    private readonly DataGrid _table = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserResizeColumns = true, CanUserSortColumns = false,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, SelectionMode = DataGridSelectionMode.Single, Background = Ui.B(Ui.Surface),
        BorderBrush = Ui.B(Ui.Line), BorderThickness = new Thickness(1), FontSize = 13, ClipToBounds = true };
    private readonly TextBox _detailText = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 170, FontSize = 13 };
    private readonly Expander _detailDrawer = new() { Header = "Расшифровка измерения" };
    private readonly CheckBox _all = new() { Content = "Все условия отчёта" };
    private readonly CheckBox _resources = new() { Content = "Ресурсы" };
    private readonly TextBlock _detailCount = Ui.Label("", 12);
    private readonly List<DataGridColumn> _resourceColumns = [];
    private readonly List<DataGridColumn> _conditionColumns = [];
    private readonly Control _detailPage;
    private bool _updating, _refreshingDetails;
    private int _selectedPage;
    public event Action? LibraryRequested;
    public event Action? FolderRequested;
    public int DisplayedResultCount => _data.Length;
    public int SelectedPage { get => _selectedPage; set { _selectedPage = Math.Clamp(value, 0, 2); RefreshPage(); } }

    public ReportDashboard()
    {
        _updating = true;
        var back = SidebarButton("←  К отчётам", () => LibraryRequested?.Invoke());
        var folder = SidebarButton("Папка отчёта", () => FolderRequested?.Invoke());
        _nav = new[] { "Сравнение", "Масштабирование", "Измерения" }
            .Select((name, index) => SidebarButton(name, () => SelectedPage = index)).ToArray();

        _phaseFilter = SidebarField("Операция", _phase);
        _clientsFilter = SidebarField("Клиентов", _clients);
        _stageFilter = SidebarField("Этап нагрузки", _stage);
        _metricFilter = SidebarField("Показатель", _metric);
        _axisFilter = SidebarField("По горизонтали", _axis);
        _comparisonFilter = SidebarField("Сравнивать", _comparison);
        _engineFilter = SidebarField("Движок", _engine);
        _databaseFilter = SidebarField("База данных", _database);
        _filters = Ui.Stack(10, _comparisonFilter, _engineFilter, _databaseFilter, _phaseFilter, SidebarField("Записей", _rows), _clientsFilter, _stageFilter, _metricFilter, _axisFilter);

        var sidebar = Ui.Stack(12, back, Ui.Stack(5, _title, _targets, _meta), Ui.Rule(), Ui.Stack(3, _nav),
            Ui.Rule(), _filters, folder);
        var root = Ui.Columns("228,20,*", Ui.Card(Ui.Scroll(sidebar), 12), _page);
        _metric.ItemsSource = new[] { new MetricChoice(AnalysisMetric.Rate, "Скорость"), new MetricChoice(AnalysisMetric.P99, "Задержка p99"),
            new MetricChoice(AnalysisMetric.ServerMemory, "Память базы"), new MetricChoice(AnalysisMetric.ClientMemory, "Память генератора"),
            new MetricChoice(AnalysisMetric.ServerCpu, "CPU базы"), new MetricChoice(AnalysisMetric.ClientCpu, "CPU генератора"), new MetricChoice(AnalysisMetric.GcPause, "Паузы GC"),
            new MetricChoice(AnalysisMetric.ErrorPercent, "Доля ошибок"), new MetricChoice(AnalysisMetric.DropPercent, "Доля пропусков"),
            new MetricChoice(AnalysisMetric.EndToEndP99, "p99 с ожиданием") };
        _metric.SelectedIndex = 0; _detailPage = BuildDetails(); Content = root;
        foreach (var box in new[] { _phase, _rows, _clients, _stage, _metric, _axis, _comparison, _engine, _database }) box.SelectionChanged += (_, _) => { if (!_updating) RefreshNow(); };
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(RefreshNow);
        _updating = false; RefreshNow();
    }

    private static Button SidebarButton(string text, Action action)
    {
        var button = Ui.Button(text, "quiet", action);
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Left;
        button.Padding = new Thickness(10, 6);
        button.MinHeight = 32;
        button.FontSize = 13;
        return button;
    }

    private static Control SidebarField(string title, ComboBox input)
    {
        input.MinHeight = 34;
        input.HorizontalAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetName(input, title);
        return Ui.Stack(5, Ui.Label(title, 12, Ui.Text), input);
    }
    private ReportSlice Slice => SliceFor((_phase.SelectedItem as PhaseChoice)?.Id ?? nameof(Scenario.PointRead));
    private ReportSlice SliceFor(string phase)
    {
        var rows = _rows.SelectedItem is long count ? count : 1000;
        if (_report.Config.Plan is { Stages.Count: > 0 } plan)
        {
            if (ReportAnalysis.SetupPhase(phase)) return Scope(new(phase, rows, plan.PeakClients));
            var choice = _stage.SelectedItem as StageChoice ?? new StageChoice(1, plan.Stages[0]);
            return Scope(new(phase, rows, choice.Stage.Clients, choice.Number));
        }
        return Scope(new(phase, rows, _clients.SelectedItem is int clients ? clients : 1));
    }
    private ReportSlice Scope(ReportSlice slice) => _comparison.SelectedIndex == 1
        ? slice with { Target = (_database.SelectedItem as TargetChoice)?.Id ?? _report.Config.Targets.FirstOrDefault() }
        : slice with { Engine = (_engine.SelectedItem as EngineChoice)?.Id ?? _report.Config.EngineIds.FirstOrDefault() ?? "csharp" };
    private AnalysisMetric Metric => (_metric.SelectedItem as MetricChoice)?.Id ?? AnalysisMetric.Rate;
    public void SetReport(BenchmarkReport report)
    {
        _updating = true;
        if (!ReferenceEquals(_report, report))
        {
            _selectedPage = 0;
            _metric.SelectedIndex = 0;
            _phase.SelectedIndex = -1;
            _stage.SelectedIndex = -1;
            _comparison.SelectedIndex = 0;
            _engine.SelectedIndex = -1;
            _database.SelectedIndex = -1;
        }
        _report = report;
        _data = report.Results.ToArray();
        _updating = false;
        RefreshNow();
    }
    public void SelectGraphView(bool scaling, AnalysisMetric metric = AnalysisMetric.Rate)
    {
        _updating = true;
        _metric.SelectedItem = ((IEnumerable<MetricChoice>)_metric.ItemsSource!).First(x => x.Id == metric);
        _updating = false; _selectedPage = scaling ? 1 : 0; RefreshNow();
    }
    public void SelectEngineComparison(bool enabled)
    { _comparison.SelectedIndex = enabled ? 1 : 0; RefreshNow(); }
    public void RefreshNow()
    {
        if (_updating) return; _updating = true;
        try
        {
            var previous = Slice;
            var previousStage = (_stage.SelectedItem as StageChoice)?.Number;
            var previousEngine = (_engine.SelectedItem as EngineChoice)?.Id;
            var previousTarget = (_database.SelectedItem as TargetChoice)?.Id;
            var engines = _report.Config.EngineIds.Concat(_data.Select(row => row.Engine)).Distinct().Select(id => new EngineChoice(id)).ToArray();
            _engine.ItemsSource = engines; _engine.SelectedItem = engines.FirstOrDefault(choice => choice.Id == previousEngine) ?? engines.FirstOrDefault();
            var databases = _report.Config.Targets.Concat(_data.Select(row => row.Target)).Distinct().Select(id => new TargetChoice(id)).ToArray();
            _database.ItemsSource = databases; _database.SelectedItem = databases.FirstOrDefault(choice => choice.Id == previousTarget) ?? databases.FirstOrDefault();
            var rows = _data.Select(r => r.DatasetRows).Concat(_report.Config.DatasetRows).Distinct().Order().ToArray();
            var clients = _data.Select(r => r.Clients).Concat(_report.Config.Clients).Distinct().Order().ToArray();
            _rows.ItemsSource = rows; _rows.SelectedItem = rows.Contains(previous.Rows) ? previous.Rows : rows.FirstOrDefault(1000L);
            _clients.ItemsSource = clients; _clients.SelectedItem = clients.Contains(previous.Clients) ? previous.Clients : clients.FirstOrDefault(1);
            var stages = _report.Config.Plan?.Stages.Select((step, index) => new StageChoice(index + 1, step)).ToArray() ?? [];
            _stage.ItemsSource = stages;
            _stage.SelectedItem = stages.FirstOrDefault(stage => stage.Number == previousStage) ?? stages.FirstOrDefault();
            var phases = _data.Select(row => row.Phase).Distinct().OrderBy(p => ReportAnalysis.SetupPhase(p) ? 1 : 0).Select(p => new PhaseChoice(p, ReportAnalysis.PhaseName(p, _report.Config.CustomWorkload))).ToArray();
            if (phases.Length == 0) phases = [new(nameof(Scenario.PointRead), ReportAnalysis.PhaseName(nameof(Scenario.PointRead)))];
            _phase.ItemsSource = phases; _phase.SelectedItem = phases.FirstOrDefault(p => p.Id == previous.Phase) ?? phases[0];
            var names = _report.Config.Targets.Concat(_data.Select(r => r.Target)).Distinct().Select(t => Catalog.Get(t).Name).ToArray();
            _title.Text = names.Length == 1 ? names[0] : $"Базы данных · {names.Length}";
            _targets.IsVisible = names.Length > 1;
            _targets.Text = string.Join(", ", names);
            var workload = _report.Config.Plan is { } plan ? $"{LoadPlan.Name(plan.Profile)} · {plan.Seconds:0.###} с на операцию" : $"{_report.Config.DurationSeconds:g} с на операцию";
            _meta.Text = $"{_report.StartedAt.ToLocalTime():dd MMM yyyy, HH:mm}\n{workload} · повторов: {_report.Config.Repeats}";
            if (_report.Config.CustomWorkload is { } custom) _meta.Text += "\n" + custom.Name;
            RefreshPage();
        }
        finally { _updating = false; }
    }
    private void RefreshPage()
    {
        _phaseFilter.IsVisible = _selectedPage != 2; _filters.IsVisible = _selectedPage != 2 || _all.IsChecked != true;
        var planned = _report.Config.Plan;
        var hasMultipleStages = planned?.Stages.Count > 1;
        var scalingTitle = hasMultipleStages ? "Ход нагрузки" : "Масштабирование";
        _nav[1].Content = scalingTitle;
        AutomationProperties.SetName(_nav[1], scalingTitle);
        _clientsFilter.IsVisible = planned is null;
        _stageFilter.IsVisible = hasMultipleStages && (_selectedPage == 2 || (_selectedPage == 0 && !ReportAnalysis.SetupPhase(Slice.Phase)));
        _metricFilter.IsVisible = _selectedPage == 0 && _data.Length > 0;
        _axisFilter.IsVisible = _selectedPage == 1 && !hasMultipleStages;
        var manyEngines = _report.Config.EngineIds.Concat(_data.Select(row => row.Engine)).Distinct().Count() > 1;
        _comparisonFilter.IsVisible = manyEngines;
        _engineFilter.IsVisible = manyEngines && _comparison.SelectedIndex == 0;
        _databaseFilter.IsVisible = manyEngines && _comparison.SelectedIndex == 1;
        for (var i = 0; i < _nav.Length; i++) Ui.Select(_nav[i], i == _selectedPage);
        if (_selectedPage == 2) RefreshDetails();
        _page.Content = _selectedPage switch { 1 => BuildScaling(), 2 => _detailPage, _ => BuildComparison() };
    }
    private Control BuildComparison()
    {
        if (_data.Length == 0) return Ui.Card(Ui.Stack(12, Ui.Label("Измерений пока нет", 22, Ui.Text, true), Ui.Label(_report.Failures.Count > 0 ? string.Join("\n", _report.Failures) : "Они появятся после завершения первого этапа.")));
        var slice = Slice; var metric = Metric; var description = ReportAnalysis.Describe(metric, slice.Phase);
        var series = ReportAnalysis.Compare(_report, _data, slice, metric); var narrative = ReportAnalysis.Explain(_report, _data, slice, metric);
        var chart = new ComparisonChart(); chart.SetData(series, description);
        var chartHeader = Ui.Stack(5, Ui.Label(description.Title, 18, Ui.Text, true), Ui.Label(description.Unit + "  ·  " + Direction(description), 12));
        ToolTip.SetTip(chartHeader, description.Explanation);
        var chartPanel = Ui.Card(Ui.Stack(12, chartHeader, chart,
            Ui.Label("Столбец — медиана. При нескольких повторах точки и линия показывают их разброс.", 12)), 20);
        var matched = _data.Where(r => ReportAnalysis.Matches(r, slice)).ToArray();
        var notes = Ui.Stack(12, Ui.Label(description.Explanation, 13), Ui.Label(narrative.Explanation, 13));
        foreach (var note in narrative.Limitations) notes.Children.Add(Ui.Label("• " + note, 13));
        var repeated = series.Where(item => item.Values.Length > 1).ToArray();
        if (repeated.Length > 0)
        {
            notes.Children.Add(Ui.Rule());
            notes.Children.Add(Ui.Label("Диапазоны повторов", 14, Ui.Text, true));
            foreach (var item in repeated)
            {
                var spread = ComparisonQuality.SpreadPercent(item);
                notes.Children.Add(Ui.Label(item.Name + $" · {item.ValidRepeats} повт.\n"
                    + ReportAnalysis.Number(item.Minimum, description.Unit) + " — " + ReportAnalysis.Number(item.Maximum, description.Unit)
                    + (spread is { } value && double.IsFinite(value) ? "\nРазброс от медианы: " + ReportAnalysis.Number(value, "%") : ""), 13));
            }
            notes.Children.Add(Ui.Label("Разброс = (максимум − минимум) / медиана. Порог предупреждения 10% — подсказка, а не проверка статистической значимости.", 12));
        }
        var stage = slice.StageNumber is not null && _stage.SelectedItem is StageChoice choice ? choice.Stage : null;
        var condition = $"{ReportAnalysis.Count(slice.Rows)} {(_report.Config.CustomWorkload is null ? "записей" : "базовых строк")} · клиентов: {slice.Clients}" + (stage is null ? "" : $" · {stage.Name}");
        if (stage?.OperationsPerSecond is { } goal) condition += $" · цель {goal:N0} оп./с";
        var story = Ui.Stack(16, Ui.Badge(narrative.Review.Label, narrative.Preliminary ? Ui.Amber : Ui.Green),
            Ui.Label(narrative.Headline, 21, Ui.Text, true), Ui.Label(condition, 13),
            Ui.Label(ComparisonQuality.EnvironmentDescription(_report), 12), Ui.Rule(),
            Stat("Повторов с данными (мин.)", series.Where(s => s.Median.HasValue).Select(s => s.ValidRepeats).DefaultIfEmpty(0).Min().ToString()),
            Stat("Ошибок запросов", ReportAnalysis.Count(matched.Sum(r => r.Errors))), Stat("Пропусков", ReportAnalysis.Count(matched.Sum(r => r.Dropped))));
        if (narrative.Review.State is not ComparisonState.Observed)
            story.Children.Insert(2, Ui.Label(narrative.Review.Summary, 13));
        if (narrative.ObservedDifference is { } observedDifference)
            story.Children.Insert(2, Ui.Label("По медианам: " + observedDifference, 13));
        var scheduled = matched.Sum(r => r.PlannedOperations);
        if (stage?.OperationsPerSecond is not null && scheduled > 0)
            story.Children.Add(Stat("Успешно из запланированных",
                ReportAnalysis.Number(matched.Sum(r => r.SuccessfulOperations) * 100d / scheduled, "%")));
        notes.Margin = new Thickness(0, 12, 0, 8);
        var result = Ui.Stack(16, Ui.Columns("2.15*,16,*", chartPanel, Ui.Card(story, 20)));
        result.Children.Add(new Expander { Header = "Почему такой вывод", Content = notes });
        var operations = new Expander { Header = "Все операции" };
        operations.PropertyChanged += (_, change) =>
        {
            if (change.Property == Expander.IsExpandedProperty && operations.IsExpanded && operations.Content is null)
                operations.Content = BuildOperationTable();
        };
        result.Children.Add(operations);
        if (_report.Config.CustomWorkload is { } customWorkload)
        {
            var breakdown = Ui.Stack(10, Ui.Label("Фактические действия в выбранном измерении. Задержка относится ко всей операции, включая транзакцию.", 12));
            foreach (var measured in matched.Where(row => row.OperationBreakdown is not null))
            {
                breakdown.Children.Add(Ui.Label(Catalog.Get(measured.Target).Name + " · повтор " + measured.Repeat, 14, Ui.Text, true));
                foreach (var detail in measured.OperationBreakdown!) breakdown.Children.Add(Ui.Label($"{detail.Name}: {detail.SuccessfulOperations:N0} успешных · {detail.Errors:N0} ошибок · p99 {ReportAnalysis.Number(detail.ServiceLatency.P99Ms, "мс")}", 13));
            }
            result.Children.Add(new Expander { Header = "Действия внутри своего сценария", Content = breakdown });
            result.Children.Add(new Expander { Header = "Описание и воспроизводимость сценария", Content = Ui.Stack(12, Ui.Label(customWorkload.Name, 16, Ui.Text, true), Ui.Label(customWorkload.Description),
                Ui.Label("SHA-256 описания: " + customWorkload.Fingerprint + "\nРежим: " + customWorkload.Mode + "\nСброс между отдельными операциями: " + customWorkload.ResetBetweenOperations, 12),
                Ui.Label(string.Join("\n", customWorkload.Datasets.Select(table => table.Name + " · " + table.RowCount(slice.Rows).ToString("N0") + " строк" + (table.Import is null ? " · генератор" : " · " + table.Import.File + " · SHA-256 " + table.Import.Sha256))), 12)) });
        }
        var conditions = Ui.Stack(12, BuildMetricTable(slice), Ui.Label(ReportAnalysis.PhaseExplanation(slice.Phase)),
            Ui.Label(ComparisonQuality.EnvironmentDescription(_report), 14, Ui.Text), Ui.Label("ОС: " + _report.Environment.GetValueOrDefault("os", "не сохранена") +
            "\nЛогических процессоров: " + _report.Environment.GetValueOrDefault("logical_cpus", "—") + $"\nЛимит серверной БД: {_report.Config.CpuLimit:g} CPU · {_report.Config.MemoryMb:N0} МБ RAM"),
            Ui.Label("CPU: " + _report.Environment.GetValueOrDefault("cpu_model", "модель не сохранена") + "\nRAM окружения: " +
                (long.TryParse(_report.Environment.GetValueOrDefault("host_visible_memory_bytes"), out var ram) ? $"{ram / (1024d * 1024 * 1024):N1} ГБ" : "не сохранена"), 13),
            Ui.Label("SQLite работает в генераторе; Redis/Valkey, SQL и ClickHouse используют разные профили хранения. Характеристики диска не собираются.", 13));
        if (_report.Config.RemoteServer is { } server)
            conditions.Children.Add(Ui.Label("Сервер: " + server.DisplayName + " · " + _report.Environment.GetValueOrDefault("remote_host", server.Host) +
                "\nГенератор и временная БД используют ресурсы этого сервера. Сетевая задержка от ПК до сервера в запросы не включена.", 13));
        if (_report.Config.VirtualMachine is not null)
            conditions.Children.Add(Ui.Label("База и генератор находятся в одной VM. Физическое железо и кэши основного компьютера остаются общими.", 13));
        if (_report.Failures.Count > 0 || _report.Warnings.Count > 0) conditions.Children.Add(Ui.Label(string.Join("\n", _report.Failures.Concat(_report.Warnings)), 13, Ui.Amber));
        result.Children.Add(new Expander { Header = "Показатели, условия и замечания", Content = conditions }); conditions.Margin = new Thickness(0, 12, 0, 8);
        return Ui.Scroll(result);
    }
    private static string Direction(MetricDescription description) => description.Direction switch { MetricDirection.Higher => "больше — выше скорость", MetricDirection.Lower => "меньше — лучше", _ => "сравнивайте вместе со скоростью" };
    private Control BuildOperationTable()
    {
        var panel = Ui.Stack(16, Ui.Label("Скорость или время подготовки. Выберите операцию, чтобы открыть её график.", 13));
        var phases = ((IEnumerable<PhaseChoice>)_phase.ItemsSource!).ToArray();
        foreach (var phase in phases)
        {
            var values = ReportAnalysis.Compare(_report, _data, SliceFor(phase.Id), AnalysisMetric.Rate);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("260,*"), Margin = new Thickness(0, 3) };
            var open = Ui.Button(phase.Name, "quiet", () => _phase.SelectedItem = phase);
            open.HorizontalAlignment = HorizontalAlignment.Left;
            row.Children.Add(open);
            var numbers = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            foreach (var item in values)
            {
                var value = Ui.Label(item.Name + "  " + ReportAnalysis.Number(item.Median, item.Median.HasValue ? ReportAnalysis.RateUnit(phase.Id) : ""), 13);
                value.Margin = new Thickness(10, 4, 16, 4);
                if (item.Median is null) ToolTip.SetTip(value, item.MissingReason);
                numbers.Children.Add(value);
            }
            Grid.SetColumn(numbers, 1); row.Children.Add(numbers); panel.Children.Add(row); panel.Children.Add(Ui.Rule());
        }
        return panel;
    }
    private static Control Stat(string label, string value)
    { var row = new DockPanel(); var right = Ui.Label(value, 14, Ui.Text, true); DockPanel.SetDock(right, Dock.Right); row.Children.Add(right); row.Children.Add(Ui.Label(label, 13)); return row; }
    private Control BuildMetricTable(ReportSlice slice)
    {
        var rates = ReportAnalysis.Compare(_report, _data, slice, AnalysisMetric.Rate);
        var p99 = ReportAnalysis.Compare(_report, _data, slice, AnalysisMetric.P99);
        var memory = ReportAnalysis.Compare(_report, _data, slice, AnalysisMetric.ServerMemory);
        var panel = Ui.Stack(0); var header = TableLine(_comparison.SelectedIndex == 1 ? "Движок" : "База данных", slice.Phase == "index_build" || WorkloadPhases.Duration(slice.Phase) ? "Подготовка, с" : "Скорость, " + ReportAnalysis.RateUnit(slice.Phase), "Ответ p99, мс", "Память базы, МБ", true);
        panel.Children.Add(header); panel.Children.Add(Ui.Rule());
        foreach (var item in rates)
        {
            var line = TableLine(item.Name, ReportAnalysis.Number(item.Median), ReportAnalysis.Number(p99.FirstOrDefault(s => s.Id == item.Id)?.Median), ReportAnalysis.Number(memory.FirstOrDefault(s => s.Id == item.Id)?.Median));
            ToolTip.SetTip(line, item.Median is null ? item.MissingReason : "Медианы корректных повторов; память — максимум среди выборок каждого этапа.");
            panel.Children.Add(line); panel.Children.Add(Ui.Rule());
        }
        return panel;
    }
    private static Control TableLine(string name, string rate, string latency, string memory, bool header = false)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("1.4*,*,*,*"), Margin = new Thickness(4, 12) };
        var values = new[] { name, rate, latency, memory };
        for (var i = 0; i < values.Length; i++) { var text = Ui.Label(values[i], header ? 12 : 14, header ? Ui.Muted : Ui.Text); Grid.SetColumn(text, i); row.Children.Add(text); } return row;
    }
    private Control BuildScaling()
    {
        var slice = Slice; var byRows = _axis.SelectedIndex == 1;
        if (_report.Config.Plan is { Stages.Count: > 1 } plan)
        {
            if (ReportAnalysis.SetupPhase(slice.Phase))
                return Ui.Card(Ui.Stack(10, Ui.Label("Выберите операцию с этапами нагрузки", 20, Ui.Text, true), Ui.Label("Загрузка данных и создание индекса измеряются один раз за запуск.")));
            return BuildStageCharts(slice, plan);
        }
        var heading = Ui.Stack(5, Ui.Label("Как меняется результат", 20, Ui.Text, true),
            Ui.Label(byRows ? $"Фиксировано: {slice.Clients} клиентов" : $"Фиксировано: {ReportAnalysis.Count(slice.Rows)} записей", 13));
        var rateDescription = ReportAnalysis.Describe(AnalysisMetric.Rate, slice.Phase);
        var rates = ReportAnalysis.Trends(_report, _data, slice, AnalysisMetric.Rate, byRows);
        var speed = new ScalingChart(); speed.SetData(rates, rateDescription, byRows);
        var latency = new ScalingChart(); latency.SetData(ReportAnalysis.Trends(_report, _data, slice, AnalysisMetric.P99, byRows), ReportAnalysis.Describe(AnalysisMetric.P99, slice.Phase), byRows);
        var left = Ui.Card(Ui.Stack(12, Ui.Label(rateDescription.Title, 17, Ui.Text, true), Ui.Label(rateDescription.Unit, 12), speed));
        var right = Ui.Card(ReportAnalysis.SetupPhase(slice.Phase) ? Ui.Stack(12, Ui.Label("Задержка запросов", 17, Ui.Text, true), Ui.Label("На этапе подготовки не измеряется."))
            : Ui.Stack(12, Ui.Label("Медленные ответы · p99", 17, Ui.Text, true), Ui.Label("мс · меньше — быстрее отклик", 12), latency));
        var summary = Ui.Stack(10);
        foreach (var series in rates.Where(s => s.Points.Length > 1 && s.Points[0].Median > 0))
        {
            var first = series.Points[0]; var last = series.Points[^1]; var delta = (last.Median / first.Median - 1) * 100;
            summary.Children.Add(Ui.Label($"{series.Name}: {first.X:N0} → {last.X:N0} {(byRows ? "записей" : "клиентов")}   ·   {(slice.Phase == "index_build" ? "время" : "скорость")} {delta:+0.0;-0.0;0}%", 14, Ui.Text));
        }
        if (ReportAnalysis.Explain(_report, _data, slice, AnalysisMetric.Rate).Preliminary) summary.Children.Add(Ui.Label("Пробный или неполный прогон: устойчивость изменения нужно проверить повторами.", 13, Ui.Amber));
        return Ui.Scroll(Ui.Stack(22, heading, Ui.Columns("*,20,*", left, right), summary, Ui.Label("Точки — измеренные уровни. Линия соединяет их; промежуточные результаты не рассчитываются.", 12)));
    }

    private Control BuildStageCharts(ReportSlice slice, LoadPlan plan)
    {
        Control Chart(AnalysisMetric metric)
        {
            var description = ReportAnalysis.Describe(metric, slice.Phase);
            var chart = new ScalingChart();
            chart.SetStages(ReportAnalysis.StageTrends(_data, slice, metric), description, plan.Stages);
            return Ui.Card(Ui.Stack(8, Ui.Label(description.Title, 17, Ui.Text, true), chart), 18);
        }

        var stages = new WrapPanel();
        foreach (var (stage, index) in plan.Stages.Select((stage, index) => (stage, index)))
        {
            var badge = Ui.Badge($"{index + 1}. {stage.Name} · {stage.Clients} кл. · {stage.Seconds:0.###} с");
            badge.Margin = new Thickness(0, 0, 8, 8);
            stages.Children.Add(badge);
        }
        var metrics = Ui.Stack(16, Ui.Columns("*,16,*", Chart(AnalysisMetric.Rate), Chart(AnalysisMetric.P99)),
            new Expander { Header = "Ошибки и память по этапам", Content = Ui.Columns("*,16,*", Chart(AnalysisMetric.ErrorPercent),
                Chart(_report.Config.Targets.All(Catalog.Embedded) ? AnalysisMetric.ClientMemory : AnalysisMetric.ServerMemory)) });
        Control schedule = plan.Stages.Count <= 5 ? stages : new Expander { Header = $"План нагрузки · {plan.Stages.Count} этапов", Content = stages };
        return Ui.Scroll(Ui.Stack(12, Ui.Label(LoadPlan.Name(plan.Profile), 20, Ui.Text, true), schedule, metrics,
            Ui.Label("Номера на графике соответствуют этапам. Для скорости показаны все завершённые измерения, включая перегрузку; p99 рассчитан по успешным операциям.", 12)));
    }
    private Control BuildDetails()
    {
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,16,Auto"), ClipToBounds = true };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24, Children = { _all, _resources, _detailCount }, Margin = new Thickness(0, 0, 0, 14) };
        _detailCount.VerticalAlignment = VerticalAlignment.Center; root.Children.Add(tools);
        // The table has no sorting, so its headers do not need space for a sort icon.
        _table.Resources["DataGridSortIconMinWidth"] = 0d;
        _table.ItemsSource = _details;
        foreach (var (header, property, width) in new[]
        {
            ("База", nameof(MeasurementRow.Database), new DataGridLength(104)),
            ("Движок", nameof(MeasurementRow.Engine), new DataGridLength(95)),
            ("Операция", nameof(MeasurementRow.Operation), new DataGridLength(1, DataGridLengthUnitType.Star)),
            ("Повтор", nameof(MeasurementRow.Repeat), new DataGridLength(56)),
            ("Скорость / время", nameof(MeasurementRow.Rate), new DataGridLength(116)),
            ("p99, мс", nameof(MeasurementRow.P99), new DataGridLength(64)),
            ("Ошибки", nameof(MeasurementRow.Errors), new DataGridLength(64)),
            ("Статус", nameof(MeasurementRow.State), new DataGridLength(98))
        })
            _table.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(property), Width = width, MinWidth = width.IsStar ? 160 : 40 });
        foreach (var (header, property) in new[] { ("Записей", nameof(MeasurementRow.Rows)), ("Клиентов", nameof(MeasurementRow.Clients)) })
        { var column = new DataGridTextColumn { Header = header, Binding = new Binding(property), IsVisible = false }; _conditionColumns.Add(column); _table.Columns.Add(column); }
        foreach (var (header, property) in new[] { ("RAM генератора, МБ", nameof(MeasurementRow.ClientRam)), ("RAM базы, МБ", nameof(MeasurementRow.ServerRam)),
            ("CPU генератора, %", nameof(MeasurementRow.ClientCpu)), ("CPU базы, %", nameof(MeasurementRow.ServerCpu)), ("GC, мс", nameof(MeasurementRow.Gc)),
            ("Пропуски", nameof(MeasurementRow.Dropped)), ("Запись, МБ", nameof(MeasurementRow.Written)) })
        { var column = new DataGridTextColumn { Header = header, Binding = new Binding(property), IsVisible = false }; _resourceColumns.Add(column); _table.Columns.Add(column); }
        _all.Click += (_, _) => { foreach (var c in _conditionColumns) c.IsVisible = _all.IsChecked == true; RefreshPage(); };
        _resources.Click += (_, _) => { foreach (var c in _resourceColumns) c.IsVisible = _resources.IsChecked == true; };
        _table.SelectionChanged += (_, _) => { if (_table.SelectedItem is MeasurementRow row) { _detailText.Text = Describe(row.Source); if (!_refreshingDetails) _detailDrawer.IsExpanded = true; } };
        Grid.SetRow(_table, 1); root.Children.Add(_table); _detailDrawer.Content = _detailText; Grid.SetRow(_detailDrawer, 3); root.Children.Add(_detailDrawer); return root;
    }
    private void RefreshDetails()
    {
        _refreshingDetails = true; var selected = (_table.SelectedItem as MeasurementRow)?.Source;
        var slice = _report.Config.Plan is null ? Slice : SliceFor(nameof(Scenario.PointRead));
        var rows = _all.IsChecked == true ? _data : _data.Where(r => r.DatasetRows == slice.Rows && r.Clients == slice.Clients && r.Stage?.Number == slice.StageNumber && ReportAnalysis.IdentityMatches(r, slice)).ToArray();
        var showStage = _report.Config.Plan?.Stages.Count > 1;
        _details.Clear();
        foreach (var r in rows) _details.Add(new(r, Catalog.Get(r.Target).Name, Engines.Name(r.Engine), ReportAnalysis.PhaseName(r.Phase, _report.Config.CustomWorkload) + (showStage && r.Stage is { } stage ? " · " + stage.Name : ""), ReportAnalysis.Count(r.DatasetRows), r.Clients.ToString(), r.Repeat.ToString(),
            ReportAnalysis.Number(ReportAnalysis.Rate(r), ReportAnalysis.RateUnit(r.Phase)), ReportAnalysis.Number(r.ServiceLatency?.P99Ms), ReportAnalysis.Count(r.Errors),
            r.Cancelled ? "Остановлен" : !r.Verified ? "Не проверен" : r.Errors > 0 ? "Есть ошибки" : "Проверен", ReportAnalysis.Number(r.Client?.RssSampledMaxMb),
            ReportAnalysis.Number(r.Server?.MemorySampledMaxMb), ReportAnalysis.Number(r.Client?.CpuPercent), ReportAnalysis.Number(r.Server?.CpuMeanPercent),
            ReportAnalysis.Number(r.Client?.GcPauseMs), ReportAnalysis.Count(r.Dropped), ReportAnalysis.Number(r.Server?.WrittenMb)));
        _detailCount.Text = _details.Count + " измерений"; _table.SelectedItem = _details.FirstOrDefault(r => r.Source == selected) ?? _details.FirstOrDefault();
        _refreshingDetails = false;
    }
    private static string Describe(PhaseResult r)
    {
        var text = $"{Catalog.Get(r.Target).Name} · {Engines.Name(r.Engine)} · {ReportAnalysis.PhaseName(r.Phase)} · повтор {r.Repeat}\n"
            + (r.Stage is { } stage ? $"Этап {stage.Number}. {stage.Name} · {stage.TargetSeconds:0.###} с по плану\n" : "")
            + (r.Stage?.OperationsPerSecond is { } rate ? $"Целевая частота: {rate:N0} оп./с · запланировано: {r.PlannedOperations:N0}\n" : "")
            + $"{ReportAnalysis.Count(r.DatasetRows)} записей · {r.Clients} клиентов · {ReportAnalysis.Number(r.ElapsedSeconds, "с")}\n"
            + ReportAnalysis.PhaseExplanation(r.Phase) + "\n\n"
            + "Результат: " + ReportAnalysis.Number(ReportAnalysis.Rate(r), ReportAnalysis.RateUnit(r.Phase)) + $"; успешных операций: {r.SuccessfulOperations:N0}; ошибок: {r.Errors:N0}; пропусков: {r.Dropped:N0}.\n"
            + "Обычный ответ p50: " + ReportAnalysis.Number(r.ServiceLatency?.P50Ms, "мс") + "; медленный ответ p99: " + ReportAnalysis.Number(r.ServiceLatency?.P99Ms, "мс") + ".\n"
            + "Генератор: RAM " + ReportAnalysis.Number(r.Client?.RssSampledMaxMb, "МБ") + "; CPU " + ReportAnalysis.Number(r.Client?.CpuPercent, "%") + "; GC " + ReportAnalysis.Number(r.Client?.GcPauseMs, "мс") + ".\n"
            + (Catalog.Embedded(r.Target) ? "Встраиваемая база работает внутри генератора. Отдельных серверных CPU и RAM нет."
                : "База: RAM " + ReportAnalysis.Number(r.Server?.MemorySampledMaxMb, "МБ") + "; CPU " + ReportAnalysis.Number(r.Server?.CpuMeanPercent, "%") + "; запись на диск " + ReportAnalysis.Number(r.Server?.WrittenMb, "МБ") + ".");
        if (r.ErrorExamples.Count > 0) text += "\n\n" + string.Join("\n", r.ErrorExamples.Select(e => e.Value + " × " + e.Key)); return text;
    }
}
