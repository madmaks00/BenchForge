using System.Globalization;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using BenchForge.Contracts;
using BenchForge.Coordinator;

namespace BenchForge.Desktop;

public static class ChartPalette
{
    public static readonly string[] Colors = ["#f78379", "#81c3eb", "#ddb079", "#86c89d", "#e48dc0", "#79ceca", "#e4cb76", "#b5b6c0"];
    public static IBrush For(TargetKind target) => Brush.Parse(Colors[(int)target % Colors.Length]);
    public static void Text(DrawingContext context, string text, double x, double y, double size = 13, string color = Ui.Text, double width = double.PositiveInfinity)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, size, Brush.Parse(color));
        if (double.IsFinite(width) && width > 0) formatted.MaxTextWidth = width;
        context.DrawText(formatted, new Point(x, y));
    }
}

public sealed class ComparisonChart : Control
{
    private const int FirstRowTop = 44;
    private const int RowHeight = 60;
    public static readonly StyledProperty<double> RevealProperty = AvaloniaProperty.Register<ComparisonChart, double>(nameof(Reveal), 0);
    public double Reveal { get => GetValue(RevealProperty); set => SetValue(RevealProperty, value); }
    static ComparisonChart() => AffectsRender<ComparisonChart>(RevealProperty);
    public SeriesAnalysis[] Series { get; private set; } = [];
    public MetricDescription Description { get; private set; } = ReportAnalysis.Describe(AnalysisMetric.Rate, nameof(Scenario.PointRead));
    public ComparisonChart()
    {
        MinWidth = 350; ClipToBounds = true; Height = 250;
        Transitions = new Transitions { new DoubleTransition { Property = RevealProperty, Duration = TimeSpan.FromMilliseconds(280), Easing = new CubicEaseOut() } };
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => Reveal = 1, DispatcherPriority.Background);
        PointerMoved += (_, e) =>
        {
            var row = (int)Math.Floor((e.GetPosition(this).Y - FirstRowTop) / RowHeight);
            if (row < 0 || row >= Series.Length) return;
            var item = Series[row];
            ToolTip.SetTip(this, item.Median is null ? item.Name + ": " + item.MissingReason
                : item.Name + ": " + ReportAnalysis.Number(item.Median, Description.Unit) + "\nИзмерений: " + item.Values.Length
                + "\nМинимум: " + ReportAnalysis.Number(item.Minimum, Description.Unit) + "\nМаксимум: " + ReportAnalysis.Number(item.Maximum, Description.Unit));
        };
        PointerExited += (_, _) => ToolTip.SetTip(this, null);
    }
    public void SetData(SeriesAnalysis[] series, MetricDescription description)
    {
        Series = series; Description = description; Height = Math.Max(190, FirstRowTop + series.Length * RowHeight + 8); InvalidateVisual();
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Bounds.Width;
        if (Series.Length == 0)
        { ChartPalette.Text(context, "Пока нет результатов", 12, 45, 17, Ui.Muted); return; }
        var left = Math.Min(145, width * .22); var plot = Math.Max(90, width - left - 165);
        var maximum = Series.Where(x => x.Maximum.HasValue).Select(x => x.Maximum!.Value).DefaultIfEmpty(1).Max();
        if (maximum <= 0) maximum = 1;
        var divisions = plot < 240 ? 2 : 4;
        for (var tick = 0; tick <= divisions; tick++)
        {
            var x = left + tick * plot / divisions;
            context.DrawLine(new Pen(Ui.B(Ui.Line), 1), new Point(x, 31), new Point(x, Height - 24));
            ChartPalette.Text(context, ReportAnalysis.Number(maximum * tick / divisions, compact: true), x - 9, 7, 10, Ui.Muted);
        }
        for (var index = 0; index < Series.Length; index++)
        {
            var s = Series[index]; var y = FirstRowTop + index * RowHeight;
            ChartPalette.Text(context, s.Name, 0, y + 2, 13, width: left - 12);
            if (s.Median is null)
            {
                ChartPalette.Text(context, "Нет данных", left, y + 1, 14, Ui.Muted);
                ChartPalette.Text(context, s.MissingReason, left, y + 26, 11, Ui.Muted, width - left - 12);
                continue;
            }
            var color = ChartPalette.For(s.Target); var valueWidth = s.Median.Value / maximum * plot;
            context.FillRectangle(Ui.B(Ui.Raised), new Rect(left, y + 1, plot, 18));
            if (s.Median > 0) context.DrawRectangle(color, null, new Rect(left, y + 1, valueWidth * Math.Clamp(Reveal, 0, 1), 18), 3, 3);
            else context.DrawEllipse(color, null, new Point(left, y + 12), 3, 3);
            ChartPalette.Text(context, ReportAnalysis.Number(s.Median, Description.Unit), left + plot + 12, y + 2, 13, width: 150);
            if (s.Values.Length > 1 && Reveal > .98)
            {
                var min = left + s.Minimum!.Value / maximum * plot; var max = left + s.Maximum!.Value / maximum * plot;
                var pen = new Pen(Ui.B(Ui.Text), 1.5);
                context.DrawLine(pen, new Point(min, y + 12), new Point(max, y + 12));
                context.DrawLine(pen, new Point(min, y + 6), new Point(min, y + 18));
                context.DrawLine(pen, new Point(max, y + 6), new Point(max, y + 18));
                foreach (var value in s.Values) context.DrawEllipse(color, new Pen(Ui.B(Ui.Background), 1), new Point(left + value / maximum * plot, y + 12), 3.2, 3.2);
            }
            var detail = s.Values.Length == 1 ? "1 повтор" : $"{s.Values.Length} повторов · минимум / максимум";
            if (s.Errors > 0) detail += $" · ошибок: {s.Errors}";
            ChartPalette.Text(context, detail, left, y + 32, 11, s.Errors > 0 ? Ui.Danger : Ui.Muted, width - left - 10);
        }
    }
}
