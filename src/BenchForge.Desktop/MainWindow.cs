using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using BenchForge.Contracts;
using BenchForge.Coordinator;

namespace BenchForge.Desktop;

public sealed class MainWindow : Window
{
    private readonly TransitioningContentControl _presenter = Ui.Presenter();
    private readonly ExperimentSetupView _setup = new();
    private readonly RunView _run = new();
    private readonly ReportDashboard _dashboard = new();
    private readonly ReportLibraryView _library;
    private readonly ContentControl _reportHost = new();
    private readonly TextBlock _notice = Ui.Label("", 13, Ui.Amber);
    private readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly Button[] _nav;
    private readonly string _reportsRoot;
    private BenchmarkReport? _lastRun;
    private CancellationTokenSource? _cancellation;
    private Task? _active;
    private string? _runFolder, _reportFolder;
    private bool _running, _closing;
    private int _selected;

    public MainWindow()
    {
        Title = "BenchForge"; Width = 1120; Height = 760; MinWidth = 1024; MinHeight = 720;
        Icon = BrandIcons.WindowIcon();
        const double headerHeight = 44;
        var combineTitleBar = OperatingSystem.IsWindows();
        if (combineTitleBar)
        {
            WindowDecorations = WindowDecorations.Full;
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaTitleBarHeightHint = headerHeight;
            // Hide the Fluent caption text while keeping the actual window title for the shell.
            Styles.Add(new Style(selector => selector.OfType<WindowDrawnDecorations>().Template()
                .OfType<Panel>().Name("PART_TitleTextPanel").Child().OfType<TextBlock>())
            { Setters = { new Setter(IsVisibleProperty, false) } });
            StyleCaptionButtons();
        }
        Background = Ui.B(Ui.Background); Foreground = Ui.B(Ui.Text); FontFamily = new FontFamily("Segoe UI, Inter, sans-serif"); FontSize = 14;
        _reportsRoot = Environment.GetEnvironmentVariable("DBLAB_REPORTS") ?? Path.Combine(ProjectRoot(), "reports");
        _library = new ReportLibraryView(_reportsRoot); _reportHost.Content = _library;
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,1,*,Auto") };
        var header = new Grid { Height = headerHeight, Background = Brushes.Transparent };
        if (combineTitleBar)
        {
            root.Bind(MarginProperty, this.GetObservable(OffScreenMarginProperty));
            WindowDecorationProperties.SetElementRole(header, WindowDecorationsElementRole.TitleBar);
        }
        _nav = new[] { "Новый эксперимент", "Выполнение", "Отчёты" }.Select(x => Ui.Button(x, "nav")).ToArray();
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        for (var i = 0; i < _nav.Length; i++)
        {
            var index = i;
            var tab = _nav[i];
            tab.MinHeight = 28;
            tab.Padding = new Thickness(13, 4);
            tab.FontSize = 13;
            tab.Click += async (_, _) =>
            {
                Navigate(index);
                if (index == 2 && _reportHost.Content == _library) await _library.RefreshAsync();
            };
            navigation.Children.Add(tab);
        }
        var capsule = new Border { Background = Ui.B(Ui.Surface), BorderBrush = Ui.B(Ui.Line), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = navigation };
        header.Children.Add(capsule);
        var help = Ui.Button("?", "normal", ShowHelp);
        help.Width = 29; help.MinHeight = 28; help.Padding = new Thickness(0); help.FontSize = 13;
        help.HorizontalAlignment = HorizontalAlignment.Right; help.VerticalAlignment = VerticalAlignment.Center;
        // The Fluent title bar has four 45-DIP buttons with 2-DIP spacing.
        help.Margin = new Thickness(0, 0, combineTitleBar ? 198 : 16, 0);
        ToolTip.SetTip(help, "Как работать с BenchForge"); Avalonia.Automation.AutomationProperties.SetName(help, "Справка");
        WindowDecorationProperties.SetElementRole(capsule, WindowDecorationsElementRole.User);
        WindowDecorationProperties.SetElementRole(help, WindowDecorationsElementRole.User);
        header.Children.Add(help); root.Children.Add(header);
        var rule = Ui.Rule(); Grid.SetRow(rule, 1); root.Children.Add(rule);
        Grid.SetRow(_presenter, 2); root.Children.Add(_presenter);
        _notice.Margin = new Thickness(40, 0, 40, 14); _notice.IsVisible = false; Grid.SetRow(_notice, 3); root.Children.Add(_notice);
        Content = root;
        _setup.StartRequested += async config => await StartAsync(config); _setup.SaveRequested += async config => await SaveConfigAsync(config);
        _run.StopRequested += Stop;
        _run.ResultsRequested += () => { if (_lastRun is { } report) ShowReport(report, _runFolder); };
        _dashboard.LibraryRequested += async () => { _reportHost.Content = _library; Navigate(2); await _library.RefreshAsync(); };
        _dashboard.FolderRequested += OpenFolder;
        _library.OpenRequested += async path => { try { await OpenSavedReportAsync(path); } catch (Exception e) { Notice(e.Message); } };
        _library.ImportRequested += async () => await ImportReportAsync();
        _noticeTimer.Tick += (_, _) => { _noticeTimer.Stop(); _notice.IsVisible = false; };
        Closing += async (_, e) =>
        {
            if (!_running) return;
            e.Cancel = true;
            if (_closing) return;
            _closing = true; Stop();
            try { if (_active is not null) await _active; } catch { } finally { _running = false; Close(); }
        };
        Navigate(0);
    }

