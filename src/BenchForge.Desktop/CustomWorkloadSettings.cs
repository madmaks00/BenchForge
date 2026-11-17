using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using BenchForge.Contracts;
using BenchForge.Coordinator;

namespace BenchForge.Desktop;

internal sealed class CustomWorkloadSettings : UserControl
{
    private readonly ComboBox _source = new() { ItemsSource = new[] { "Стандартные тесты", "Свои таблицы и SQL-запросы" }, SelectedIndex = 0 };
    private readonly TextBlock _summary = Ui.Label("", 13);
    private readonly TextBlock _status = Ui.Label("", 12, Ui.Amber);
    private SqlWorkload _workload = WorkloadTemplates.Store();
    private string? _directory;
    public event Action<bool>? ModeChanged;
    public bool Active => _source.SelectedIndex == 1;
    public string? Directory => _directory;
    public SqlWorkload? Read() => Active ? _workload : null;
    public CustomWorkloadSettings()
    {
        var editor = Ui.Button("Открыть редактор сценария"); editor.Click += async (_, _) =>
        {
            try
            {
                var owner = TopLevel.GetTopLevel(this) as Window ?? throw new InvalidOperationException("Не найдено окно приложения.");
                var result = await new WorkloadEditorWindow(_workload, _directory).ShowDialog<LoadedWorkload?>(owner);
                if (result is not null) { _workload = result.Workload; _directory = result.Directory; Refresh(); }
            }
            catch (Exception error) { _status.Text = error.Message; }
        };
        var load = Ui.Button("Открыть сценарий", "quiet"); load.Click += async (_, _) =>
        {
            try
            {
                var top = TopLevel.GetTopLevel(this)!;
                var files = await top.StorageProvider.OpenFilePickerAsync(new() { Title = "Свой SQL-сценарий", AllowMultiple = false,
                    FileTypeFilter = [new("Сценарий BenchForge") { Patterns = ["*.bfworkload", "*.json"] }] });
                var path = files.FirstOrDefault()?.TryGetLocalPath(); if (path is null) return;
                var loaded = await WorkloadFiles.OpenAsync(path, CancellationToken.None); Apply(loaded.Workload, loaded.Directory); _status.Text = "Сценарий загружен.";
            }
            catch (Exception error) { _status.Text = error.Message; }
        };
        var save = Ui.Button("Сохранить пакет", "quiet"); save.Click += async (_, _) =>
        {
            try
            {
                var file = await TopLevel.GetTopLevel(this)!.StorageProvider.SaveFilePickerAsync(new() { Title = "Сохранить сценарий с входными файлами", SuggestedFileName = "scenario.bfworkload", DefaultExtension = "bfworkload" });
                var path = file?.TryGetLocalPath(); if (path is null) return;
                await WorkloadFiles.SavePackageAsync(_workload, _directory, path, CancellationToken.None); _status.Text = "Пакет сохранён вместе с импортированными файлами.";
            }
            catch (Exception error) { _status.Text = error.Message; }
        };
        var template = new ComboBox { ItemsSource = new[] { "Интернет-магазин", "Кошельки и переводы", "Пустой пример" }, SelectedIndex = 0 };
        var use = Ui.Button("Создать по шаблону", "quiet", () => { _workload = template.SelectedIndex switch { 1 => WorkloadTemplates.Wallets(), 2 => WorkloadTemplates.Blank(), _ => WorkloadTemplates.Store() }; _directory = null; Refresh(); });
        var body = Ui.Stack(12, _summary, editor, new WrapPanel { Orientation = Orientation.Horizontal, Children = { load, save } },
            new Expander { Header = "Готовые примеры", Content = Ui.Columns("*,12,*", template, use) },
            Ui.Label("C# · PostgreSQL, MySQL, MariaDB и SQLite. Свою схему и данные можно использовать на этом ПК, в чистой VM или на Linux-сервере.", 12), _status);
        var details = Ui.Card(body, 16); details.IsVisible = false;
        _source.SelectionChanged += (_, _) => { details.IsVisible = Active; ModeChanged?.Invoke(Active); Refresh(); };
        Content = Ui.Stack(12, Ui.Field("Что тестируем", _source), details); Refresh();
    }
    private void Refresh() => _summary.Text = _workload.Name + " · таблиц: " + _workload.Datasets.Count + " · операций: " + _workload.ActiveOperations.Count() + " · проверок: " + _workload.Checks.Count;
    public void Apply(SqlWorkload? workload, string? directory)
    { if (workload is not null) _workload = workload; _directory = directory; _source.SelectedIndex = workload is null ? 0 : 1; Refresh(); }
}
