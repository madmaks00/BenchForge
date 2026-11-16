namespace BenchForge.Contracts;

public enum LoadCurve { Hold, Linear, EaseIn, EaseOut, Smooth }

public sealed record LoadPoint(double TimeSeconds, int Clients, int? OperationsPerSecond = null,
    LoadCurve Curve = LoadCurve.Hold, int Steps = 4);

public sealed record LoadGraph(IReadOnlyList<LoadPoint> Points, double FinalSeconds)
{
    public const int MaxTimeSeconds = 24 * 3600;

    public static double Progress(LoadCurve curve, double fraction) => curve switch
    {
        LoadCurve.Hold => 0,
        LoadCurve.Linear => fraction,
        LoadCurve.EaseIn => fraction * fraction,
        LoadCurve.EaseOut => fraction * (2 - fraction),
        LoadCurve.Smooth => fraction * fraction * (3 - 2 * fraction),
        _ => throw new ArgumentOutOfRangeException(nameof(curve))
    };

    internal List<LoadStage> CreateStages()
    {
        if (Points is null || Points.Count < 1 || Points[0]?.TimeSeconds != 0)
            throw new ArgumentException("Добавьте начальную точку графика на 0 с.");
        var previousTime = -1d;
        foreach (var point in Points)
        {
            if (point is null || !double.IsFinite(point.TimeSeconds) || point.TimeSeconds <= previousTime || point.TimeSeconds > MaxTimeSeconds ||
                point.Clients is < 1 or > 2048 || point.OperationsPerSecond is < 1 or > 10_000_000 ||
                !Enum.IsDefined(point.Curve) || point.Steps < 2)
                throw new ArgumentException("Проверьте время, число клиентов, частоту и число ступеней в точках графика.");
            previousTime = point.TimeSeconds;
        }

        var stages = new List<LoadStage>();
        for (var index = 0; index < Points.Count - 1; index++)
        {
            var point = Points[index];
            var next = Points[index + 1];
            var count = point.Curve == LoadCurve.Hold ? 1 : point.Steps;
            var seconds = Math.Round((next.TimeSeconds - point.TimeSeconds) / count, 9);
            if (seconds < LoadPlan.MinStageSeconds)
                throw new ArgumentException($"Между точками {index + 1} и {index + 2} слишком короткие интервалы. Уменьшите число ступеней или увеличьте время перехода, чтобы на интервал приходилось хотя бы 0,05 с.");
            for (var step = 0; step < count; step++)
            {
                var fraction = Progress(point.Curve, (double)step / count);
                var clients = Interpolate(point.Clients, next.Clients, fraction);
                // Enter or leave fixed-rate mode at a point boundary.
                var rate = point.OperationsPerSecond is { } from && next.OperationsPerSecond is { } to
                    ? Interpolate(from, to, fraction) : point.OperationsPerSecond;
                var name = count == 1 ? $"Отрезок {index + 1}" : $"Отрезок {index + 1} · {step + 1}/{count}";
                stages.Add(new(name, clients, seconds, rate));
            }
        }

        var last = Points[^1];
        stages.Add(new("Удержание в конце", last.Clients, FinalSeconds, last.OperationsPerSecond));
        return stages;
    }

    private static int Interpolate(int from, int to, double fraction) =>
        (int)Math.Round(from + (to - from) * fraction, MidpointRounding.AwayFromZero);
}