    private void StyleCaptionButtons()
    {
        var idle = new ImmutableSolidColorBrush(Color.Parse(Ui.Hover), 0);
        foreach (var name in new[] { "PART_MinimizeButton", "PART_MaximizeButton" })
        {
            Selector CaptionButton(Selector? selector) => selector.OfType<WindowDrawnDecorations>().Template().OfType<Button>().Name(name);
            Styles.Add(new Style(CaptionButton)
            {
                Setters = { new Setter(BackgroundProperty, Ui.B(Ui.Hover)), new Setter(BorderBrushProperty, Ui.B(Ui.Raised)) }
            });
            Styles.Add(new Style(selector => CaptionButton(selector).Template().OfType<ContentPresenter>())
            {
                Setters =
                {
                    new Setter(ContentPresenter.CornerRadiusProperty, new CornerRadius(6)),
                    new Setter(TransitionsProperty, new Transitions
                    {
                        new BrushTransition { Property = ContentPresenter.BackgroundProperty, Duration = TimeSpan.FromMilliseconds(100), Easing = new CubicEaseOut() }
                    })
                }
            });
            Styles.Add(new Style(selector => CaptionButton(selector).Not(s => s.Class(":pointerover")).Not(s => s.Class(":pressed"))
                .Template().OfType<ContentPresenter>())
            {
                Setters = { new Setter(ContentPresenter.BackgroundProperty, idle) }
            });
        }
    }

