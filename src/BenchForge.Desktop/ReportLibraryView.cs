using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using BenchForge.Contracts;
using BenchForge.Coordinator;

namespace BenchForge.Desktop;

public sealed class ReportLibraryView : UserControl
{
    private readonly string _root;
    private readonly StackPanel _list = Ui.Stack(12);
    private readonly TextBlock _count = Ui.Label("Сохранённые эксперименты", 13);
    private int _revision;
    public event Action<string>? OpenRequested;
    public event Action? ImportRequested;
    public ReportLibraryView(string root)
    {
        _root = root;
        var content = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        var import = Ui.Button("Импортировать отчёт", "normal", () => ImportRequested?.Invoke());
        content.Children.Add(Ui.Heading("Отчёты", "Возвращайтесь к экспериментам и сравнивайте результаты.", import));
        _count.Margin = new Thickness(0, 0, 0, 18); Grid.SetRow(_count, 1); content.Children.Add(_count);
        var scroll = Ui.Scroll(_list); Grid.SetRow(scroll, 2); content.Children.Add(scroll); Content = content;
    }
    public async Task RefreshAsync()
    {
        var revision = ++_revision;
        _count.Text = "Загружаю отчёты…";
        try
        {
            var reports = await Task.Run(async () =>
            {
                var found = new List<(string Path, BenchmarkReport Report)>();
                if (!Directory.Exists(_root)) return found;
                foreach (var folder in Directory.EnumerateDirectories(_root).OrderDescending())
                {
                    var file = Path.Combine(folder, "report.json"); if (!File.Exists(file)) continue;
                    try { await using var stream = File.OpenRead(file); var report = await JsonSerializer.DeserializeAsync<BenchmarkReport>(stream, Json.Options);
                        if (report?.SchemaVersion == 1) found.Add((file, report)); }
                    catch (IOException) { } catch (JsonException) { }
                    if (found.Count >= 40) break;
                }
                return found;
            });
            if (revision != _revision) return; _list.Children.Clear();
            _count.Text = reports.Count == 0 ? "Сохранённых отчётов пока нет" : "Последних отчётов: " + reports.Count;
            foreach (var (path, report) in reports)
            {
                var heading = string.Join(" / ", report.Config.Targets.Select(t => Catalog.Get(t).Name));
                var text = Ui.Stack(7, Ui.Label(heading, 18, Ui.Text, true),
                    Ui.Label($"{report.StartedAt.ToLocalTime():dd MMMM, HH:mm}   ·   {report.Results.Count} измерений   ·   {report.Config.Repeats} повтор(ов)", 13));
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,32,Auto") }; row.Children.Add(text);
                var badge = Ui.Badge(report.Status switch { "completed" => "Завершён", "cancelled" => "Остановлен", "running" => "Частичный", _ => "С замечаниями" }, report.Status == "completed" ? Ui.Green : Ui.Amber);
                badge.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(badge, 1); row.Children.Add(badge);
                var arrow = Ui.Label("→", 20, Ui.Muted); arrow.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(arrow, 3); row.Children.Add(arrow);
                var button = Ui.Button(heading, "normal", () => OpenRequested?.Invoke(path)); button.Content = row; button.Padding = new Thickness(24);
                button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Stretch; _list.Children.Add(button);
            }
            if (reports.Count == 0)
            {
                var import = Ui.Button("Открыть report.json", "normal", () => ImportRequested?.Invoke()); import.HorizontalAlignment = HorizontalAlignment.Left;
                _list.Children.Add(Ui.Card(Ui.Stack(16, Ui.Label("Здесь будет история экспериментов", 23, Ui.Text, true), Ui.Label("Запустите новый тест или импортируйте уже сохранённый отчёт."), import), 36));
            }
        }
        catch (Exception e) { if (revision == _revision) { _count.Text = "Не удалось прочитать папку отчётов"; _list.Children.Clear(); _list.Children.Add(Ui.Label(e.Message, 13, Ui.Amber)); } }
    }
}
