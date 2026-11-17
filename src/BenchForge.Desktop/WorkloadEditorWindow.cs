using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BenchForge.Contracts;
using BenchForge.Coordinator;

namespace BenchForge.Desktop;

internal sealed class WorkloadEditorWindow : Window
{
    private SqlWorkload _model;
    private string? _directory;
    private readonly TextBox _name, _description;
    private readonly ComboBox _mode;
    private readonly CheckBox _reset, _prepare;
    private readonly TextBox _timeout;
    private readonly ContentControl _body = new();
    private readonly TextBlock _status = Ui.Label("", 12, Ui.Amber);
    private readonly Button[] _tabs;
    private Action _commit = () => { };
    private int _page;
    private CancellationTokenSource? _probe;
    private CancellationTokenSource? _importing;
    private string? _storageRoot;
    private readonly string _originalFingerprint;

    public WorkloadEditorWindow(SqlWorkload workload, string? directory)
    {
        _model = JsonSerializer.Deserialize<SqlWorkload>(JsonSerializer.Serialize(workload, Json.Options), Json.Options)!; _directory = directory;
        _originalFingerprint = _model.Fingerprint;
        Title = "Свой сценарий · BenchForge"; Icon = BrandIcons.WindowIcon(); Width = 1060; Height = 790; MinWidth = 900; MinHeight = 650;
        Background = Ui.B(Ui.Background); Foreground = Ui.B(Ui.Text); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _name = WorkloadForm.Text(_model.Name); _description = WorkloadForm.Text(_model.Description);
        _mode = new() { ItemsSource = new[] { "Смесь запросов", "Каждая операция отдельно", "Отдельно и затем смесь" }, SelectedIndex = (int)_model.Mode }; _timeout = WorkloadForm.Text(_model.CommandTimeoutSeconds.ToString());
        _reset = new() { Content = "Заново загружать таблицы между отдельными операциями", IsChecked = _model.ResetBetweenOperations };
        _prepare = new() { Content = "Подготавливать запросы до замеров", IsChecked = _model.PrepareCommands };
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto"), Margin = new Thickness(24) };
        root.Children.Add(Ui.Columns("2*,16,*", Ui.Field("Название сценария", _name), Ui.Field("Как выполнять операции", _mode)));
        _tabs = new[] { "Таблицы и данные", "Операции", "Схема и индексы", "Проверки", "Настройки и JSON" }.Select((text, index) => Ui.Button(text, "quiet", () => GoTo(index))).ToArray();
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(0, 16, 0, 14) };
        foreach (var tab in _tabs) { tab.FontSize = 12; tab.Padding = new Thickness(10, 7); tab.MinHeight = 34; navigation.Children.Add(tab); }
        Grid.SetRow(navigation, 1); root.Children.Add(navigation); Grid.SetRow(_body, 2); root.Children.Add(_body);
        Grid.SetRow(_status, 3); _status.Margin = new Thickness(0, 10, 0, 10); root.Children.Add(_status);
        var validate = Ui.Button("Проверить описание", "quiet", Validate);
        var probe = Ui.Button("Пробный запуск SQLite"); probe.Click += async (_, _) => await ProbeAsync(probe);
        var save = Ui.Button("Применить сценарий", "primary", () =>
        {
            if (_importing is not null || _probe is not null) { _status.Text = "Завершите или остановите текущую операцию перед применением сценария."; return; }
            try { var value = Read(); value.Validate(); Close(new LoadedWorkload(value, _directory ?? "")); }
            catch (Exception error) { _status.Text = error.Message; }
        });
        var footer = new DockPanel(); var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { probe, save } };
        DockPanel.SetDock(right, Dock.Right); footer.Children.Add(right); footer.Children.Add(validate); Grid.SetRow(footer, 4); root.Children.Add(footer);
        Content = root; GoTo(0);
        Closing += (_, _) => { _probe?.Cancel(); _importing?.Cancel(); };
    }

    private SqlWorkload Read()
    {
        _commit();
        _model = _model with { Name = _name.Text?.Trim() ?? "", Description = _description.Text ?? "", Mode = (WorkloadMode)_mode.SelectedIndex,
            ResetBetweenOperations = _reset.IsChecked == true, PrepareCommands = _prepare.IsChecked == true, CommandTimeoutSeconds = checked((int)WorkloadForm.Long(_timeout, "Таймаут")) };
        return _model;
    }
    private void GoTo(int page)
    {
        try
        {
            _commit(); _commit = () => { }; _page = page;
            for (var index = 0; index < _tabs.Length; index++) Ui.Select(_tabs[index], index == page);
            _body.Content = page switch { 0 => Tables(), 1 => Operations(), 2 => Schema(), 3 => Checks(), _ => Options() };
            _status.Text = "";
        }
        catch (Exception error) { _status.Text = error.Message; }
    }
    private void Validate()
    {
        try
        {
            var workload = Read(); workload.Validate(new() { CustomWorkload = workload, DatasetRows = [1000] });
            _status.Text = "Описание корректно. SQL и данные дополнительно проверяются при запуске. " + workload.Datasets.Count + " таблиц · " + workload.ActiveOperations.Count() + " операций · " + workload.Checks.Count + " проверок.";
        }
        catch (Exception error) { _status.Text = error.Message; }
    }

    private static ListBox List<T>(IEnumerable<T> items, Func<T, string> title) where T : class => new()
    { ItemsSource = items, ItemTemplate = new FuncDataTemplate<T>((item, _) => Ui.Label(item is null ? "" : title(item), 13, Ui.Text)), MinHeight = 80 };
    private static Grid Split(Control left, Control right)
    { var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("210,18,*") }; grid.Children.Add(left); Grid.SetColumn(right, 2); grid.Children.Add(right); return grid; }
    private static StackPanel Actions(params Control[] actions) => new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }.AddActions(actions);
    private static DataGrid ValuesGrid(ObservableCollection<ValueDefinition> values)
    {
        var grid = new DataGrid { ItemsSource = values, AutoGenerateColumns = false, IsReadOnly = true, Height = 230, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal };
        foreach (var (title, path) in new[] { ("Поле", "Name"), ("Тип", "Type"), ("Генератор", "Generator"), ("Ключ", "PrimaryKey"), ("NULL", "Nullable") })
            grid.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(path), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        return grid;
    }
    private Control Tables()
    {
        var items = new ObservableCollection<SqlDataset>(_model.Datasets);
        var list = List(items, table => table.Name + (table.Import is null ? "" : " · файл"));
        var panel = new ContentControl(); SqlDataset? active = null; Action save = () => { }; var changing = false;
        void Show()
        {
            var selectedTable = list.SelectedItem as SqlDataset; save(); active = selectedTable;
            changing = true; list.SelectedItem = selectedTable; changing = false;
            if (active is null) { panel.Content = Ui.Label("Добавьте таблицу."); save = () => { }; return; }
            var original = active; var name = WorkloadForm.Text(original.Name);
            var fixedRows = WorkloadForm.Text(original.FixedRows?.ToString()); var multiplier = WorkloadForm.Text(original.RowsMultiplier.ToString());
            var columns = new ObservableCollection<ValueDefinition>(original.Columns); var grid = ValuesGrid(columns); ImportedData? imported = original.Import;
            var importedStatus = Ui.Label(imported is null ? "Данные генерируются по правилам полей." : $"{imported.File} · {imported.Rows:N0} строк · SHA-256 проверяется перед запуском", 12);
            save = () =>
            {
                var index = items.IndexOf(active!); if (index < 0) return;
                var updated = original with { Name = name.Text?.Trim() ?? "", FixedRows = WorkloadForm.Optional(fixedRows, "Фиксированные строки"), RowsMultiplier = checked((int)WorkloadForm.Long(multiplier, "Множитель")), Columns = columns.ToList(), Import = imported };
                changing = true; items[index] = updated; active = updated; list.SelectedItem = updated; changing = false;
            };
            async Task Edit(bool add)
            {
                try
                {
                    var selected = add ? new ValueDefinition { Name = "field_" + (columns.Count + 1), Type = SqlValueType.Text, Generator = ValueGenerator.Text } : grid.SelectedItem as ValueDefinition;
                    if (selected is null) return;
                    var value = await WorkloadForm.ValueDialog(selected, false).ShowDialog<ValueDefinition?>(this);
                    if (value is null) return; if (add) columns.Add(value); else columns[columns.IndexOf(selected)] = value;
                }
                catch (Exception error) { _status.Text = error.Message; }
            }
            var add = Ui.Button("Добавить поле"); add.Click += async (_, _) => await Edit(true);
            var edit = Ui.Button("Изменить", "quiet"); edit.Click += async (_, _) => await Edit(false);
            var remove = Ui.Button("Удалить поле", "quiet", () => { if (grid.SelectedItem is ValueDefinition value) columns.Remove(value); });
            grid.DoubleTapped += async (_, _) => await Edit(false);
            var import = Ui.Button("Импорт CSV / JSONL"); import.Click += async (_, _) =>
            {
                if (_importing is not null) { _importing.Cancel(); return; }
                using var stopImport = new CancellationTokenSource(); _importing = stopImport;
                import.Content = "Остановить импорт";
                list.IsEnabled = false;
                foreach (var tab in _tabs) tab.IsEnabled = false;
                try
                {
                    var files = await StorageProvider.OpenFilePickerAsync(new() { Title = "Данные таблицы", AllowMultiple = false, FileTypeFilter = [new("CSV / JSONL") { Patterns = ["*.csv", "*.jsonl", "*.ndjson"] }] });
                    var path = files.FirstOrDefault()?.TryGetLocalPath(); if (path is null) return;
                    var tableName = name.Text?.Trim() ?? "";
                    var format = path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? ImportFormat.Csv : ImportFormat.JsonLines;
                    _status.Text = "Проверка файла и подсчёт строк…";
                    var inspection = await Task.Run(() => WorkloadFiles.InspectAsync(path, format, stopImport.Token));
                    var previousDirectory = _directory;
                    var directory = await WorkloadFiles.ImportFileAsync(path, tableName, stopImport.Token, _storageRoot);
                    if (!string.IsNullOrWhiteSpace(previousDirectory))
                        await WorkloadFiles.CopyInputsAsync(new() { CustomWorkload = _model with { Datasets = items.Where(table => table != active).ToList() }, WorkloadDirectory = previousDirectory }, directory, stopImport.Token);
                    _directory = directory; var relative = "data/" + tableName + (format == ImportFormat.Csv ? ".csv" : ".jsonl");
                    imported = new(relative, format, inspection.Sha256, inspection.Rows, inspection.Delimiter);
                    columns.Clear(); foreach (var column in inspection.Columns) columns.Add(column.Name.Equals("id", StringComparison.OrdinalIgnoreCase) && column.Type is SqlValueType.Int64 or SqlValueType.Int32 ? column with { PrimaryKey = true, Nullable = false, Generator = ValueGenerator.Sequence } : column);
                    importedStatus.Text = $"{Path.GetFileName(path)} · {inspection.Rows:N0} строк. Проверьте типы полей: они определены по первым 1000 записям.";
                    _status.Text = "Файл импортирован. Его записи не будут вложены в отчёт.";
                }
                catch (OperationCanceledException) { _status.Text = "Импорт остановлен."; }
                catch (Exception error) { _status.Text = error.Message; }
                finally { _importing = null; import.Content = "Импорт CSV / JSONL"; list.IsEnabled = true; foreach (var tab in _tabs) tab.IsEnabled = true; }
            };
            var generated = Ui.Button("Использовать генератор", "quiet", () => { imported = null; importedStatus.Text = "Данные генерируются по правилам полей."; });
            panel.Content = Ui.Scroll(Ui.Stack(14, Ui.Field("Имя таблицы", name),
                Ui.Columns("*,12,*", Ui.Field("Строк = базовый объём × множитель", multiplier), Ui.Field("Фиксированные строки; пусто — по объёму", fixedRows)),
                grid, Actions(add, edit, remove), Actions(import, generated), importedStatus,
                Ui.Label("Порядок таблиц задаёт порядок загрузки. Reference ссылается на предыдущую таблицу с числовыми ключами 1..N. Импорт загружает файл целиком при каждом прогоне.", 12)));
        }
        list.SelectionChanged += (_, _) => { if (changing) return; try { Show(); } catch (Exception error) { _status.Text = error.Message; } };
        var addTable = Ui.Button("Добавить таблицу", "quiet", () => { var table = new SqlDataset { Name = "table_" + (items.Count + 1), Columns = [new() { Name = "id", PrimaryKey = true }] }; items.Add(table); list.SelectedItem = table; });
        var delete = Ui.Button("Удалить", "quiet", () => { if (list.SelectedItem is SqlDataset table) { save = () => { }; active = null; items.Remove(table); list.SelectedItem = items.FirstOrDefault(); } });
        void Move(int delta)
        {
            save(); if (active is null) return; var index = items.IndexOf(active); var next = index + delta;
            if (next < 0 || next >= items.Count) return; changing = true; items.Move(index, next); list.SelectedItem = active; changing = false;
        }
        list.SelectedIndex = 0; _commit = () => { save(); _model = _model with { Datasets = items.ToList() }; };
        return Split(Ui.Stack(10, list, Actions(Ui.Button("↑ Выше", "quiet", () => Move(-1)), Ui.Button("↓ Ниже", "quiet", () => Move(1))), addTable, delete), panel);
    }

    private Control Operations()
    {
        var items = new ObservableCollection<SqlOperation>(_model.Operations); var list = List(items, operation => operation.Name);
        var panel = new ContentControl(); SqlOperation? active = null; Action save = () => { }; var changing = false;
        void Show()
        {
            var selectedOperation = list.SelectedItem as SqlOperation; save(); active = selectedOperation;
            changing = true; list.SelectedItem = selectedOperation; changing = false;
            if (active is null) { panel.Content = Ui.Label("Добавьте операцию."); save = () => { }; return; }
            var original = active; var id = WorkloadForm.Text(original.Id); var name = WorkloadForm.Text(original.Name); var weight = WorkloadForm.Text(original.Weight.ToString());
            var enabled = new CheckBox { Content = "Включена", IsChecked = original.Enabled }; var readOnly = new CheckBox { Content = "Разрешить для прогрева чтением", IsChecked = original.ReadOnly };
            var transaction = new CheckBox { Content = "Все шаги в одной транзакции", IsChecked = original.Transaction }; var isolation = WorkloadForm.Enum(original.Isolation);
            var steps = new ObservableCollection<SqlStep>(original.Steps); var stepList = List(steps, step => step.Result + " · " + step.Command.Sql.Replace('\n', ' ')[..Math.Min(95, step.Command.Sql.Length)]); stepList.MaxHeight = 160;
            var parameters = new ObservableCollection<ValueDefinition>(original.Parameters); var parameterList = List(parameters, value => "@" + value.Name + " · " + value.Generator + " · " + value.Type); parameterList.MaxHeight = 120;
            save = () => { var index = items.IndexOf(active!); if (index >= 0) { var value = original with { Id = id.Text?.Trim() ?? "", Name = name.Text?.Trim() ?? "", Weight = checked((int)WorkloadForm.Long(weight, "Вес")), Enabled = enabled.IsChecked == true,
                ReadOnly = readOnly.IsChecked == true, Transaction = transaction.IsChecked == true, Isolation = WorkloadForm.Selected<SqlIsolation>(isolation), Steps = steps.ToList(), Parameters = parameters.ToList() }; changing = true; items[index] = value; active = value; list.SelectedItem = value; changing = false; } };
            async Task Step(bool add)
            {
                var selected = add ? new SqlStep { Command = new() { Sql = "SELECT id FROM " + (_model.Datasets.FirstOrDefault()?.Name ?? "items") + " WHERE id=@key" } } : stepList.SelectedItem as SqlStep;
                if (selected is null) return; var value = await WorkloadForm.StepDialog(selected).ShowDialog<SqlStep?>(this);
                if (value is not null) { if (add) steps.Add(value); else steps[steps.IndexOf(selected)] = value; }
            }
            async Task Parameter(bool add)
            {
                var selected = add ? new ValueDefinition { Name = "param_" + (parameters.Count + 1), Generator = ValueGenerator.Integer } : parameterList.SelectedItem as ValueDefinition;
                if (selected is null) return; var value = await WorkloadForm.ValueDialog(selected, true).ShowDialog<ValueDefinition?>(this);
                if (value is not null) { if (add) parameters.Add(value); else parameters[parameters.IndexOf(selected)] = value; }
            }
            var addStep = Ui.Button("Добавить SQL-шаг"); addStep.Click += async (_, _) => await Step(true);
            var editStep = Ui.Button("Изменить", "quiet"); editStep.Click += async (_, _) => await Step(false);
            var deleteStep = Ui.Button("Удалить", "quiet", () => { if (stepList.SelectedItem is SqlStep value) steps.Remove(value); });
            var up = Ui.Button("↑", "quiet", () => { if (stepList.SelectedItem is SqlStep value && steps.IndexOf(value) > 0) { var index = steps.IndexOf(value); steps.Move(index, index - 1); stepList.SelectedItem = value; } });
            var down = Ui.Button("↓", "quiet", () => { if (stepList.SelectedItem is SqlStep value && steps.IndexOf(value) < steps.Count - 1) { var index = steps.IndexOf(value); steps.Move(index, index + 1); stepList.SelectedItem = value; } });
            stepList.DoubleTapped += async (_, _) => await Step(false);
            var addParam = Ui.Button("Добавить параметр", "quiet"); addParam.Click += async (_, _) => await Parameter(true);
            var editParam = Ui.Button("Изменить", "quiet"); editParam.Click += async (_, _) => await Parameter(false);
            var deleteParam = Ui.Button("Удалить", "quiet", () => { if (parameterList.SelectedItem is ValueDefinition value) parameters.Remove(value); });
            panel.Content = Ui.Scroll(Ui.Stack(12, Ui.Columns("*,12,*", Ui.Field("Название", name), Ui.Field("ID", id)),
                Ui.Columns("*,12,*", Ui.Field("Вес в смеси", weight), enabled), readOnly, Ui.Columns("*,12,*", transaction, Ui.Field("Изоляция транзакции", isolation)),
                Ui.Label("SQL-шаги — выполняются сверху вниз", 14, Ui.Text, true), stepList, Actions(addStep, editStep, deleteStep, up, down),
                new Expander { Header = "Параметры запроса", Content = Ui.Stack(10, parameterList, Actions(addParam, editParam, deleteParam),
                    Ui.Label("Доступны @key, @sequence (уникальный ID после базового объёма), @client, @baseRows, @seed и @rows_имяТаблицы. Свои параметры создаются один раз на всю операцию. Scalar можно передать следующему шагу.", 12)) }));
        }
        list.SelectionChanged += (_, _) => { if (changing) return; try { Show(); } catch (Exception error) { _status.Text = error.Message; } };
        var add = Ui.Button("Добавить операцию", "quiet", () => { var value = new SqlOperation { Id = "operation_" + (items.Count + 1), Name = "Новая операция", Steps = [new() { Command = new() { Sql = "SELECT 1" } }] }; items.Add(value); list.SelectedItem = value; });
        var delete = Ui.Button("Удалить", "quiet", () => { if (list.SelectedItem is SqlOperation value) { save = () => { }; active = null; items.Remove(value); list.SelectedItem = items.FirstOrDefault(); } });
        void Move(int delta)
        {
            save(); if (active is null) return; var index = items.IndexOf(active); var next = index + delta;
            if (next < 0 || next >= items.Count) return; changing = true; items.Move(index, next); list.SelectedItem = active; changing = false;
        }
        list.SelectedIndex = 0; _commit = () => { save(); _model = _model with { Operations = items.ToList() }; };
        return Split(Ui.Stack(10, list, Actions(Ui.Button("↑ Выше", "quiet", () => Move(-1)), Ui.Button("↓ Ниже", "quiet", () => Move(1))), add, delete), panel);
    }

    private Control Schema()
    {
        var automatic = new CheckBox { Content = "Создавать схему автоматически из таблиц и полей", IsChecked = string.IsNullOrWhiteSpace(_model.Schema.Sql) && _model.Schema.Overrides.Count == 0 };
        var schema = new SqlTextEditor(_model.Schema, 200) { IsVisible = automatic.IsChecked != true };
        var after = new SqlTextEditor(_model.AfterLoad, 170);
        automatic.IsCheckedChanged += (_, _) => schema.IsVisible = automatic.IsChecked != true;
        var preview = WorkloadForm.Text(SqlWorkloadText.AutoSchema(_model, TargetKind.PostgreSql), true, 200); preview.IsReadOnly = true;
        _commit = () => _model = _model with { Schema = automatic.IsChecked == true ? new() : schema.Read(), AfterLoad = after.Read() };
        return Ui.Scroll(Ui.Stack(14, automatic, schema,
            new Expander { Header = "Посмотреть автоматически созданную схему PostgreSQL", Content = preview },
            Ui.Label("После загрузки данных: индексы и статистика", 15, Ui.Text, true), after,
            Ui.Label("Общий SQL используется по умолчанию. Для отличающегося синтаксиса выберите диалект и измените текст. Все таблицы сценария должны быть перечислены на вкладке данных.", 12)));
    }
    private Control Checks()
    {
        var items = new ObservableCollection<SqlCheck>(_model.Checks); var list = List(items, check => check.Name + " · " + check.Comparison + " " + check.Expected); list.Height = 280;
        async Task Edit(bool add)
        {
            var selected = add ? new SqlCheck { Command = new() { Sql = "SELECT COUNT(*) FROM " + (_model.Datasets.FirstOrDefault()?.Name ?? "items") }, Expected = "rows:" + (_model.Datasets.FirstOrDefault()?.Name ?? "items") } : list.SelectedItem as SqlCheck;
            if (selected is null) return; var value = await WorkloadForm.CheckDialog(selected).ShowDialog<SqlCheck?>(this);
            if (value is not null) { if (add) items.Add(value); else items[items.IndexOf(selected)] = value; }
        }
        var add = Ui.Button("Добавить проверку"); add.Click += async (_, _) => await Edit(true);
        var edit = Ui.Button("Изменить", "quiet"); edit.Click += async (_, _) => await Edit(false);
        var delete = Ui.Button("Удалить", "quiet", () => { if (list.SelectedItem is SqlCheck value) items.Remove(value); });
        list.DoubleTapped += async (_, _) => await Edit(false); _commit = () => _model = _model with { Checks = items.ToList() };
        return Ui.Scroll(Ui.Stack(16, Ui.Label("Быстрый ответ ещё должен быть правильным", 20, Ui.Text, true),
            Ui.Label("Проверяйте число записей, отсутствие потерянных связей, сохранение сумм и свои бизнес-условия. Проверки выполняются до нагрузки и после каждого интервала, вне времени измеряемых операций.", 13), list, Actions(add, edit, delete)));
    }
    private Control Options()
    {
        var json = WorkloadForm.Text("", true, 300);
        var refresh = Ui.Button("Показать JSON", "quiet", () => { try { json.Text = JsonSerializer.Serialize(Read(), Json.Pretty); } catch (Exception error) { _status.Text = error.Message; } });
        var apply = Ui.Button("Применить JSON", "quiet", () =>
        {
            try
            {
                var value = JsonSerializer.Deserialize<SqlWorkload>(json.Text ?? "", Json.Options) ?? throw new InvalidDataException("Пустой JSON."); value.Validate();
                _model = value; _name.Text = value.Name; _description.Text = value.Description; _mode.SelectedIndex = (int)value.Mode;
                _reset.IsChecked = value.ResetBetweenOperations; _prepare.IsChecked = value.PrepareCommands; _timeout.Text = value.CommandTimeoutSeconds.ToString(); _status.Text = "JSON применён. Вернитесь к таблицам или операциям.";
            }
            catch (Exception error) { _status.Text = error.Message; }
        });
        _commit = () => { };
        var storage = Ui.Label(_storageRoot ?? "По умолчанию — данные приложения BenchForge", 12);
        var chooseStorage = Ui.Button("Выбрать диск / папку для больших файлов", "quiet");
        chooseStorage.Click += async (_, _) =>
        {
            try
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new() { Title = "Где хранить копии импортированных данных", AllowMultiple = false });
                if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) { _storageRoot = path; storage.Text = path; }
            }
            catch (Exception error) { _status.Text = error.Message; }
        };
        return Ui.Scroll(Ui.Stack(14, Ui.Field("Описание", _description), _reset, _prepare, Ui.Field("Таймаут одной SQL-команды, с", _timeout),
            chooseStorage, storage,
            Ui.Label("Mix выполняет смесь с заданными весами. Separate измеряет операции отдельно. SeparateAndMix выполняет оба варианта. Этапы одного графика сохраняют общее состояние данных.", 12),
            new Expander { Header = "Полное описание JSON", Content = Ui.Stack(10, Actions(refresh, apply), json) }));
    }
    private async Task ProbeAsync(Button button)
    {
        if (_importing is not null) { _status.Text = "Завершите импорт перед пробным запуском."; return; }
        if (_probe is not null) { _probe.Cancel(); return; }
        try
        {
            var workload = Read(); workload.Validate();
            using var stop = new CancellationTokenSource(); _probe = stop; _body.IsEnabled = false; button.Content = "Остановить пробу";
            var config = new BenchmarkConfig { CustomWorkload = workload, WorkloadDirectory = _directory, DatasetRows = [100], Clients = [1], Repeats = 1, WarmupSeconds = 0, DurationSeconds = .15, BatchSize = 100 };
            _status.Text = "Проба SQLite: 100 базовых строк; фиксированные таблицы и файлы загружаются целиком…";
            var reports = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BenchForge", "preview-reports");
            var result = await Task.Run(() => new ExperimentRunner(update => { if (update.Type == "log") Dispatcher.UIThread.Post(() => _status.Text = update.Message); }).RunAsync(config, reports, stop.Token));
            _status.Text = result.Report.Status == "completed" ? $"Проба прошла: {result.Report.Results.Count} измерений, ошибок нет. Сценарий можно применять." :
                "Проба: " + result.Report.Status + " · " + string.Join("; ", result.Report.Failures.Concat(result.Report.Results.SelectMany(row => row.ErrorExamples.Keys))).Trim();
        }
        catch (Exception error) { _status.Text = error.Message; }
        finally { _probe = null; _body.IsEnabled = true; button.Content = "Пробный запуск SQLite"; }
    }
    public async Task VerifyPagesAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        for (var page = 0; page < _tabs.Length; page++)
        {
            GoTo(page); await Task.Delay(200);
            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Avalonia.Vector(96, 96));
            bitmap.Render(this); bitmap.Save(Path.Combine(directory, "workload-editor-" + page + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        var read = Read(); read.Validate();
        if (read.Fingerprint != _originalFingerprint) throw new InvalidOperationException("Editor changed workload unexpectedly");
    }
}

internal static class WorkloadPanelExtensions
{
    public static StackPanel AddActions(this StackPanel panel, IEnumerable<Control> children) { foreach (var child in children) panel.Children.Add(child); return panel; }
}
