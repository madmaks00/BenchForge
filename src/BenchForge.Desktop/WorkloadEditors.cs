using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BenchForge.Contracts;

namespace BenchForge.Desktop;

internal static class WorkloadForm
{
    public static TextBox Text(string? value = "", bool code = false, double height = 0) => new()
    { Text = value, AcceptsReturn = height > 0, Height = height > 0 ? height : double.NaN, TextWrapping = code ? TextWrapping.NoWrap : TextWrapping.Wrap,
        FontFamily = new FontFamily(code ? "Cascadia Code, Consolas, monospace" : "Segoe UI, sans-serif"), FontSize = 13 };
    public static ComboBox Enum<T>(T selected) where T : struct, Enum => new() { ItemsSource = System.Enum.GetValues<T>(), SelectedItem = selected };
    public static T Selected<T>(ComboBox box) where T : struct, Enum => box.SelectedItem is T value ? value : throw new ArgumentException("Выберите значение.");
    public static long Long(TextBox box, string name) => long.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : throw new ArgumentException("Проверьте «" + name + "».");
    public static decimal Decimal(TextBox box, string name) => decimal.TryParse(box.Text?.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : throw new ArgumentException("Проверьте «" + name + "».");
    public static long? Optional(TextBox box, string name) => string.IsNullOrWhiteSpace(box.Text) ? null : Long(box, name);
    public static string? Optional(TextBox box) => string.IsNullOrWhiteSpace(box.Text) ? null : box.Text;
    public static Window Dialog(string title, Control content, Func<object> read, double width = 620, double height = 690)
    {
        var window = new Window { Title = title, Icon = BrandIcons.WindowIcon(), Width = width, Height = height, MinWidth = 480, MinHeight = 430,
            Background = Ui.B(Ui.Background), Foreground = Ui.B(Ui.Text), WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var status = Ui.Label("", 12, Ui.Danger); var apply = Ui.Button("Применить", "primary");
        apply.Click += (_, _) => { try { window.Close(read()); } catch (Exception error) { status.Text = error.Message; } };
        var close = Ui.Button("Отмена", "quiet", () => window.Close());
        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto"), Margin = new Thickness(24) };
        root.Children.Add(Ui.Scroll(content)); Grid.SetRow(status, 1); status.Margin = new Thickness(0, 12, 0, 12); root.Children.Add(status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { close, apply } };
        Grid.SetRow(actions, 2); root.Children.Add(actions); window.Content = root; return window;
    }

    public static Window ValueDialog(ValueDefinition definition, bool parameter)
    {
        var name = Text(definition.Name); var type = Enum(definition.Type); var generator = Enum(definition.Generator);
        var min = Text(definition.Min.ToString(CultureInfo.InvariantCulture)); var max = Text(definition.Max.ToString(CultureInfo.InvariantCulture));
        var length = Text(definition.Length.ToString()); var scale = Text(definition.Scale.ToString()); var prefix = Text(definition.Prefix);
        var reference = Text(definition.Reference); var constant = Text(definition.Constant); var choices = Text(string.Join('\n', definition.Choices), false, 100);
        var sampleColumn = Text(definition.SampleColumn); var sampleSize = Text(definition.SampleSize.ToString());
        var nullable = new CheckBox { Content = "Допускать NULL", IsChecked = definition.Nullable };
        var primary = new CheckBox { Content = "Первичный ключ", IsChecked = definition.PrimaryKey, IsVisible = !parameter };
        var unique = new CheckBox { Content = "Уникальное значение", IsChecked = definition.Unique, IsVisible = !parameter };
        var nulls = Text(definition.NullPercent.ToString(CultureInfo.InvariantCulture));
        primary.IsCheckedChanged += (_, _) => { if (primary.IsChecked == true) { nullable.IsChecked = false; nulls.Text = "0"; } nullable.IsEnabled = primary.IsChecked != true; };
        var range = Ui.Columns("*,12,*", Ui.Field("Минимум / начало Sequence", min), Ui.Field("Максимум", max));
        var text = Ui.Stack(12, Ui.Columns("*,12,*", Ui.Field("Длина случайной части", length), Ui.Field("Знаков после запятой", scale)), Ui.Field("Префикс; {row}, {sequence}, {key}, {client}", prefix));
        var choiceField = Ui.Field("Значения — каждое с новой строки", choices);
        var referenceField = Ui.Field("Имя таблицы с ключами 1..N", reference);
        var sampleFields = Ui.Columns("*,12,*", Ui.Field("Столбец для Sample", sampleColumn), Ui.Field("Максимум значений в памяти", sampleSize));
        var constantField = Ui.Field("Постоянное значение; пустое поле — NULL", constant);
        void Visibility()
        {
            var selected = Selected<ValueGenerator>(generator);
            range.IsVisible = selected is ValueGenerator.Integer or ValueGenerator.Decimal or ValueGenerator.Sequence or ValueGenerator.Timestamp;
            max.IsEnabled = selected != ValueGenerator.Sequence;
            text.IsVisible = selected is ValueGenerator.Text or ValueGenerator.Decimal; choiceField.IsVisible = selected == ValueGenerator.Choice;
            referenceField.IsVisible = selected is ValueGenerator.Reference or ValueGenerator.Sample; sampleFields.IsVisible = selected == ValueGenerator.Sample;
            constantField.IsVisible = selected == ValueGenerator.Constant;
        }
        generator.SelectionChanged += (_, _) => Visibility(); Visibility();
        return Dialog(parameter ? "Параметр запроса" : "Поле таблицы", Ui.Stack(14,
            Ui.Field("Имя", name), Ui.Columns("*,12,*", Ui.Field("Тип", type), Ui.Field("Генератор", generator)), range, text, choiceField, referenceField, sampleFields, constantField,
            Ui.Columns("*,12,*", nullable, Ui.Field("Вероятность NULL, %", nulls)), Ui.Columns("*,12,*", primary, unique),
            Ui.Label("Reference выбирает ключ 1..N. Sample выбирает реальные ненулевые значения столбца из входных данных, включая UUID и строки; ограниченная выборка держится в памяти. Для полей импортированных файлов важны имя, тип и NULL; генератор не используется.", 12)), () =>
        {
            var value = definition with { Name = name.Text?.Trim() ?? "", Type = Selected<SqlValueType>(type), Generator = Selected<ValueGenerator>(generator),
                Min = Decimal(min, "Минимум"), Max = Decimal(max, "Максимум"), Length = checked((int)Long(length, "Длина")), Scale = checked((int)Long(scale, "Точность")),
                Prefix = prefix.Text ?? "", Reference = Optional(reference)?.Trim(), SampleColumn = sampleColumn.Text?.Trim() ?? "id", SampleSize = checked((int)Long(sampleSize, "Размер выборки")), Constant = Optional(constant), Choices = (choices.Text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(value => value.TrimEnd('\r')).ToList(),
                Nullable = nullable.IsChecked == true, PrimaryKey = primary.IsChecked == true, Unique = unique.IsChecked == true, NullPercent = (double)Decimal(nulls, "NULL, %") };
            if (!SqlWorkload.Identifier(value.Name)) throw new ArgumentException("Имя: латинские буквы, цифры и подчёркивания; до 63 символов.");
            return value;
        });
    }
    public static Window StepDialog(SqlStep step)
    {
        var sql = new SqlTextEditor(step.Command, 220); var result = Enum(step.Result);
        var min = Text(step.MinRows?.ToString()); var max = Text(step.MaxRows?.ToString()); var scalar = Text(step.ExpectedScalar);
        var capture = Text(step.CaptureAs); var type = Enum(step.CaptureType);
        return Dialog("SQL-шаг операции", Ui.Stack(14, sql, Ui.Field("Как получить результат", result),
            Ui.Columns("*,12,*", Ui.Field("Минимум строк; пусто — без проверки", min), Ui.Field("Максимум строк", max)),
            Ui.Field("Ожидаемый Scalar; пусто — без сравнения", scalar),
            new Expander { Header = "Передать результат следующему шагу", Content = Ui.Stack(12, Ui.Field("Имя параметра без @", capture), Ui.Field("Тип значения", type), Ui.Label("Используйте режим Scalar. Следующие шаги могут обратиться к @имя. Все шаги входят в одну измеряемую операцию.", 12)) },
            Ui.Label("Rows читает весь ответ; NonQuery считает затронутые строки; Scalar получает первое значение. Проверки ответа входят в задержку операции.", 12)), () =>
                step with { Command = sql.Read(), Result = Selected<SqlResultMode>(result), MinRows = Optional(min, "Минимум строк"), MaxRows = Optional(max, "Максимум строк"),
                    ExpectedScalar = Optional(scalar), CaptureAs = Optional(capture)?.Trim(), CaptureType = Selected<SqlValueType>(type) }, 700, 760);
    }
    public static Window CheckDialog(SqlCheck check)
    {
        var name = Text(check.Name); var sql = new SqlTextEditor(check.Command, 200); var comparison = Enum(check.Comparison); var expected = Text(check.Expected);
        var before = new CheckBox { Content = "До нагрузки", IsChecked = check.Before }; var after = new CheckBox { Content = "После нагрузки", IsChecked = check.After };
        return Dialog("Проверка целостности", Ui.Stack(14, Ui.Field("Название", name), sql, Ui.Columns("*,12,*", Ui.Field("Сравнение", comparison), Ui.Field("Ожидаемое значение", expected)),
            Ui.Columns("*,12,*", before, after), Ui.Label("Запрос должен вернуть одно число. Ожидание: число, baseRows или rows:имя_таблицы. Параметры: @baseRows, @rows_таблица, @completed, @completed_IDоперации. Проверка выполняется после каждого интервала вне времени запросов; ошибка делает интервал непроверенным.", 12)),
            () => check with { Name = name.Text?.Trim() ?? "", Command = sql.Read(), Comparison = Selected<CheckComparison>(comparison), Expected = expected.Text?.Trim() ?? "0", Before = before.IsChecked == true, After = after.IsChecked == true }, 700, 710);
    }
}

internal sealed class SqlTextEditor : UserControl
{
    private SqlText _value;
    private readonly ComboBox _dialect = new() { ItemsSource = new[] { "Общий SQL", "PostgreSQL", "MySQL", "MariaDB", "SQLite", "SQL Server" }, SelectedIndex = 0 };
    private readonly TextBox _sql;
    private int _index;
    private string _loaded;
    private static readonly TargetKind[] Targets = [TargetKind.PostgreSql, TargetKind.MySql, TargetKind.MariaDb, TargetKind.Sqlite, TargetKind.SqlServer];
    public SqlTextEditor(SqlText value, double height = 200)
    {
        _value = value with { Overrides = new(value.Overrides) }; _loaded = value.Sql; _sql = WorkloadForm.Text(value.Sql, true, height);
        var inherit = Ui.Button("Использовать общий SQL для этого диалекта", "quiet", () =>
        { if (_index > 0) { _value.Overrides.Remove(Targets[_index - 1]); _loaded = _value.Sql; _sql.Text = _loaded; } });
        inherit.IsVisible = false;
        _dialect.SelectionChanged += (_, _) => { Save(); _index = _dialect.SelectedIndex; _loaded = _index == 0 ? _value.Sql : _value.For(Targets[_index - 1]); _sql.Text = _loaded; inherit.IsVisible = _index > 0; };
        Content = Ui.Stack(8, _dialect, _sql, inherit);
    }
    private void Save()
    {
        var text = _sql.Text ?? "";
        if (_index == 0) _value = _value with { Sql = text };
        else if (text != _loaded || _value.Overrides.ContainsKey(Targets[_index - 1])) _value.Overrides[Targets[_index - 1]] = text;
    }
    public SqlText Read() { Save(); return _value; }
}
