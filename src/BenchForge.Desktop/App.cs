using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace BenchForge.Desktop;

public sealed class App : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://BenchForge.Desktop/")) { Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml") });
        Ui.Styles(this);
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow(); desktop.MainWindow = window;
            if (desktop.Args?.Contains("--ui-smoke") == true)
                window.Opened += async (_, _) =>
                {
                    try { await window.VerifyUiAsync(); desktop.Shutdown(0); }
                    catch (Exception e) { Console.Error.WriteLine(e); desktop.Shutdown(1); }
                };
            else if (desktop.Args?.Contains("--ui-cancel-smoke") == true)
                window.Opened += async (_, _) =>
                {
                    try { await window.VerifyCancelAsync(); desktop.Shutdown(0); }
                    catch (Exception e) { Console.Error.WriteLine(e); desktop.Shutdown(1); }
                };
            else if (desktop.Args is { } openArgs && Array.IndexOf(openArgs, "--open-report") is var openIndex && openIndex >= 0 && openIndex + 1 < openArgs.Length)
                window.Opened += async (_, _) =>
                {
                    try { await window.OpenSavedReportAsync(openArgs[openIndex + 1]); }
                    catch (Exception e) { Console.Error.WriteLine(e); }
                };
            else if (desktop.Args is { } args && Array.IndexOf(args, "--preview-report") is var index && index >= 0 && index + 1 < args.Length)
                window.Opened += async (_, _) =>
                {
                    try { await window.VerifyDashboardAsync(args[index + 1]); desktop.Shutdown(0); }
                    catch (Exception e) { Console.Error.WriteLine(e); desktop.Shutdown(1); }
                };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
