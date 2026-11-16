using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using BenchForge.Contracts;
using BenchForge.Coordinator;

namespace BenchForge.Desktop;

internal sealed class VirtualMachineSettings : UserControl
{
    private readonly ComboBox _mode = new() { ItemsSource = new[] { "Этот компьютер", "Чистая виртуальная машина" }, SelectedIndex = 0 };
    private readonly TextBox _template = new() { Text = "benchforge-base-v1" };
    private readonly TextBox _cpus = new() { Text = "4" };
    private readonly TextBox _memory = new() { Text = "6144" };
    private readonly TextBox _disk = new() { Text = "32" };
    private readonly TextBlock _status = Ui.Label("", 12);
    private readonly Button _prepare = Ui.Button("Подготовить шаблон");
    private CancellationTokenSource? _preparing;

    public VirtualMachineSettings()
    {
        foreach (var (control, label) in new (Control, string)[] { (_mode, "Окружение"), (_template, "Шаблон VM"),
            (_cpus, "CPU машины"), (_memory, "Память VM, МБ"), (_disk, "Диск VM, ГБ") })
            AutomationProperties.SetName(control, label);
        var fields = Ui.Stack(10, Ui.Field("Шаблон VM", _template),
            Ui.Columns("*,12,*,12,*", Ui.Field("CPU машины", _cpus), Ui.Field("Память, МБ", _memory), Ui.Field("Диск, ГБ", _disk)));
        var details = Ui.Stack(12,
            Ui.Label("Каждый прогон получает отдельную Linux-машину из выключенного шаблона. База и генератор работают внутри неё. После получения отчёта машина удаляется.", 13),
            fields, _prepare, _status);
        details.IsVisible = false;
        _mode.SelectionChanged += (_, _) => details.IsVisible = _mode.SelectedIndex == 1;
        _prepare.Click += async (_, _) =>
        {
            if (_preparing is { } active) { active.Cancel(); return; }
            try
            {
                var options = Read() ?? throw new InvalidOperationException("Выберите окружение VM.");
                using var stop = new CancellationTokenSource();
                _preparing = stop;
                _prepare.Content = "Остановить подготовку";
                fields.IsEnabled = false;
                _mode.IsEnabled = false;
                await new VirtualMachineTemplate().PrepareAsync(options,
                    Catalog.Targets.Where(target => target.Kind != TargetKind.Sqlite).Select(target => target.Image),
                    message => Dispatcher.UIThread.Post(() => _status.Text = message), stop.Token);
            }
            catch (OperationCanceledException) { _status.Text = "Подготовка остановлена."; }
            catch (Exception error) { _status.Text = error.Message; }
            finally
            {
                _preparing = null;
                _prepare.Content = "Подготовить шаблон";
                fields.IsEnabled = true;
                _mode.IsEnabled = true;
            }
        };
        Content = Ui.Stack(12, Ui.Field("Окружение", _mode), details);
    }

    public VirtualMachineOptions? Read()
    {
        if (_mode.SelectedIndex == 0) return null;
        if (!int.TryParse(_cpus.Text, out var cpus) || !int.TryParse(_memory.Text, out var memory) || !int.TryParse(_disk.Text, out var disk))
            throw new ArgumentException("Проверьте CPU, память и диск виртуальной машины.");
        var options = new VirtualMachineOptions { Template = _template.Text?.Trim() ?? "", Cpus = cpus, MemoryMb = memory, DiskGb = disk };
        options.Validate();
        return options;
    }

    public void Apply(VirtualMachineOptions? options)
    {
        _mode.SelectedIndex = options is null ? 0 : 1;
        if (options is null) return;
        _template.Text = options.Template;
        _cpus.Text = options.Cpus.ToString();
        _memory.Text = options.MemoryMb.ToString();
        _disk.Text = options.DiskGb.ToString();
    }
}
