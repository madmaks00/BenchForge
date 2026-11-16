using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using BenchForge.Contracts;
using BenchForge.Coordinator;

namespace BenchForge.Desktop;

public sealed class ExperimentSetupView : UserControl
{
    private readonly Dictionary<TargetKind, (Button Button, Border Indicator)> _databases = [];
    private readonly HashSet<TargetKind> _selected = [TargetKind.Sqlite];
    private readonly Dictionary<string, TextBox> _fields = [];
    private readonly TransitioningContentControl _presenter = Ui.Presenter();
    private readonly RowDefinition _pageRow = new(GridLength.Auto);
    private readonly Button[] _steps;
    private readonly Control[] _pages;
    private readonly Button _back = Ui.Button("← Назад", "quiet");
    private readonly Button _next = Ui.Button("К нагрузке →", "primary");
    private readonly TextBlock _error = Ui.Label("", 13, Ui.Danger);
    private readonly TextBlock _selection = Ui.Label("Выбрано: 1");
    private readonly StackPanel _review = Ui.Stack(20);
    private readonly TextBlock _checkStatus = Ui.Label("");
    private readonly LoadPlanEditor _load = new();
    private readonly ComboBox _scenario = new() { ItemsSource = new[] { "Чтение по умолчанию", "Все доступные операции" }.Concat(Enum.GetValues<Scenario>().Select(Catalog.Name)).ToArray(), SelectedIndex = 0 };
    private readonly CheckBox _hotspot = new() { Content = "Чаще обращаться к популярным ключам" };
    private int _step, _reviewRevision;
    public event Action<BenchmarkConfig>? StartRequested;
    public event Action<BenchmarkConfig>? SaveRequested;

