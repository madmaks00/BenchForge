using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using BenchForge.Contracts;

namespace BenchForge.Desktop;

internal sealed class ResourceSeriesSettings : UserControl
{
    private readonly CheckBox _enabled = new() { Content = "Проверить несколько лимитов CPU и памяти" };
    private readonly StackPanel _rows = Ui.Stack(8);
    private readonly List<(Control View, TextBox Cpu, TextBox Memory)> _items = [];
    private readonly Control _editor;
    public ResourceSeriesSettings()
    {
        var add = Ui.Button("Добавить сочетание", "quiet", () => Add(2, 2048));
        _editor = Ui.Stack(12, Ui.Label("Каждое сочетание запускается отдельно на свежей базе. Все результаты попадут в один отчёт; графики покажут эффект CPU и памяти.", 13),
            Ui.Columns("*,12,*,12,70", Ui.Label("CPU", 12), Ui.Label("Память, МБ", 12), Ui.Label("")), _rows, add);
        _editor.IsVisible = false;
        _enabled.IsCheckedChanged += (_, _) => { _editor.IsVisible = _enabled.IsChecked == true; if (_editor.IsVisible && _items.Count == 0) { Add(1, 2048); Add(2, 2048); } };
        Content = Ui.Stack(12, _enabled, _editor);
    }
    private void Add(double cpu, int memory)
    {
        var c = new TextBox { Text = cpu.ToString(CultureInfo.InvariantCulture) }; var m = new TextBox { Text = memory.ToString(CultureInfo.InvariantCulture) };
        Control? row = null;
        var remove = Ui.Button("×", "quiet", () => { _rows.Children.Remove(row!); _items.RemoveAll(item => item.View == row); });
        row = Ui.Columns("*,12,*,12,70", c, m, remove); _rows.Children.Add(row); _items.Add((row, c, m));
    }
    public List<ResourceLimits> Read()
    {
        if (_enabled.IsChecked != true) return [];
        if (_items.Count < 2) throw new ArgumentException("Для серии добавьте хотя бы два сочетания ресурсов.");
        return _items.Select(item => double.TryParse(item.Cpu.Text?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var cpu) && int.TryParse(item.Memory.Text, out var memory)
            ? new ResourceLimits(cpu, memory) : throw new ArgumentException("Проверьте CPU и память в серии ресурсов.")).ToList();
    }
    public void Apply(IReadOnlyList<ResourceLimits> profiles)
    {
        _rows.Children.Clear(); _items.Clear(); foreach (var profile in profiles) Add(profile.CpuLimit, profile.MemoryMb);
        _enabled.IsChecked = profiles.Count > 0; _editor.IsVisible = profiles.Count > 0;
    }
}
