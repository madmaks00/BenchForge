using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using BenchForge.Coordinator;
using BenchForge.Contracts;

namespace BenchForge.Desktop;

public sealed class ScalingChart : Control
{
    private TrendSeries[] _series = [];
    private MetricDescription _description = ReportAnalysis.Describe(AnalysisMetric.Rate, "PointRead");
    private bool _byRows;
    private IReadOnlyList<LoadStage>? _stages;
    private readonly List<(Point Position, string Tip)> _hits = [];
    public ScalingChart()
    {
        Height = 245; MinWidth = 250; ClipToBounds = true;
        SizeChanged += (_, _) => UpdateHeight();
        PointerMoved += (_, e) =>
        {
            var point = e.GetPosition(this);
            var hit = _hits.OrderBy(x => Math.Pow(x.Position.X - point.X, 2) + Math.Pow(x.Position.Y - point.Y, 2)).FirstOrDefault();
            ToolTip.SetTip(this, hit.Tip is not null && Math.Abs(hit.Position.X - point.X) < 16 && Math.Abs(hit.Position.Y - point.Y) < 18 ? hit.Tip : null);
        };
        PointerExited += (_, _) => ToolTip.SetTip(this, null);
    }
    public void SetData(TrendSeries[] series, MetricDescription description, bool byRows)
    { _stages = null; _series = series; _description = description; _byRows = byRows; UpdateHeight(); InvalidateVisual(); }

    public void SetStages(TrendSeries[] series, MetricDescription description, IReadOnlyList<LoadStage> stages)
    { _stages = stages; _series = series; _description = description; UpdateHeight(); InvalidateVisual(); }
    private void UpdateHeight()
    {
        var columns = Math.Max(1, (int)((Bounds.Width - 24) / 150));
        Height = Math.Max(245, 180 + (int)Math.Ceiling((double)_series.Length / columns) * 22);
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context); _hits.Clear();
        var xs = _stages is not null ? Enumerable.Range(1, _stages.Count).Select(i => (double)i).ToArray()
            : _series.SelectMany(x => x.Points).Select(p => p.X).Distinct().Order().ToArray();
        if (_stages is not null && _series.Length == 0)
        {
            ChartPalette.Text(context, "Нет измерений для этого показателя", 16, 65, 16, Ui.Muted, Bounds.Width - 32);
            return;
        }
        if (xs.Length < 2)
        {
            ChartPalette.Text(context, "Нужны хотя бы два уровня", 16, 65, 18, width: Bounds.Width - 32);
            ChartPalette.Text(context, _byRows ? "Задайте несколько объёмов, например 10 000 и 100 000 записей."
                : "Для роста используйте профиль «Рост нагрузки».", 16, 105, 13, Ui.Muted, Bounds.Width - 32);
            return;
        }
        var legendColumns = Math.Max(1, (int)((Bounds.Width - 24) / 150));
        var legendRows = (int)Math.Ceiling((double)_series.Length / legendColumns);
        var left = 65d; var right = Math.Max(left + 100, Bounds.Width - 24); var top = 25d; var bottom = Height - 50 - legendRows * 22;
        var maximum = _series.SelectMany(s => s.Points).Max(p => p.Maximum); if (maximum <= 0) maximum = 1;
        for (var i = 0; i <= 4; i++)
        {
            var y = bottom - (bottom - top) * i / 4;
            context.DrawLine(new Pen(Ui.B(Ui.Line), 1), new Point(left, y), new Point(right, y));
            ChartPalette.Text(context, ReportAnalysis.Number(maximum * i / 4, compact: true), 0, y - 7, 10, Ui.Muted, 61);
        }
        double X(double value) => left + Array.IndexOf(xs, value) * (right - left) / (xs.Length - 1);
        double Y(double value) => bottom - value / maximum * (bottom - top);
        var labelStride = Math.Max(1, (int)Math.Ceiling(xs.Length / Math.Max(1, (right - left) / 36)));
        for (var i = 0; i < xs.Length; i++)
        {
            if (i % labelStride != 0 && !(i == xs.Length - 1 && i % labelStride >= labelStride / 2d)) continue;
            var x = xs[i];
            ChartPalette.Text(context, _stages is null && _byRows ? ReportAnalysis.Number(x, compact: true) : ReportAnalysis.Count((long)x), X(x) - 8, bottom + 10, 10, Ui.Muted, 75);
        }
        foreach (var series in _series)
        {
            var color = ChartPalette.For(series.Target); TrendPoint? previous = null;
            foreach (var point in series.Points)
            {
                var position = new Point(X(point.X), Y(point.Median));
                if (previous is not null && Array.IndexOf(xs, point.X) - Array.IndexOf(xs, previous.X) == 1)
                    context.DrawLine(new Pen(color, 2.5), new Point(X(previous.X), Y(previous.Median)), position);
                if (point.Repeats > 1) context.DrawLine(new Pen(color, 1), new Point(position.X, Y(point.Minimum)), new Point(position.X, Y(point.Maximum)));
                context.DrawEllipse(color, new Pen(Ui.B(Ui.Surface), 1), position, 4.5, 4.5);
                var condition = _stages is null ? (_byRows ? "Записей: " : "Клиентов: ") + ReportAnalysis.Count((long)point.X)
                    : $"{_stages[(int)point.X - 1].Name} · клиентов: {_stages[(int)point.X - 1].Clients}";
                _hits.Add((position, series.Name + "\n" + condition
                    + "\n" + ReportAnalysis.Number(point.Median, _description.Unit) + "\nПовторов: " + point.Repeats));
                previous = point;
            }
        }
        for (var index = 0; index < _series.Length; index++)
        {
            var series = _series[index]; var legendX = 12 + index % legendColumns * 150;
            var legendY = bottom + 43 + index / legendColumns * 22;
            context.DrawEllipse(ChartPalette.For(series.Target), null, new Point(legendX + 4, legendY), 4, 4);
            ChartPalette.Text(context, series.Name, legendX + 14, legendY - 9, 11, width: 130);
        }
        ChartPalette.Text(context, _description.Unit, left, 0, 10, Ui.Muted);
    }
}
