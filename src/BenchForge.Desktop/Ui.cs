using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;

namespace BenchForge.Desktop;

// A small shared visual language. Views only decide which information to show.
public static class Ui
{
    public const string Background = "#141414", Surface = "#1b1b1c", Raised = "#242425", Line = "#363536";
    public const string Text = "#f3f0ee", Muted = "#aaa6a4", Accent = "#ff6966", Green = "#81d2af", Amber = "#efc683";
    public const string Input = "#181819", Hover = "#2b2a2c", Selected = "#322326", SelectedLine = "#754346";
    public const string SelectedText = "#ffaca8", Danger = "#ffaca8";
    // Fade the opacity of the dark hover brush instead of blending with Transparent.
    private static readonly IBrush QuietBackground = new ImmutableSolidColorBrush(Color.Parse(Hover), 0);
    public static IBrush B(string color) => Brush.Parse(color);
    public static readonly ControlTheme ButtonTheme = new(typeof(Button))
    {
        Setters =
        {
            new Setter(TemplatedControl.TemplateProperty, new FuncControlTemplate<Button>((button, scope) =>
            {
                var presenter = new ContentPresenter { Name = "PART_ContentPresenter" };
                scope.Register("PART_ContentPresenter", presenter);
                foreach (var (property, name) in new (AvaloniaProperty, string)[] {
                    (ContentPresenter.ContentProperty, "Content"), (ContentPresenter.ContentTemplateProperty, "ContentTemplate"),
                    (ContentPresenter.BackgroundProperty, "Background"), (ContentPresenter.BorderBrushProperty, "BorderBrush"),
                    (ContentPresenter.BorderThicknessProperty, "BorderThickness"), (ContentPresenter.CornerRadiusProperty, "CornerRadius"),
                    (ContentPresenter.PaddingProperty, "Padding"), (ContentPresenter.HorizontalContentAlignmentProperty, "HorizontalContentAlignment"),
                    (ContentPresenter.VerticalContentAlignmentProperty, "VerticalContentAlignment") })
                    presenter.Bind(property, new Binding(name) { Source = button });
                return presenter;
            }))
        }
    };
    public static TextBlock Label(string text, double size = 14, string color = Muted, bool bold = false) => new()
    { Text = text, FontSize = size, Foreground = B(color), FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, TextWrapping = TextWrapping.Wrap };
    public static StackPanel Stack(double spacing = 12, params Control[] children)
    { var panel = new StackPanel { Spacing = spacing }; foreach (var child in children) panel.Children.Add(child); return panel; }
    public static Border Card(Control content, double padding = 24) => new()
    { Background = B(Surface), BorderBrush = B(Line), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(11), Padding = new Thickness(padding), Child = content };
    public static Border Rule() => new() { Height = 1, Background = B(Line), HorizontalAlignment = HorizontalAlignment.Stretch };
    public static Border Badge(string text, string color = Muted) => new()
    { Child = Label(text, 12, color), Background = B(Raised), CornerRadius = new CornerRadius(6), Padding = new Thickness(9, 5) };
    public static Button Button(string text, string tone = "normal", Action? action = null)
    {
        var button = new Button { Content = text, Theme = ButtonTheme, MinHeight = 40, Padding = new Thickness(16, 10), CornerRadius = new CornerRadius(8),
            FontSize = 14, FontWeight = FontWeight.Medium, BorderThickness = new Thickness(1), HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center, Cursor = new Cursor(StandardCursorType.Hand), Transitions = new Transitions {
                new BrushTransition { Property = TemplatedControl.BackgroundProperty, Duration = TimeSpan.FromMilliseconds(100), Easing = new CubicEaseOut() } } };
        button.Classes.Add(tone); Paint(button);
        button.PropertyChanged += (_, change) =>
        {
            if (change.Property == InputElement.IsPointerOverProperty || change.Property == Avalonia.Controls.Button.IsPressedProperty ||
                change.Property == InputElement.IsFocusedProperty || change.Property == InputElement.IsEffectivelyEnabledProperty)
                Paint(button);
        };
        AutomationProperties.SetName(button, text);
        if (action is not null) button.Click += (_, _) => action(); return button;
    }
    private static void Paint(Button button)
    {
        var selected = button.Classes.Contains("selected");
        var primary = button.Classes.Contains("primary");
        var quiet = button.Classes.Contains("quiet") || button.Classes.Contains("nav");
        var danger = button.Classes.Contains("danger");
        var enabled = button.IsEffectivelyEnabled;
        var hover = enabled && button.IsPointerOver;
        var pressed = enabled && button.IsPressed;
        button.Opacity = enabled ? 1 : .4;
        button.BorderBrush = button.IsFocused ? B(Accent) : selected ? B(SelectedLine) : primary || quiet ? Brushes.Transparent : B(Line);
        if (primary)
        {
            button.Background = B(pressed ? "#e85e5b" : hover ? "#ff7773" : Accent);
            button.Foreground = B("#241415");
            return;
        }

        button.Background = selected ? B(pressed ? "#291d20" : hover ? "#39282b" : Selected)
            : pressed ? B(Raised) : hover ? B(Hover) : quiet ? QuietBackground : B(Surface);
        button.Foreground = B(selected ? SelectedText : danger ? Danger : quiet ? Muted : Text);
    }
    public static void Select(Button button, bool selected)
    { button.Classes.Set("selected", selected); Paint(button); }
    public static TransitioningContentControl Presenter() => new()
    { PageTransition = new CrossFade(TimeSpan.FromMilliseconds(170)) { FadeInEasing = new CubicEaseOut(), FadeOutEasing = new CubicEaseOut() }, ClipToBounds = true };
    public static ScrollViewer Scroll(Control content) => new() { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    public static Control Field(string title, Control input, string? hint = null)
    {
        input.HorizontalAlignment = HorizontalAlignment.Stretch;
        var label = Label(title, 13, Text); if (hint is not null) ToolTip.SetTip(label, hint);
        return Stack(8, label, input);
    }
    public static Grid Columns(string definitions, params Control[] children)
    { var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(definitions) }; for (var i = 0; i < children.Length; i++) { Grid.SetColumn(children[i], i * 2); grid.Children.Add(children[i]); } return grid; }
    public static DockPanel Heading(string title, string subtitle, Control? action = null)
    {
        var panel = new DockPanel { Margin = new Thickness(0, 0, 0, 28) };
        if (action is not null) { DockPanel.SetDock(action, Dock.Right); action.VerticalAlignment = VerticalAlignment.Center; panel.Children.Add(action); }
        panel.Children.Add(Stack(6, Label(title, 28, Text, true), Label(subtitle))); return panel;
    }
    public static void Styles(Application app)
    {
        app.Styles.Add(new Style(s => s.OfType<TextBox>()) { Setters = {
            new Setter(TemplatedControl.BackgroundProperty, B(Input)), new Setter(TemplatedControl.BorderBrushProperty, B(Line)),
            new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(1)), new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(8)),
            new Setter(TemplatedControl.FontSizeProperty, 14d), new Setter(TemplatedControl.MinHeightProperty, 42d), new Setter(TemplatedControl.PaddingProperty, new Thickness(12, 10)) } });
        app.Styles.Add(new Style(s => s.OfType<ComboBox>()) { Setters = {
            new Setter(TemplatedControl.BackgroundProperty, B(Raised)), new Setter(TemplatedControl.BorderBrushProperty, B(Line)),
            new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(8)), new Setter(TemplatedControl.MinHeightProperty, 40d),
            new Setter(TemplatedControl.FontSizeProperty, 13d) } });
        app.Styles.Add(new Style(s => s.OfType<Expander>()) { Setters = {
            new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent), new Setter(TemplatedControl.BorderBrushProperty, B(Line)),
            new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(8)), new Setter(TemplatedControl.HorizontalAlignmentProperty, HorizontalAlignment.Stretch) } });
        app.Resources["SystemAccentColor"] = Color.Parse(Accent);
        app.Resources["SystemAccentColorLight1"] = Color.Parse(SelectedText);
        app.Resources["SystemAccentColorDark1"] = Color.Parse("#d95452");
        app.Resources["SystemAccentColorDark2"] = Color.Parse(SelectedLine);
        app.Resources["SystemAccentColorDark3"] = Color.Parse(Selected);
        app.Resources["TextControlBackground"] = B(Input);
        app.Resources["TextControlBackgroundPointerOver"] = B(Surface);
        app.Resources["TextControlBackgroundFocused"] = B(Input);
        app.Resources["ComboBoxBackground"] = B(Raised);
        app.Resources["ComboBoxBackgroundPointerOver"] = B(Hover);
        app.Resources["ComboBoxBorderBrush"] = B(Line);
    }
}
