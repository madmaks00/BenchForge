using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using BenchForge.Contracts;

namespace BenchForge.Desktop;

internal sealed class LoadGraphEditor : UserControl
{
    private const decimal MinGap = .05m;
    private readonly List<LoadPoint> _points = [new(0, 4, Curve: LoadCurve.Smooth), new(12, 24), new(17, 4)];
    private readonly LoadGraphChart _chart = new();
    private readonly ComboBox _pointPicker = new();
    private readonly ComboBox _curve = new() { ItemsSource = new[] { "Удержание и скачок", "Линейная", "С разгоном", "С замедлением", "Плавный переход" } };
    private readonly NumericUpDown _time = Number(0, LoadGraph.MaxTimeSeconds, .5m);
    private readonly NumericUpDown _clients = Number(1, 2048, 1);
    private readonly NumericUpDown _rate = Number(0, 10_000_000, 1);
    private readonly NumericUpDown _steps = Number(2, int.MaxValue, 1);
    private readonly NumericUpDown _final = Number(.05m, 3600, .5m);
    private readonly Button _add;
    private readonly Button _remove;
    private readonly Control _curveField;
    private readonly Control _stepField;
    private readonly TextBlock _status = Ui.Label("", 12);
    private int _selected;
    private bool _updating;

    public LoadGraphEditor()
    {
        _final.Value = 10;
        _add = Ui.Button("+ Точка", action: () => Add(_points[^1].TimeSeconds + 5, _points[^1].Clients));
        _remove = Ui.Button("Удалить точку", "quiet", Remove);
        AutomationProperties.SetName(_pointPicker, "Точка графика");
        AutomationProperties.SetName(_curve, "Переход к следующей точке");
        _pointPicker.HorizontalAlignment = HorizontalAlignment.Stretch;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { _add, _remove } };
        var picker = Ui.Columns("*,12,Auto", _pointPicker, actions);
        _curveField = Ui.Field("Переход к следующей точке", _curve);
        _stepField = Field("Ступеней перехода", _steps, "Каждая ступень — интервал с постоянной нагрузкой. Больше ступеней — точнее кривая. На интервал должно приходиться хотя бы 0,05 с.");
        Content = Ui.Stack(10,
            Ui.Label("Нажмите на график, чтобы добавить точку. Перетащите маркер, чтобы изменить время и число клиентов.", 13),
            _chart, Ui.Label("Пунктир — заданная кривая. Коралловая линия — ступени, которые выполнит движок.", 12),
            picker,
            Ui.Columns("*,12,*,12,*", Field("Время точки, с", _time), Field("Клиентов", _clients),
                Field("Цель, оп./с (0 — без цели)", _rate, "Частота старта операций. 0 — клиенты начинают новую операцию сразу после завершения предыдущей.")),
            Ui.Columns("*,12,*,12,*", _curveField, _stepField, Field("Удержание в конце, с", _final, "Сколько времени выполнять нагрузку последней точки.")),
            _status);

