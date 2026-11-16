using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using BenchForge.Contracts;

namespace BenchForge.Desktop;

internal sealed class LoadPlanEditor : UserControl
{
    private readonly Dictionary<(LoadProfile Profile, string Key), TextBox> _inputs = [];
    private readonly Dictionary<LoadProfile, Button> _buttons = [];
    private readonly Dictionary<LoadProfile, Control> _editors = [];
    private readonly ContentControl _settings = new();
    private readonly LoadGraphEditor _graph = new();
    private readonly Grid _options = new() { ColumnDefinitions = new ColumnDefinitions("*,10,*,10,*"), RowDefinitions = new RowDefinitions("Auto,10,Auto") };
    private readonly TextBlock _heading = Ui.Label("Тип теста", 17, Ui.Text, true);
    private readonly Button _modeSwitch;
    private LoadProfile _profile;
    private LoadProfile _lastPreset;

    public LoadPlanEditor()
    {
        _modeSwitch = Ui.Button("Свой график", action: () => Choose(_profile == LoadProfile.Custom ? _lastPreset : LoadProfile.Custom));
        _modeSwitch.MinHeight = 32;
        _modeSwitch.Padding = new Thickness(12, 6);
        _heading.VerticalAlignment = VerticalAlignment.Center;
        var heading = Ui.Columns("*,12,Auto", _heading, _modeSwitch);
        var profiles = new (LoadProfile Kind, string Purpose)[]
        {
            (LoadProfile.Smoke, "Проверить, что всё запускается"),
            (LoadProfile.Steady, "Сравнить базы при обычной нагрузке"),
            (LoadProfile.Ramp, "Найти, где падает отдача"),
            (LoadProfile.Spike, "Проверить перегрузку и восстановление"),
            (LoadProfile.Soak, "Заметить ухудшение со временем"),
            (LoadProfile.FixedRate, "Проверить заданный поток запросов")
        };
        for (var index = 0; index < profiles.Length; index++)
        {
            var (profile, purpose) = profiles[index];
            var name = LoadPlan.Name(profile);
            var button = Ui.Button(name, action: () => Choose(profile));
            button.Content = Ui.Stack(4, Ui.Label(name, 14, Ui.Text, true), Ui.Label(purpose, 12));
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.Padding = new Thickness(13, 9);
            Grid.SetColumn(button, index % 3 * 2); Grid.SetRow(button, index / 3 * 2);
            _options.Children.Add(button); _buttons.Add(profile, button);
        }

        const string clientsHint = "Сколько клиентов параллельно выполняют операции. Каждый начинает следующую операцию после ответа.";
        const string secondsHint = "Время замера для каждой выбранной операции. Прогрев и подготовка набора выполняются отдельно.";
        _editors[LoadProfile.Smoke] = Ui.Columns("*,16,*", Field(LoadProfile.Smoke, "clients", "Клиентов", "1", clientsHint), Field(LoadProfile.Smoke, "seconds", "Секунд", "2", secondsHint));
        _editors[LoadProfile.Steady] = Ui.Columns("*,16,*", Field(LoadProfile.Steady, "clients", "Клиентов", "8", clientsHint), Field(LoadProfile.Steady, "seconds", "Секунд", "30", secondsHint));
        _editors[LoadProfile.Ramp] = Ui.Stack(12,
            Ui.Columns("*,16,*", Field(LoadProfile.Ramp, "clients", "Клиентов на старте", "2", "Нагрузка на первой ступени."), Field(LoadProfile.Ramp, "peak", "Клиентов на последней ступени", "32", "Нагрузка на последней ступени.")),
            Ui.Columns("*,16,*", Field(LoadProfile.Ramp, "steps", "Ступеней", "5", "Число уровней нагрузки, включая первый и последний. От 2 до 12."), Field(LoadProfile.Ramp, "seconds", "Секунд на ступень", "10", "Сколько секунд измерять каждый уровень нагрузки.")));
        _editors[LoadProfile.Spike] = Ui.Stack(12,
            Ui.Columns("*,16,*", Field(LoadProfile.Spike, "clients", "Обычная нагрузка, клиентов", "4", "Число клиентов до и после пика."), Field(LoadProfile.Spike, "peak", "Пик, клиентов", "32", "Число клиентов во время всплеска. Должно быть выше обычного.")),
            Ui.Columns("*,16,*,16,*", Field(LoadProfile.Spike, "before", "До пика, с", "10", "Время на обычной нагрузке до скачка."), Field(LoadProfile.Spike, "during", "Пик, с", "5", "Время на повышенной нагрузке."), Field(LoadProfile.Spike, "after", "Восстановление, с", "15", "Время замера после возврата к обычной нагрузке.")));
        _editors[LoadProfile.Soak] = Ui.Columns("*,16,*,16,*", Field(LoadProfile.Soak, "clients", "Клиентов", "8", clientsHint), Field(LoadProfile.Soak, "minutes", "Минут всего", "5", "Продолжительность замеров на одной операции."), Field(LoadProfile.Soak, "steps", "Интервалов измерения", "10", "Разбивает результат на 2–24 равных периода для сравнения во времени."));
        _editors[LoadProfile.FixedRate] = Ui.Columns("*,16,*,16,*", Field(LoadProfile.FixedRate, "rate", "Операций в секунду", "300", "Планируемое число стартов операций в секунду. Старты, которые не успели выполнить, учитываются как пропуски."), Field(LoadProfile.FixedRate, "clients", "Параллельных клиентов, максимум", "16", "Сколько операций могут выполняться одновременно, чтобы поддерживать заданный поток."), Field(LoadProfile.FixedRate, "seconds", "Секунд", "30", secondsHint));
        _editors[LoadProfile.Custom] = _graph;
        Content = Ui.Stack(14, heading, _options, Ui.Card(_settings, 15));
        Choose(LoadProfile.Smoke);
    }

