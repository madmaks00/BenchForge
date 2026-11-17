using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using BenchForge.Contracts;
using BenchForge.Coordinator;

namespace BenchForge.Desktop;

internal sealed class EngineSettings : UserControl
{
    private readonly Dictionary<string, CheckBox> _choices = [];
    private readonly TextBox _journal = new() { IsReadOnly = true, AcceptsReturn = true, Height = 130, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock _status = Ui.Label("C# выбран по умолчанию. Для остальных языков один раз подготовьте зависимости.", 13);
    private CancellationTokenSource? _preparing;
    private Button? _prepareButton, _checkButton;
    public EngineSettings()
    {
        var choices = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var engine in Engines.All)
        {
            var choice = new CheckBox { Content = engine.Language, IsChecked = engine.Id == "csharp", Margin = new Avalonia.Thickness(0, 0, 20, 10) };
            AutomationProperties.SetName(choice, engine.Language);
            _choices.Add(engine.Id, choice); choices.Children.Add(choice);
        }
        var prepare = Ui.Button("Подготовить выбранные движки");
        var check = Ui.Button("Проверить движки", "quiet");
        _prepareButton = prepare; _checkButton = check;
        prepare.Click += async (_, _) =>
        {
            if (_preparing is { } active) { active.Cancel(); return; }
            try
            {
                var selected = Read();
                using var stop = new CancellationTokenSource(); _preparing = stop;
                choices.IsEnabled = false; check.IsEnabled = false;
                prepare.Content = "Остановить подготовку"; _journal.Text = "";
                await WorkerPreparation.PrepareLocalAsync(selected, Append, stop.Token);
                _status.Text = "Движки готовы на этом компьютере.";
            }
            catch (OperationCanceledException) { _status.Text = "Подготовка остановлена."; }
            catch (Exception error) { _status.Text = error.Message; }
            finally { _preparing = null; choices.IsEnabled = true; check.IsEnabled = true; prepare.Content = "Подготовить выбранные движки"; }
        };
        check.Click += async (_, _) =>
        {
            check.IsEnabled = false;
            try { foreach (var engine in Read()) await WorkerCatalog.CheckAsync(engine, CancellationToken.None); _status.Text = "Выбранные движки запускаются; данные и гистограммы совпали с эталоном."; }
            catch (Exception error) { _status.Text = error.Message; }
            finally { check.IsEnabled = true; }
        };
        var buttons = new WrapPanel { Children = { prepare, check } };
        Content = Ui.Stack(10, choices, _status,
            Ui.Label("Одинаковая БД будет проверена каждым выбранным движком отдельно. Сравнивается язык вместе с его драйвером и способом выполнения запросов.", 12),
            buttons, new Expander { Header = "Журнал подготовки", Content = _journal });
    }
    public List<string> Read()
    {
        var selected = _choices.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToList();
        return selected.Count > 0 ? selected : throw new ArgumentException("Выберите хотя бы один движок нагрузки.");
    }
    public void Apply(IEnumerable<string> selected)
    { var values = selected.ToHashSet(); foreach (var (id, choice) in _choices) choice.IsChecked = values.Contains(id); }
    public void SetRemoteMode(bool remote)
    {
        if (_prepareButton is not null) _prepareButton.IsVisible = !remote;
        if (_checkButton is not null) _checkButton.IsVisible = !remote;
        _status.Text = remote ? "Выбранные движки подготавливаются на Linux-сервере в разделе «Окружение тестирования». Устанавливать их на этот ПК не требуется."
            : "C# готов после сборки. Остальные движки можно подготовить на этом компьютере.";
    }
    private void Append(string message) => Dispatcher.UIThread.Post(() =>
    { var text = _journal.Text + message + Environment.NewLine; _journal.Text = text.Length > 30_000 ? text[^25_000..] : text; _journal.CaretIndex = _journal.Text.Length; });
}
