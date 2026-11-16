using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using BenchForge.Contracts;
using BenchForge.Coordinator;

namespace BenchForge.Desktop;

public sealed class RunView : UserControl
{
    private readonly ActivityIndicator _activity = new();
    private readonly TextBlock _title = Ui.Label("Эксперимент ещё не запущен", 23, Ui.Text, true);
    private readonly TextBlock _subtitle = Ui.Label("Создайте тест в разделе «Новый эксперимент».");
    private readonly TextBlock _count = Ui.Label("0 / 0", 27, Ui.Text, true);
    private readonly TextBlock _elapsed = Ui.Label("00:00", 27, Ui.Text, true);
    private readonly TextBlock _errors = Ui.Label("0", 27, Ui.Text, true);
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Height = 5, Foreground = Ui.B(Ui.Accent), Background = Ui.B(Ui.Line),
        Transitions = new Transitions { new DoubleTransition { Property = RangeBase.ValueProperty, Duration = TimeSpan.FromMilliseconds(260) } } };
    private readonly TextBox _journal = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        FontFamily = new FontFamily("Cascadia Code, Consolas, monospace"), FontSize = 12, Height = 190 };
    private readonly StackPanel _recent = Ui.Stack(0);
    private readonly Button _stop = Ui.Button("Остановить", "danger");
    private readonly Button _result = Ui.Button("К результатам →", "primary");
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTimeOffset _started;
    private long _errorCount;
    private bool _stopping;
    private readonly Queue<PhaseResult> _latest = [];
    public event Action? StopRequested;
    public event Action? ResultsRequested;
    public RunView()
    {
        var content = Ui.Stack(24);
        content.Children.Add(Ui.Heading("Выполнение", "Текущий эксперимент и его состояние."));
        var state = new Grid { ColumnDefinitions = new ColumnDefinitions("48,20,*") };
        state.Children.Add(_activity); var text = Ui.Stack(8, _title, _subtitle); Grid.SetColumn(text, 2); state.Children.Add(text);
        var stats = Ui.Columns("*,24,*,24,*", Ui.Stack(6, Ui.Label("Завершено запусков", 12), _count), Ui.Stack(6, Ui.Label("Прошло времени", 12), _elapsed), Ui.Stack(6, Ui.Label("Ошибок запросов", 12), _errors));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _stop, _result } };
        content.Children.Add(Ui.Card(Ui.Stack(28, state, _progress, stats, buttons), 32));
        content.Children.Add(Ui.Stack(14, Ui.Label("Последние измерения", 16, Ui.Text, true), _recent));
        var journal = new Expander { Header = "Журнал запуска", Content = _journal }; _journal.Margin = new Thickness(0, 12, 0, 0); content.Children.Add(journal);
        _recent.Children.Add(Ui.Label("Результаты появятся после первого измерения.", 13));
        _stop.IsVisible = false; _result.IsVisible = false;
        _stop.Click += (_, _) => StopRequested?.Invoke(); _result.Click += (_, _) => ResultsRequested?.Invoke();
        _timer.Tick += (_, _) => UpdateElapsed(); Content = Ui.Scroll(content);
    }
    public void Begin(BenchmarkConfig config)
    {
        _title.Text = "Подготовка эксперимента"; _subtitle.Text = string.Join(" · ", config.Targets.Select(t => Catalog.Get(t).Name));
        _journal.Text = ""; _latest.Clear(); _recent.Children.Clear(); _errorCount = 0; _errors.Text = "0";
        _stopping = false; _errors.Foreground = Ui.B(Ui.Text);
        _count.Text = "0 / —"; _progress.Value = 0; _progress.IsIndeterminate = true;
        _stop.IsVisible = true; _stop.IsEnabled = true; _stop.Content = "Остановить"; _result.IsVisible = false;
        _started = DateTimeOffset.UtcNow; UpdateElapsed(); _timer.Start(); _activity.Running = true;
    }
    public void Status(string title, string? detail = null)
    { if (_stopping) return; _title.Text = title; if (detail is not null) _subtitle.Text = detail; }
    public void Cases(int completed, int total)
    {
        _count.Text = completed + " / " + total;
        _progress.IsIndeterminate = false; _progress.Maximum = Math.Max(total, 1); _progress.Value = completed;
    }
    public void Add(PhaseResult row)
    {
        _errorCount += row.Errors; _errors.Text = ReportAnalysis.Count(_errorCount); _errors.Foreground = Ui.B(_errorCount > 0 ? Ui.Danger : Ui.Text);
        _latest.Enqueue(row); while (_latest.Count > 3) _latest.Dequeue(); _recent.Children.Clear();
        foreach (var result in _latest.Reverse())
        {
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*,180,105"), Margin = new Thickness(0, 12) };
            line.Children.Add(Ui.Label(Catalog.Get(result.Target).Name, 14, Ui.Text));
            var phase = Ui.Label(ReportAnalysis.PhaseName(result.Phase) + (result.Stage is { } stage ? " · " + stage.Name : ""));
            Grid.SetColumn(phase, 1); line.Children.Add(phase);
            var rate = Ui.Label(ReportAnalysis.Number(ReportAnalysis.Rate(result), ReportAnalysis.RateUnit(result.Phase)), 14, Ui.Text); Grid.SetColumn(rate, 2); line.Children.Add(rate);
            var status = Ui.Label(result.Cancelled ? "Остановлен" : ReportAnalysis.Valid(result) ? "Проверен" : "С ошибкой", 12, ReportAnalysis.Valid(result) ? Ui.Green : Ui.Amber);
            Grid.SetColumn(status, 3); line.Children.Add(status); _recent.Children.Add(line); _recent.Children.Add(Ui.Rule());
        }
    }
    public void Log(string message)
    {
        var text = (_journal.Text ?? "") + message + Environment.NewLine;
        _journal.Text = text.Length > 250_000 ? text[^200_000..] : text; _journal.CaretIndex = _journal.Text.Length;
    }
    public void Stopping() { _stop.IsEnabled = false; _stop.Content = "Сохраняю…"; Status("Завершение эксперимента", "Операции завершаются, отчёт сохраняется."); _stopping = true; }
    public void Finish(string status, bool hasResults)
    {
        _timer.Stop(); UpdateElapsed(); _activity.Running = false; _progress.IsIndeterminate = false; _stop.IsVisible = false; _result.IsVisible = hasResults;
        _title.Text = status switch { "completed" => "Эксперимент завершён", "cancelled" => "Эксперимент остановлен", "partial" or "completed_with_errors" => "Завершено с замечаниями", _ => "Не удалось завершить эксперимент" };
        _subtitle.Text = hasResults ? "Отчёт сохранён. Можно перейти к сравнению." : "Подробности доступны в журнале запуска.";
        if (status == "completed") _progress.Value = _progress.Maximum;
    }
    private void UpdateElapsed() { var elapsed = DateTimeOffset.UtcNow - _started; _elapsed.Text = elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"mm\:ss"); }
}

