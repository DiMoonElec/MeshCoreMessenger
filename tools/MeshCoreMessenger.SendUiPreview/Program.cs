using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;

namespace MeshCoreMessenger.SendUiPreview;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args) => AppBuilder.Configure<PreviewApp>()
        .UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
}

// Deliberately does not instantiate Desktop.App, DI, paths, SQLite or any session.
public sealed class PreviewApp : Application
{
    public override void Initialize()
    {
        Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://MeshCoreMessenger.Desktop/"))
        {
            Source = new Uri("avares://MeshCoreMessenger.Desktop/Styles/ThemeResources.axaml"),
        });
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new PreviewWindow { DataContext = new PreviewViewModel() };
            desktop.MainWindow = window;
            if (desktop.Args?.Contains("--audit") == true)
            {
                window.Opened += async (_, _) =>
                {
                    try { await PreviewAudit.RunAsync(window); desktop.Shutdown(0); }
                    catch (Exception error) { Console.Error.WriteLine(error); desktop.Shutdown(1); }
                };
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