    public LoadPlan Read()
    {
        if (_profile == LoadProfile.Custom) return _graph.Read();
        var clients = InputInt("clients", "Клиенты");
        var plan = _profile switch
        {
            LoadProfile.Ramp => LoadPlan.Ramp(clients, InputInt("peak", "Клиенты на последней ступени"), InputInt("steps", "Ступени"), InputNumber("seconds", "Время ступени")),
            LoadProfile.Spike => LoadPlan.Spike(clients, InputInt("peak", "Пик"), InputNumber("before", "Время до пика"), InputNumber("during", "Время пика"), InputNumber("after", "Восстановление")),
            LoadProfile.Soak => LoadPlan.Soak(clients, 60 * InputNumber("minutes", "Минуты"), InputInt("steps", "Интервалы измерения")),
            LoadProfile.FixedRate => LoadPlan.Single(_profile, clients, InputNumber("seconds", "Секунды"), InputInt("rate", "Операции в секунду")),
            _ => LoadPlan.Single(_profile, clients, InputNumber("seconds", "Секунды"))
        };
        plan.Validate();
        return plan;
    }

    public void Apply(BenchmarkConfig config)
    {
        if (config.Plan is not { Stages.Count: > 0 } plan)
        {
            Choose(config.LoadModel == LoadModel.ConstantRate ? LoadProfile.FixedRate : LoadProfile.Steady);
            Set("clients", config.Clients.FirstOrDefault(1)); Set("seconds", config.DurationSeconds); Set("rate", config.TargetOpsPerSecond);
            return;
        }

        Choose(plan.Profile);
        if (plan.Graph is { } graph)
        {
            _graph.Apply(graph);
            return;
        }
        var first = plan.Stages[0];
        Set("clients", first.Clients); Set("seconds", first.Seconds); Set("peak", plan.PeakClients); Set("steps", plan.Stages.Count);
        Set("minutes", plan.Seconds / 60); Set("rate", first.OperationsPerSecond ?? 300);
        if (plan.Profile == LoadProfile.Spike && plan.Stages.Count == 3)
        { Set("before", first.Seconds); Set("during", plan.Stages[1].Seconds); Set("after", plan.Stages[2].Seconds); }
    }

    private void Choose(LoadProfile profile)
    {
        _profile = profile;
        var custom = profile == LoadProfile.Custom;
        if (!custom) _lastPreset = profile;
        _options.IsVisible = !custom;
        _heading.Text = custom ? "График нагрузки" : "Тип теста";
        var action = custom ? "Готовые режимы" : "Свой график";
        _modeSwitch.Content = action;
        Avalonia.Automation.AutomationProperties.SetName(_modeSwitch, action);
        foreach (var (key, button) in _buttons) Ui.Select(button, key == profile);
        _settings.Content = _editors[profile];
    }

    private Control Field(LoadProfile profile, string key, string label, string initial, string hint)
    {
        var field = new TextBox { Text = initial };
        Avalonia.Automation.AutomationProperties.SetName(field, label);
        ToolTip.SetTip(field, hint);
        _inputs.Add((profile, key), field);
        return Ui.Field(label, field, hint);
    }

    private int InputInt(string key, string label) => int.TryParse(_inputs[(_profile, key)].Text, out var value)
        ? value : throw new ArgumentException($"{label}: введите целое число.");

    private double InputNumber(string key, string label) =>
        double.TryParse(_inputs[(_profile, key)].Text?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value : throw new ArgumentException($"{label}: введите число.");

    private void Set(string key, double value)
    { if (_inputs.TryGetValue((_profile, key), out var field)) field.Text = value.ToString("G", CultureInfo.InvariantCulture); }
}