public sealed class ActivityIndicator : Control
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private double _angle;
    private bool _running, _attached;
    public bool Running { get => _running; set { _running = value; UpdateTimer(); InvalidateVisual(); } }
    public ActivityIndicator()
    {
        Width = 48; Height = 48; VerticalAlignment = VerticalAlignment.Center;
        _timer.Tick += (_, _) => { _angle += .12; InvalidateVisual(); };
        AttachedToVisualTree += (_, _) => { _attached = true; UpdateTimer(); };
        DetachedFromVisualTree += (_, _) => { _attached = false; UpdateTimer(); };
    }
    private void UpdateTimer() { if (_attached && _running) _timer.Start(); else _timer.Stop(); }
    public override void Render(DrawingContext context)
    {
        base.Render(context); var center = new Point(24, 24);
        context.DrawEllipse(null, new Pen(Ui.B(Ui.Line), 2), center, 18, 18);
        if (_running)
            for (var i = 0; i < 3; i++)
            { var angle = _angle - i * .23; context.DrawEllipse(Ui.B(i == 0 ? Ui.Accent : i == 1 ? Ui.SelectedLine : Ui.Selected), null, new Point(24 + Math.Cos(angle) * 18, 24 + Math.Sin(angle) * 18), 3.2 - i * .3, 3.2 - i * .3); }
        else context.DrawEllipse(Ui.B(Ui.Green), null, center, 5, 5);
    }
}
