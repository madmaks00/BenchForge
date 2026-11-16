using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using BenchForge.Contracts;

namespace BenchForge.Desktop;

internal sealed class LoadGraphChart : Control
{
    private static readonly Pen TargetLine = new(Ui.B(Ui.Muted), 1.5, DashStyle.Dash);
    private static readonly Pen StageLine = new(Ui.B(Ui.Accent), 2);
    private static readonly Pen GridLine = new(Ui.B(Ui.Line));
    private static readonly IBrush MarkerFill = Ui.B(Ui.Surface);
    private static readonly IBrush Highlight = Ui.B(Ui.Accent);
    private LoadGraph _graph = new([], 0);
    private IReadOnlyList<LoadStage> _stages = [];
    private int _selected;
    private int _dragging;
    private Axes? _dragAxes;
    private Point _dragStart;
    private bool _moved;

    public event Action<int>? PointSelected;
    public event Action<double, int>? PointAdded;
    public event Action<int, double, int>? PointMoved;

    public LoadGraphChart()
    {
        Height = 190;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    public void Update(LoadGraph graph, IReadOnlyList<LoadStage> stages, int selected)
    {
        _graph = graph;
        _stages = stages;
        _selected = selected;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (_graph.Points.Count == 0 || Bounds.Width < 100) return;
        var axes = _dragAxes ?? GetAxes();

        for (var tick = 0; tick <= 4; tick++)
        {
            var t = tick / 4d;
            var x = axes.Plot.X + t * axes.Plot.Width;
            var y = axes.Plot.Bottom - t * axes.Plot.Height;
            context.DrawLine(GridLine, new Point(x, axes.Plot.Y), new Point(x, axes.Plot.Bottom));
            context.DrawLine(GridLine, new Point(axes.Plot.X, y), new Point(axes.Plot.Right, y));
            ChartPalette.Text(context, (axes.Seconds * t).ToString("0.##"), x - 8, axes.Plot.Bottom + 5, 11, Ui.Muted);
            ChartPalette.Text(context, (axes.Clients * t).ToString("0"), 1, y - 7, 11, Ui.Muted);
        }
        ChartPalette.Text(context, "Клиенты", 0, 0, 12, Ui.Muted);
        ChartPalette.Text(context, "с", axes.Plot.Right + 25, axes.Plot.Bottom + 5, 11, Ui.Muted);

        var points = _graph.Points;
        using (context.PushClip(axes.Plot.Inflate(7)))
        {
            for (var index = 0; index < points.Count - 1; index++)
            {
                var from = points[index]; var to = points[index + 1];
                var previous = axes.Position(from.TimeSeconds, from.Clients);
                for (var sample = 1; sample <= 24; sample++)
                {
                    var t = sample / 24d;
                    var p = axes.Position(from.TimeSeconds + (to.TimeSeconds - from.TimeSeconds) * t,
                        from.Clients + (to.Clients - from.Clients) * LoadGraph.Progress(from.Curve, t));
                    context.DrawLine(TargetLine, previous, p);
                    previous = p;
                }
                if (from.Curve == LoadCurve.Hold) context.DrawLine(TargetLine, previous, axes.Position(to.TimeSeconds, to.Clients));
            }
            var last = points[^1];
            context.DrawLine(TargetLine, axes.Position(last.TimeSeconds, last.Clients), axes.Position(last.TimeSeconds + _graph.FinalSeconds, last.Clients));

            var time = 0d;
            foreach (var (stage, index) in _stages.Select((stage, index) => (stage, index)))
            {
                if (index > 0) context.DrawLine(StageLine, axes.Position(time, _stages[index - 1].Clients), axes.Position(time, stage.Clients));
                context.DrawLine(StageLine, axes.Position(time, stage.Clients), axes.Position(time + stage.Seconds, stage.Clients));
                time += stage.Seconds;
            }
            for (var index = 0; index < points.Count; index++)
            {
                var point = points[index];
                var size = index == _selected ? 6d : 4.5;
                context.DrawEllipse(index == _selected ? Highlight : MarkerFill, StageLine, axes.Position(point.TimeSeconds, point.Clients), size, size);
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || _graph.Points.Count == 0) return;
        var axes = GetAxes();
        var position = e.GetPosition(this);
        for (var index = 0; index < _graph.Points.Count; index++)
        {
            var point = _graph.Points[index];
            var delta = position - axes.Position(point.TimeSeconds, point.Clients);
            if (delta.X * delta.X + delta.Y * delta.Y > 100) continue;
            _dragging = index;
            _dragAxes = axes;
            _dragStart = position;
            _moved = false;
            e.Pointer.Capture(this);
            PointSelected?.Invoke(index);
            e.Handled = true;
            return;
        }
        if (!axes.Plot.Contains(position)) return;
        var (time, clients) = axes.Values(position);
        PointAdded?.Invoke(time, clients);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragAxes is not { } axes) return;
        var position = e.GetPosition(this);
        var delta = position - _dragStart;
        if (!_moved && delta.X * delta.X + delta.Y * delta.Y < 9) return;
        _moved = true;
        var (time, clients) = axes.Values(position);
        PointMoved?.Invoke(_dragging, time, clients);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragAxes is not null) e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragAxes = null;
        InvalidateVisual();
    }

    private Axes GetAxes()
    {
        var finish = _graph.Points[^1].TimeSeconds + _graph.FinalSeconds;
        var seconds = finish < 4 ? Math.Max(1, finish) : Math.Ceiling(finish / 4) * 4;
        var clients = Math.Max(8, Math.Ceiling(_graph.Points.Max(p => p.Clients) * 1.125 / 4) * 4);
        return new(new Rect(43, 27, Math.Max(1, Bounds.Width - 86), Bounds.Height - 55), seconds, clients);
    }

    private readonly record struct Axes(Rect Plot, double Seconds, double Clients)
    {
        public Point Position(double time, double clients) =>
            new(Plot.X + Plot.Width * time / Seconds, Plot.Bottom - Plot.Height * clients / Clients);

        public (double Time, int Clients) Values(Point position) => (
            Math.Round(Math.Clamp((position.X - Plot.X) / Plot.Width, 0, 1) * Seconds, 2),
            (int)Math.Clamp(Math.Round(Math.Clamp((Plot.Bottom - position.Y) / Plot.Height, 0, 1) * Clients), 1, int.MaxValue));
    }
}
