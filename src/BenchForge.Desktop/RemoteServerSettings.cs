using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using BenchForge.Contracts;
using BenchForge.Coordinator;

namespace BenchForge.Desktop;

internal sealed class RemoteServerSettings(Func<IReadOnlyCollection<string>> engines) : UserControl
{
    private readonly TextBox _name = new() { PlaceholderText = "Например, тестовый VPS" };
    private readonly TextBox _host = new() { PlaceholderText = "IP, домен или алиас из SSH config" };
    private readonly TextBox _user = new() { PlaceholderText = "ubuntu; пусто — пользователь из SSH config" };
    private readonly TextBox _port = new() { Text = "22" };
    private readonly TextBox _key = new() { PlaceholderText = "Пусто — стандартный ключ или ssh-agent" };
    private readonly TextBox _known = new() { PlaceholderText = "Пусто — стандартный known_hosts" };
    private readonly TextBlock _status = Ui.Label("", 12);
    private readonly TextBox _journal = new() { IsReadOnly = true, AcceptsReturn = true, Height = 140, FontSize = 12 };
    private CancellationTokenSource? _preparing;
    public event Action<RemoteServerOptions>? ResumeRequested;

    public RemoteServerSettings() : this(() => new[] { "csharp" }) { }

    public void Initialize()
    {
        var fields = Ui.Stack(12, Ui.Columns("*,16,*", Ui.Field("Название сервера", _name), Ui.Field("Адрес или SSH-алиас", _host)),
            Ui.Columns("3*,16,*", Ui.Field("Пользователь", _user), Ui.Field("Порт SSH", _port)),
            new Expander { Header = "Параметры SSH-ключа", Content = Ui.Stack(12, Ui.Field("Файл приватного ключа на этом ПК", _key), Ui.Field("Файл известных серверов", _known)) });
        foreach (var (control, label) in new[] { (_name, "Название сервера"), (_host, "Адрес Linux-сервера"), (_user, "Пользователь SSH"),
            (_port, "Порт SSH"), (_key, "Приватный SSH-ключ"), (_known, "Файл known_hosts") }) AutomationProperties.SetName(control, label);
        var check = Ui.Button("Проверить SSH", "quiet");
        var prepare = Ui.Button("Подготовить сервер");
        var resume = Ui.Button("Продолжить наблюдение", "quiet");
        check.Click += async (_, _) =>
        {
            check.IsEnabled = false;
            try { var info = await new RemoteAgentClient().ProbeAsync(Read(), CancellationToken.None); _status.Text = info.Hostname + " · Linux · " + info.Architecture + " · Docker доступен"; }
            catch (Exception error) { _status.Text = error.Message; }
            finally { check.IsEnabled = true; }
        };
        prepare.Click += async (_, _) =>
        {
            if (_preparing is { } running) { running.Cancel(); return; }
            try
            {
                var server = Read(); var selected = engines();
                using var stop = new CancellationTokenSource(); _preparing = stop;
                fields.IsEnabled = false; check.IsEnabled = false; resume.IsEnabled = false;
                _journal.Text = ""; prepare.Content = "Остановить подготовку";
                await new RemoteAgentClient().PrepareAsync(server, selected, Append, stop.Token);
                _status.Text = "Сервер готов для выбранных движков.";
            }
            catch (OperationCanceledException) { _status.Text = "Подготовка прервана. При необходимости её можно повторить."; }
            catch (Exception error) { _status.Text = error.Message; }
            finally { _preparing = null; fields.IsEnabled = true; check.IsEnabled = true; resume.IsEnabled = true; prepare.Content = "Подготовить сервер"; }
        };
        resume.Click += (_, _) => { try { ResumeRequested?.Invoke(Read()); } catch (Exception error) { _status.Text = error.Message; } };
        Content = Ui.Stack(14,
            Ui.Label("База и генератор работают на сервере. Этот компьютер передаёт настройки и показывает результаты. На сервере нужен доступный по SSH Docker Engine.", 13),
            fields, Ui.Columns("*,12,*", prepare, check), resume, _status,
            Ui.Label("Используется доступ по ключу. Первое SSH-подключение и отпечаток сервера подтвердите в обычном SSH-клиенте. Пароли и приватные ключи на сервер не передаются.", 12),
            new Expander { Header = "Журнал подготовки сервера", Content = _journal });
    }

    public RemoteServerOptions Read()
    {
        if (!int.TryParse(_port.Text, out var port)) throw new ArgumentException("Укажите числовой порт SSH.");
        var options = new RemoteServerOptions { Name = _name.Text?.Trim() ?? "", Host = _host.Text?.Trim() ?? "", User = _user.Text?.Trim() ?? "", Port = port,
            IdentityFile = string.IsNullOrWhiteSpace(_key.Text) ? null : _key.Text.Trim(), KnownHostsFile = string.IsNullOrWhiteSpace(_known.Text) ? null : _known.Text.Trim() };
        options.Validate(); return options;
    }
    public void Apply(RemoteServerOptions options)
    { _name.Text = options.Name; _host.Text = options.Host; _user.Text = options.User; _port.Text = options.Port.ToString(); _key.Text = options.IdentityFile; _known.Text = options.KnownHostsFile; }
    private void Append(string line) => Dispatcher.UIThread.Post(() =>
    {
        _status.Text = line.Length < 160 ? line : "Подготовка сервера…";
        var text = _journal.Text + line + Environment.NewLine; _journal.Text = text.Length > 60000 ? text[^50000..] : text;
        _journal.CaretIndex = _journal.Text.Length;
    });
}