        _chart.PointSelected += Select;
        _chart.PointAdded += Add;
        _chart.PointMoved += Move;
        _pointPicker.SelectionChanged += (_, _) => { if (!_updating && _pointPicker.SelectedIndex >= 0) Select(_pointPicker.SelectedIndex); };
        foreach (var input in new[] { _time, _clients, _rate, _steps, _final }) input.ValueChanged += (_, _) => UpdatePoint();
        _curve.SelectionChanged += (_, _) => UpdatePoint();
        Select(0);
    }

    public LoadPlan Read()
    {
        if (_time.Value is null || _clients.Value is null || _rate.Value is null || _steps.Value is null || _final.Value is null)
            throw new ArgumentException("Заполните числовые поля графика.");
        return LoadPlan.FromGraph(new([.. _points], (double)_final.Value.Value));
    }

    public void Apply(LoadGraph graph)
    {
        _updating = true;
        _points.Clear();
        _points.AddRange(graph.Points);
        _final.Value = (decimal)graph.FinalSeconds;
        Select(0);
    }

    private void Select(int index)
    {
        _updating = true;
        _selected = index;
        var point = _points[index];
        var (min, max) = TimeBounds(index);
        _time.Minimum = 0;
        _time.Maximum = LoadGraph.MaxTimeSeconds;
        _time.Value = (decimal)point.TimeSeconds;
        _time.Minimum = min;
        _time.Maximum = max;
        _time.IsEnabled = index != 0;
        _clients.Value = point.Clients;
        _rate.Value = point.OperationsPerSecond ?? 0;
        _steps.Value = point.Steps;
        _curve.SelectedIndex = (int)point.Curve;
        _updating = false;
        Refresh();
    }

    private void UpdatePoint()
    {
        if (_updating) return;
        if (_time.Value is { } time && _clients.Value is { } clients && _rate.Value is { } rate && _steps.Value is { } steps && _curve.SelectedIndex >= 0)
        {
            var (min, max) = TimeBounds(_selected);
            _points[_selected] = new((double)Math.Clamp(time, min, max), (int)clients, rate == 0 ? null : (int)rate,
                (LoadCurve)_curve.SelectedIndex, (int)steps);
        }
        Refresh();
    }

    private void Add(double time, int clients)
    {
        var index = _points.FindIndex(point => point.TimeSeconds > time);
        if (index < 0) index = _points.Count;
        var min = index == 0 ? MinGap : (decimal)_points[index - 1].TimeSeconds + MinGap;
        var max = index == _points.Count ? LoadGraph.MaxTimeSeconds : (decimal)_points[index].TimeSeconds - MinGap;
        if ((decimal)time < min || (decimal)time > max) { SetStatus("Добавьте точку чуть дальше от соседних точек.", true); return; }
        _points.Insert(index, new(time, clients, _points[index - 1].OperationsPerSecond));
        Select(index);
    }

    private void Move(int index, double time, int clients)
    {
        var (min, max) = TimeBounds(index);
        _points[index] = _points[index] with { TimeSeconds = (double)Math.Clamp((decimal)time, min, max), Clients = clients };
        Select(index);
    }

    private void Remove()
    {
        if (_selected == 0) return;
        _points.RemoveAt(_selected);
        Select(Math.Min(_selected, _points.Count - 1));
    }

    private (decimal Min, decimal Max) TimeBounds(int index)
    {
        if (index == 0) return (0, 0);
        var current = (decimal)_points[index].TimeSeconds;
        var min = Math.Min(current, (decimal)_points[index - 1].TimeSeconds + MinGap);
        var max = index == _points.Count - 1 ? LoadGraph.MaxTimeSeconds : Math.Max(current, (decimal)_points[index + 1].TimeSeconds - MinGap);
        return (min, max);
    }

    private void Refresh()
    {
        _updating = true;
        _pointPicker.ItemsSource = _points.Select(point => $"{point.TimeSeconds:0.##} с · {point.Clients} кл.").ToArray();
        _pointPicker.SelectedIndex = _selected;
        _updating = false;
        _remove.IsEnabled = _selected > 0;
        _curveField.IsVisible = _selected < _points.Count - 1;
        _stepField.IsVisible = _curveField.IsVisible && _points[_selected].Curve != LoadCurve.Hold;
        var graph = new LoadGraph([.. _points], (double)(_final.Value ?? 0));
        try
        {
            var plan = Read();
            _chart.Update(graph, plan.Stages, _selected);
            SetStatus($"{plan.Seconds:0.##} с на операцию · точек {_points.Count} · интервалов нагрузки {plan.Stages.Count}", false);
        }
        catch (ArgumentException error)
        {
            _chart.Update(graph, [], _selected);
            SetStatus(error.Message, true);
        }
    }

    private void SetStatus(string text, bool error)
    {
        _status.Text = text;
        _status.Foreground = Ui.B(error ? Ui.Danger : Ui.Muted);
    }

    private static NumericUpDown Number(decimal min, decimal max, decimal increment) => new()
    {
        Minimum = min, Maximum = max, Increment = increment, ClipValueToMinMax = true, MinHeight = 36,
        FormatString = increment < 1 ? "0.##" : "0", ParsingNumberStyle = increment < 1 ? NumberStyles.Number : NumberStyles.Integer,
        ShowButtonSpinner = false, Background = Ui.B(Ui.Input), BorderBrush = Ui.B(Ui.Line), CornerRadius = new CornerRadius(8)
    };

    private static Control Field(string name, NumericUpDown input, string? hint = null)
    {
        AutomationProperties.SetName(input, name);
        if (hint is not null) ToolTip.SetTip(input, hint);
        return Ui.Field(name, input, hint);
    }
}