    private void Navigate(int page)
    {
        _selected = Math.Clamp(page, 0, 2); _notice.IsVisible = false;
        for (var i = 0; i < _nav.Length; i++) Ui.Select(_nav[i], i == _selected);
        var reportOpen = _selected == 2 && _reportHost.Content == _dashboard;
        _presenter.MaxWidth = reportOpen ? double.PositiveInfinity : 1140;
        _presenter.Margin = reportOpen ? new Thickness(24, 16, 24, 20) : new Thickness(40, 22, 40, 24);
        _presenter.Content = _selected switch { 1 => _run, 2 => _reportHost, _ => _setup };
    }
    private void ShowReport(BenchmarkReport report, string? folder)
    {
        _reportFolder = folder;
        _dashboard.SetReport(report);
        _reportHost.Content = _dashboard;
        Navigate(2);
    }
    private void Notice(string message) { _notice.Text = message; _notice.IsVisible = true; _noticeTimer.Stop(); _noticeTimer.Start(); }
    private async Task StartAsync(BenchmarkConfig config)
    {
        if (_running) return;
        _cancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation; _running = true; _lastRun = null; _runFolder = null;
        _setup.SetBusy(true); _nav[0].IsEnabled = false; _run.Begin(config); _nav[1].Content = "Выполнение  ·"; Navigate(1);
        var runner = new ExperimentRunner(update => Dispatcher.UIThread.Post(() => { if (_cancellation == cancellation && _running) Receive(update); }));
        var task = Task.Run(async () => (await runner.RunAsync(config, _reportsRoot, cancellation.Token)).Report);
        _active = task;
        var status = "failed";
        try { _lastRun = await task; status = _lastRun.Status; }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { status = "cancelled"; }
        catch (Exception e) { _run.Log(e.Message); }
        finally
        {
            _running = false; _setup.SetBusy(false); _nav[0].IsEnabled = true; _nav[1].Content = "Выполнение";
            _run.Finish(status, _lastRun is not null);
            if (_selected == 2 && _reportHost.Content == _library) _ = _library.RefreshAsync();
        }
    }
    private void Stop() { if (!_running) return; _cancellation?.Cancel(); _run.Stopping(); }
    private void Receive(RunUpdate update)
    {
        switch (update.Type)
        {
            case "log":
                _run.Log(update.Message);
                var operation = update.Message.Split(" · ").LastOrDefault();
                if (operation is not null && ReportAnalysis.KnownPhases.Contains(operation))
                    _run.Status(ReportAnalysis.PhaseName(operation), update.Message[..^(operation.Length + 3)]);
                else if (update.Message.Contains(" · повтор ")) _run.Status("Подготовка запуска", update.Message);
                break;
            case "started": _runFolder = update.Folder; _run.Cases(0, update.TotalCases); break;
            case "result": _run.Add(update.Result!); break;
            case "case_finished": _run.Cases(update.CompletedCases, update.TotalCases); break;
            case "progress":
                using (var json = JsonDocument.Parse(update.Message))
                {
                    var data = json.RootElement;
                    if (data.TryGetProperty("loaded", out var loaded)) _run.Status("Подготовка данных", $"{loaded.GetInt64():N0} / {data.GetProperty("total").GetInt64():N0} записей");
                    else
                    {
                        var operationName = ReportAnalysis.PhaseName(data.GetProperty("phase").GetString()!);
                        var detail = $"{data.GetProperty("elapsedSeconds").GetDouble():N1} с · успешных операций: {data.GetProperty("successful").GetInt64():N0}";
                        if (data.TryGetProperty("stage", out var stage) && stage.ValueKind == JsonValueKind.String)
                            detail = $"{stage.GetString()} · клиентов: {data.GetProperty("clients").GetInt32()} · {detail}";
                        _run.Status(operationName, detail);
                    }
                }
                break;
            case "finished": _runFolder = update.Folder; _run.Cases(update.CompletedCases, update.TotalCases); break;
        }
    }
    public async Task<BenchmarkReport> OpenSavedReportAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var report = await ReadReportAsync(stream);
        ShowReport(report, Path.GetDirectoryName(path));
        return report;
    }
    private static async Task<BenchmarkReport> ReadReportAsync(Stream stream)
    {
        var report = await JsonSerializer.DeserializeAsync<BenchmarkReport>(stream, Json.Options) ?? throw new InvalidDataException("Пустой отчёт.");
        if (report.SchemaVersion != 1) throw new InvalidDataException("Этот формат отчёта не поддерживается.");
        return report;
    }
    private async Task ImportReportAsync()
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Открыть отчёт BenchForge", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Отчёт JSON") { Patterns = ["*.json"] }] });
            if (files.Count == 0) return;
            await using var stream = await files[0].OpenReadAsync();
            var report = await ReadReportAsync(stream);
            ShowReport(report, files[0].TryGetLocalPath() is { } path ? Path.GetDirectoryName(path) : null);
        }
        catch (Exception e) { Notice("Не удалось открыть отчёт: " + e.Message); }
    }
    private async Task SaveConfigAsync(BenchmarkConfig config)
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Сохранить настройки", SuggestedFileName = "experiment.json", DefaultExtension = "json" });
            if (file is null) return; await using var stream = await file.OpenWriteAsync(); stream.SetLength(0); await JsonSerializer.SerializeAsync(stream, config, Json.Pretty);
            Notice("Настройки сохранены.");
        }
        catch (Exception e) { Notice(e.Message); }
    }
    private void OpenFolder()
    { try { var path = _reportFolder ?? _reportsRoot; Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception e) { Notice(e.Message); } }
    private void ShowHelp()
    {
        var help = Ui.Stack(18, Ui.Label("Как работать с BenchForge", 24, Ui.Text, true), Ui.Label("Три шага от настройки до результата."),
            Ui.Label("1. Новый эксперимент", 17, Ui.Text, true), Ui.Label("Выберите базы. Задайте объём, число клиентов и время. Проверьте настройки и запустите тест."),
            Ui.Label("2. Выполнение", 17, Ui.Text, true), Ui.Label("Здесь видны прогресс, последние измерения и журнал. Остановка сохраняет частичный отчёт."),
            Ui.Label("3. Отчёты", 17, Ui.Text, true), Ui.Label("Сравнение — выбранная операция. Масштабирование — изменение нагрузки. Измерения — отдельные повторы."));
        foreach (var (title, text) in new[] {
            ("Скорость и задержка p99", "Скорость — успешные операции или записи за секунду. p99 — время, в которое уложились около 99% успешных запросов. Это не максимальное время ответа."),
            ("Повторы и предварительные результаты", "Один повтор не показывает разброс. Точки и отрезки на графике показывают измерения, минимум и максимум. Короткий тест даёт предварительное наблюдение; пороги интерфейса не рассчитывают статистическую достоверность."),
            ("Память, CPU и пропущенная нагрузка", "Ресурсы базы и генератора показаны отдельно. SQLite работает внутри генератора. Отсутствие выборки не означает ноль. 100% CPU соответствует одному логическому ядру. Ошибки и пропуски проверяйте вместе со скоростью."),
            ("Условия сравнения", "Результаты относятся к вашему железу, данным и настройкам. Redis/Valkey, SQL, MongoDB, SQLite и ClickHouse используют разные профили хранения. Скоростная сводка включает проверенные повторы без ошибок; все повторы остаются в измерениях.") })
            help.Children.Add(new Expander { Header = title, Content = Ui.Label(text, 13) });
        var window = new Window { Title = "Справка · BenchForge", Icon = Icon, Width = 610, Height = 650, MinWidth = 510, MinHeight = 440,
            Background = Ui.B(Ui.Background), Foreground = Ui.B(Ui.Text), WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = Ui.Scroll(new Border { Padding = new Thickness(30), Child = help }) };
        _ = window.ShowDialog(this);
    }
    private static string ProjectRoot()
    { for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent) if (File.Exists(Path.Combine(dir.FullName, "BenchForge.slnx"))) return dir.FullName; return AppContext.BaseDirectory; }
    public async Task VerifyUiAsync()
    {
        var config = new BenchmarkConfig { Targets = [TargetKind.Sqlite], DatasetRows = [100], Clients = [4], Repeats = 1, WarmupSeconds = 0, BatchSize = 20,
            Plan = LoadPlan.Spike(1, 4, .3, .3, .3) };
        _setup.SetConfig(config); _setup.GoTo(2); await StartAsync(_setup.ReadConfig());
        if (_lastRun?.Status != "completed" || !_lastRun.Results.Where(row => row.Stage is not null).Select(row => row.Clients).SequenceEqual([1, 4, 1]))
            throw new InvalidOperationException("UI spike experiment did not complete: " + _lastRun?.Status);
        ShowReport(_lastRun, _runFolder); await Task.Delay(350);
        if (_dashboard.DisplayedResultCount != 5) throw new InvalidOperationException("UI lost results");
        if (Environment.GetEnvironmentVariable("DBLAB_SCREENSHOT") is { } screenshot) Capture(screenshot);
    }
    public async Task VerifyCancelAsync()
    {
        var config = new BenchmarkConfig { Targets = [TargetKind.Sqlite], DatasetRows = [100], Clients = [4], Repeats = 1, WarmupSeconds = 0, BatchSize = 20,
            Plan = LoadPlan.Spike(1, 4, .3, 5, .3) };
        _setup.SetConfig(config); _setup.GoTo(2); var task = StartAsync(_setup.ReadConfig()); await Task.Delay(800);
        if (Environment.GetEnvironmentVariable("DBLAB_SCREENSHOT") is { } screenshot) Capture(screenshot);
        Stop(); await task;
        if (_lastRun?.Status != "cancelled" || _runFolder is null || !File.Exists(Path.Combine(_runFolder, "report.json")))
            throw new InvalidOperationException("UI cancellation failed: " + _lastRun?.Status);
    }
    public async Task VerifyDashboardAsync(string path)
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("DBLAB_PREVIEW_WIDTH"), out var width)) Width = Math.Max(MinWidth, width);
        var report = await OpenSavedReportAsync(path); _setup.SetConfig(report.Config);
        var screenshot = Environment.GetEnvironmentVariable("DBLAB_SCREENSHOT");
        async Task Snapshot(string name) { await Task.Delay(350); if (screenshot is not null) Capture(Path.Combine(Path.GetDirectoryName(screenshot)!, Path.GetFileNameWithoutExtension(screenshot) + "-" + name + ".png")); }
        for (var step = 0; step < 3; step++) { Navigate(0); _setup.GoTo(step); await Snapshot("setup-" + (step + 1)); }
        _reportHost.Content = _library; Navigate(2); await _library.RefreshAsync(); await Snapshot("library");
        ShowReport(report, Path.GetDirectoryName(path));
        for (var page = 0; page < 3; page++) { _dashboard.SelectedPage = page; await Snapshot(new[] { "compare", "scaling", "measurements" }[page]); }
        if (report.Config.EngineIds.Count > 1)
        {
            _dashboard.SelectEngineComparison(true);
            _dashboard.SelectGraphView(false); await Snapshot("engines");
            _dashboard.SelectGraphView(true); await Snapshot("engine-scaling");
            _dashboard.SelectEngineComparison(false);
        }
        foreach (var metric in new[] { AnalysisMetric.ServerMemory, AnalysisMetric.ErrorPercent })
        { _dashboard.SelectGraphView(false, metric); await Snapshot(metric == AnalysisMetric.ServerMemory ? "memory" : "errors"); }
        _dashboard.SelectGraphView(false); _setup.GoTo(0); ShowReport(report, Path.GetDirectoryName(path));
        if (_dashboard.DisplayedResultCount != report.Results.Count) throw new InvalidOperationException("Report rows were lost");
    }
    private void Capture(string path)
    { using var bitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96)); bitmap.Render(this); bitmap.Save(path, PngBitmapEncoderOptions.Default); }
}