    public ExperimentSetupView()
    {
        var root = new Grid { RowDefinitions = new RowDefinitions { new(GridLength.Auto), _pageRow, new(GridLength.Auto), new(GridLength.Auto) } };
        _steps = new[] { "Базы данных", "Нагрузка", "Проверка" }
            .Select((label, index) => Ui.Button(label, "quiet", () => GoTo(index))).ToArray();
        var stepBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var button in _steps)
        {
            button.MinHeight = 32;
            button.Padding = new Thickness(14, 5);
            button.FontSize = 13;
            stepBar.Children.Add(button);
        }
        var steps = new Border { HorizontalAlignment = HorizontalAlignment.Left, Background = Ui.B(Ui.Input), BorderBrush = Ui.B(Ui.Line),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(3),
            Margin = new Thickness(0, 0, 18, 18), Child = stepBar };
        root.Children.Add(steps);
        _pages = [BuildDatabases(), BuildWorkload(), Ui.Scroll(_review)];
        Grid.SetRow(_presenter, 1); root.Children.Add(_presenter);
        _error.Margin = new Thickness(0, 12, 0, 0); _error.IsVisible = false; Grid.SetRow(_error, 2); root.Children.Add(_error);
        var footer = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _back, _next } };
        DockPanel.SetDock(actions, Dock.Right); footer.Children.Add(actions); footer.Children.Add(_selection);
        _selection.VerticalAlignment = VerticalAlignment.Center; Grid.SetRow(footer, 3); root.Children.Add(footer);
        _back.Click += (_, _) => GoTo(_step - 1);
        _next.Click += (_, _) => { if (_step < 2) GoTo(_step + 1); else { try { StartRequested?.Invoke(ReadConfig()); } catch (Exception e) { Error(e.Message); } } };
        Content = root; GoTo(0);
    }
    private Control BuildDatabases()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*"), RowDefinitions = new RowDefinitions("Auto,8,Auto,8,Auto,8,Auto") };
        for (var i = 0; i < Catalog.Targets.Length; i++)
        {
            var target = Catalog.Targets[i];
            var tick = Ui.Label("✓", 12, Ui.SelectedText, true);
            tick.HorizontalAlignment = HorizontalAlignment.Center;
            tick.VerticalAlignment = VerticalAlignment.Center;
            var indicator = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center, Child = tick };
            var tile = new Border { Width = 42, Height = 42, CornerRadius = new CornerRadius(8), Background = Ui.B(Ui.Raised),
                Child = BrandIcons.DatabaseMark(target.Kind, 29) };
            var labels = Ui.Stack(3, Ui.Label(target.Name, 15, Ui.Text, true), Ui.Label(target.Family, 12));
            labels.VerticalAlignment = VerticalAlignment.Center;
            var row = new DockPanel();
            DockPanel.SetDock(indicator, Dock.Right); row.Children.Add(indicator);
            DockPanel.SetDock(tile, Dock.Left); tile.Margin = new Thickness(0, 0, 13, 0); row.Children.Add(tile);
            row.Children.Add(labels);
            var button = Ui.Button(target.Name);
            button.Content = row;
            button.Padding = new Thickness(12, 9);
            button.MinHeight = 62;
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            _databases.Add(target.Kind, (button, indicator));
            PaintDatabase(target.Kind);
            button.Click += (_, _) =>
            {
                if (!_selected.Remove(target.Kind)) _selected.Add(target.Kind);
                PaintDatabase(target.Kind);
                _selection.Text = "Выбрано: " + _selected.Count;
                _error.IsVisible = false;
            };
            Grid.SetColumn(button, i % 2 * 2);
            Grid.SetRow(button, i / 2 * 2);
            grid.Children.Add(button);
        }
        return Ui.Scroll(Ui.Stack(14, Ui.Label("Выберите базы для тестирования", 18, Ui.Text, true), grid,
            Ui.Label("Серверным базам нужен Docker в режиме Linux-контейнеров.", 13)));
    }

    private void PaintDatabase(TargetKind target)
    {
        var (button, indicator) = _databases[target];
        var selected = _selected.Contains(target);
        Ui.Select(button, selected);
        indicator.Background = selected ? Ui.B(Ui.Selected) : null;
        indicator.BorderBrush = Ui.B(selected ? Ui.Accent : Ui.Line);
        indicator.Child!.IsVisible = selected;
    }
    private Control BuildWorkload()
    {
        var workload = Ui.Columns("1.4*,16,*,16,*", Ui.Field("Операции", _scenario),
            Field("rows", "Записей в наборе", "1000", "Можно задать несколько объёмов через запятую"),
            Field("repeats", "Повторов", "1", "Повторы позволяют увидеть разброс результатов"));
        var advanced = Ui.Stack(20,
            Ui.Columns("*,24,*", Field("payload", "Размер текстового поля, байт", "256"), Field("batch", "Записей в пакете", "100")),
            Ui.Columns("*,24,*", Field("warmup", "Прогрев, секунд", "1"), Field("seed", "Начальное значение генератора", "42")),
            Ui.Columns("*,24,*", Field("cpu", "Лимит CPU базы", "2"), Field("memory", "Лимит памяти базы, МБ", "2048")), _hotspot);
        advanced.Margin = new Thickness(0, 20, 0, 8);
        return Ui.Scroll(Ui.Stack(16, Ui.Label("Тип теста", 17, Ui.Text, true), _load, workload,
            new Expander { Header = "Дополнительные параметры", Content = advanced }));
    }
    private Control Field(string key, string label, string value, string? hint = null)
    {
        var input = new TextBox { Text = value, PlaceholderText = hint }; if (hint is not null) ToolTip.SetTip(input, hint);
        _fields.Add(key, input); return Ui.Field(label, input, hint);
    }
    private void BuildReview()
    {
        if (_checkStatus.Parent is Panel parent) parent.Children.Remove(_checkStatus);
        _review.Children.Clear(); _checkStatus.Text = ""; var config = ReadConfig(); var revision = ++_reviewRevision;
        var plan = config.Plan!;
        var databaseNames = string.Join("  ·  ", config.Targets.Select(t => Catalog.Get(t).Name));
        var summary = Ui.Stack(14, Ui.Label("Настройки запуска", 17, Ui.Text, true), Ui.Label(databaseNames, 17, Ui.Accent, true), Ui.Rule(),
            SummaryRow("Объём данных", string.Join(" / ", config.DatasetRows.Select(ReportAnalysis.Count)) + " записей"),
            SummaryRow("Тип теста", LoadPlan.Name(plan.Profile)),
            SummaryRow("Каждая операция", $"{plan.Seconds:0.###} с × {config.Repeats} повтор(ов)"),
            SummaryRow("Операции", _scenario.SelectedItem?.ToString() ?? "Чтение по умолчанию"));
        var schedule = Ui.Stack(8);
        foreach (var stage in plan.Stages)
        {
            var rate = stage.OperationsPerSecond is { } value ? $" · цель {value:N0} оп./с" : "";
            schedule.Children.Add(SummaryRow(stage.Name, $"{stage.Seconds:0.###} с · клиентов: {stage.Clients}{rate}"));
        }
        summary.Children.Add(new Expander { Header = $"План нагрузки · {plan.Stages.Count} эт.", IsExpanded = plan.Stages.Count <= 5, Content = schedule });
        var check = Ui.Button("Проверить готовность");
        check.Click += async (_, _) =>
        {
            check.IsEnabled = false; _checkStatus.Text = "Проверяю компоненты…";
            try { var problems = await ExperimentRunner.PreflightAsync(config, CancellationToken.None); if (revision == _reviewRevision) { _checkStatus.Text = problems.Count == 0 ? "Компоненты готовы к запуску" : string.Join("\n", problems); _checkStatus.Foreground = Ui.B(problems.Count == 0 ? Ui.Green : Ui.Amber); } }
            catch (Exception e) { if (revision == _reviewRevision) { _checkStatus.Text = e.Message; _checkStatus.Foreground = Ui.B(Ui.Amber); } }
            finally { check.IsEnabled = true; }
        };
        var conditions = Ui.Stack(18, Ui.Label("Окружение", 17, Ui.Text, true),
            Ui.Label(config.Targets.All(t => t == TargetKind.Sqlite) ? "SQLite · локальный файл" : "Docker · отдельные тестовые контейнеры", 14, Ui.Text),
            Ui.Label(config.Targets.All(t => t == TargetKind.Sqlite) ? "База работает внутри генератора нагрузки." : $"На каждую серверную базу: {config.CpuLimit:g} CPU, {config.MemoryMb:N0} МБ памяти."), check, _checkStatus);
        var save = Ui.Button("Сохранить настройки", "quiet", () => SaveRequested?.Invoke(ReadConfig())); save.HorizontalAlignment = HorizontalAlignment.Left;
        _review.Children.Add(Ui.Columns("1.6*,20,*", Ui.Card(summary), Ui.Card(conditions)));
        var seconds = config.Targets.Sum(target => config.Operations(target).Count) * plan.Seconds * config.Repeats * config.DatasetRows.Count;
        _review.Children.Add(Ui.Label($"Время самих измерений по плану: {TimeSpan.FromSeconds(seconds):g}. Подготовка баз и проверка данных займут дополнительное время.", 13));
        var skipped = config.Targets.Where(t => config.Operations(t).Count == 0).Select(t => Catalog.Get(t).Name).ToArray();
        if (skipped.Length > 0) _review.Children.Add(Ui.Label($"Выбранная операция недоступна для {string.Join(", ", skipped)}. Для них будет только подготовка набора.", 13, Ui.Amber));
        _review.Children.Add(save);
    }
    private static Control SummaryRow(string label, string value)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*") }; row.Children.Add(Ui.Label(label));
        var text = Ui.Label(value, 14, Ui.Text); Grid.SetColumn(text, 1); row.Children.Add(text); return row;
    }
    public void GoTo(int step)
    {
        if (step is < 0 or > 2) return;
        try
        {
            if (step > 0 && _selected.Count == 0) throw new ArgumentException("Выберите хотя бы одну базу данных.");
            if (step == 2) BuildReview();
            _error.IsVisible = false; _step = step;
            for (var i = 0; i < _steps.Length; i++) Ui.Select(_steps[i], i == step);
            _pageRow.Height = step == 0 ? GridLength.Auto : GridLength.Star;
            _back.IsVisible = step > 0;
            var nextAction = step switch { 0 => "К нагрузке", 1 => "К проверке", _ => "Запустить эксперимент" };
            _next.Content = nextAction + " →";
            AutomationProperties.SetName(_next, nextAction);
            _selection.Text = step == 0 ? "Выбрано: " + _selected.Count : step == 1 ? "" : "Результаты сохраняются автоматически";
            _presenter.Content = _pages[step];
        }
        catch (Exception e) { Error(e.Message); }
    }
    private void Error(string message) { _error.Text = message; _error.IsVisible = true; }
    public BenchmarkConfig ReadConfig()
    {
        var culture = CultureInfo.InvariantCulture;
        string F(string key) => _fields[key].Text?.Trim() ?? "";
        int Integer(string key) => int.TryParse(F(key), out var n) ? n : throw new ArgumentException("Проверьте числовые параметры нагрузки.");
        double Decimal(string key) => double.TryParse(F(key).Replace(',', '.'), NumberStyles.Float, culture, out var n) ? n : throw new ArgumentException("Проверьте время и лимиты ресурсов.");
        if (!ulong.TryParse(F("seed"), out var seed)) throw new ArgumentException("Начальное значение генератора должно быть целым неотрицательным числом.");
        var plan = _load.Read();
        var operations = _scenario.SelectedIndex switch { <= 0 => [], 1 => Enum.GetValues<Scenario>().ToList(), _ => new List<Scenario> { Enum.GetValues<Scenario>()[_scenario.SelectedIndex - 2] } };
        var config = new BenchmarkConfig {
            Targets = _selected.Order().ToList(), DatasetRows = F("rows").Split(',').Select(x => long.TryParse(x.Trim(), out var n) ? n : throw new ArgumentException("Объёмы: целые числа через запятую.")).ToList(),
            Clients = [plan.PeakClients], Plan = plan, DurationSeconds = plan.Stages[0].Seconds,
            Repeats = Integer("repeats"), PayloadBytes = Integer("payload"), BatchSize = Integer("batch"),
            WarmupSeconds = Decimal("warmup"), Seed = seed, CpuLimit = Decimal("cpu"), MemoryMb = Integer("memory"),
            Hotspot = _hotspot.IsChecked == true, Scenarios = operations };
        config.Validate(); return config;
    }
    public void SetConfig(BenchmarkConfig config)
    {
        _selected.Clear(); foreach (var target in config.Targets) _selected.Add(target);
        foreach (var target in _databases.Keys) PaintDatabase(target);
        var values = new Dictionary<string, string> { ["rows"] = string.Join(", ", config.DatasetRows),
            ["repeats"] = config.Repeats.ToString(), ["payload"] = config.PayloadBytes.ToString(),
            ["batch"] = config.BatchSize.ToString(), ["warmup"] = config.WarmupSeconds.ToString(CultureInfo.InvariantCulture), ["seed"] = config.Seed.ToString(),
            ["cpu"] = config.CpuLimit.ToString(CultureInfo.InvariantCulture), ["memory"] = config.MemoryMb.ToString() };
        foreach (var (key, value) in values) _fields[key].Text = value;
        _load.Apply(config);
        _hotspot.IsChecked = config.Hotspot;
        _scenario.SelectedIndex = config.Scenarios.Count == 1 ? Array.IndexOf(Enum.GetValues<Scenario>(), config.Scenarios[0]) + 2 : config.Plan is null || config.Scenarios.Count > 1 ? 1 : 0;
        GoTo(_step);
    }
    public void SetBusy(bool busy) { IsEnabled = !busy; }
}
