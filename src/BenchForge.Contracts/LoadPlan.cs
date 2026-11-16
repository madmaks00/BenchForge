using System.Text.Json.Serialization;

namespace BenchForge.Contracts;

public enum LoadProfile { Smoke, Steady, Ramp, Spike, Soak, FixedRate }

public sealed record LoadStage(string Name, int Clients, double Seconds, int? OperationsPerSecond = null);

public sealed record LoadPlan(LoadProfile Profile, List<LoadStage> Stages)
{
    [JsonIgnore] public int PeakClients => Stages.Max(stage => stage.Clients);
    [JsonIgnore] public double Seconds => Stages.Sum(stage => stage.Seconds);

    public void Validate()
    {
        if (!Enum.IsDefined(Profile) || Stages is null || Stages.Count is < 1 or > 24)
            throw new ArgumentException("План должен содержать от 1 до 24 этапов.");
        foreach (var stage in Stages)
        {
            if (stage is null || string.IsNullOrWhiteSpace(stage.Name) || stage.Name.Length > 80 || stage.Clients is < 1 or > 2048 ||
                !double.IsFinite(stage.Seconds) || stage.Seconds is < .05 or > 3600 || stage.OperationsPerSecond is < 1 or > 10_000_000)
                throw new ArgumentException("Проверьте этапы: 1–2048 клиентов, 0,05–3600 секунд, до 10 млн операций/с.");
            if (stage.OperationsPerSecond is { } rate && stage.Seconds * rate < 1)
                throw new ArgumentException("Увеличьте время или частоту, чтобы в этапе была хотя бы одна операция.");
        }
    }

    public static LoadPlan Single(LoadProfile profile, int clients, double seconds, int? rate = null) =>
        new(profile, [new(Name(profile), clients, seconds, rate)]);

    public static LoadPlan Ramp(int start, int peak, int steps, double secondsPerStep)
    {
        if (start < 1 || peak > 2048 || steps is < 2 or > 12 || peak - (long)start < steps - 1)
            throw new ArgumentException("Для роста задайте от 2 до 12 ступеней и достаточно высокий пик, чтобы нагрузка росла на каждой ступени.");
        var stages = Enumerable.Range(0, steps).Select(index => new LoadStage($"Ступень {index + 1}",
            start + (int)Math.Round((peak - start) * index / (double)(steps - 1)), secondsPerStep)).ToList();
        return new(LoadProfile.Ramp, stages);
    }

    public static LoadPlan Spike(int baseline, int peak, double before, double during, double recovery)
    {
        if (peak <= baseline) throw new ArgumentException("На пике должно быть больше клиентов, чем на обычной нагрузке.");
        return new(LoadProfile.Spike, [new("До пика", baseline, before), new("Пик", peak, during), new("Восстановление", baseline, recovery)]);
    }

    public static LoadPlan Soak(int clients, double totalSeconds, int windows)
    {
        if (windows is < 2 or > 24) throw new ArgumentException("Для длительного теста задайте от 2 до 24 интервалов.");
        return new(LoadProfile.Soak, Enumerable.Range(1, windows).Select(index => new LoadStage($"Интервал {index}", clients, totalSeconds / windows)).ToList());
    }

    public static string Name(LoadProfile profile) => profile switch
    {
        LoadProfile.Smoke => "Быстрая проверка",
        LoadProfile.Steady => "Постоянная нагрузка",
        LoadProfile.Ramp => "Рост нагрузки",
        LoadProfile.Spike => "Пиковая нагрузка",
        LoadProfile.Soak => "Длительный тест",
        LoadProfile.FixedRate => "Заданная частота",
        _ => throw new ArgumentOutOfRangeException(nameof(profile))
    };
}

// Measured results store a snapshot of the applied stage, including its order.
public sealed record LoadStageResult(int Number, string Name, double TargetSeconds, int? OperationsPerSecond);
