using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using MeshCoreMessenger.Desktop.Views;
using Microsoft.Extensions.Logging;

namespace MeshCoreMessenger.Desktop.Lifecycle;

/// <summary>Owns only the native tray and exit routing; hiding never stops application services.</summary>
internal sealed class DesktopTrayLifecycle : IDisposable
{
    private readonly Application _application;
    private readonly TrayIcon? _icon;

    internal DesktopTrayLifecycle(Application application, IClassicDesktopStyleApplicationLifetime desktop,
        MainWindow window, DesktopActivationCoordinator activation, ILogger logger)
    {
        _application = application;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        window.ConfigureTrayLifecycle(() => IsAvailable, () => desktop.Shutdown());
        try
        {
            var open = new AsyncRelayCommand(async () =>
            {
                try { await activation.RequestAsync(); }
                catch (Exception exception) { logger.LogWarning(exception, "Could not open the window from the tray."); }
            });
            var menu = new NativeMenu();
            menu.Items.Add(new NativeMenuItem("Открыть") { Command = open });
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(new NativeMenuItem("Выйти") { Command = new RelayCommand(window.RequestExit) });
            _icon = new TrayIcon
            {
                Icon = CreateIcon(), Menu = menu,
                Command = open, ToolTipText = "MeshCoreMessenger", IsVisible = true,
            };
            MacOSProperties.SetIsTemplateIcon(_icon, true);
            TrayIcon.SetIcons(application, new TrayIcons { _icon });
            // Both supported targets export a native tray menu. A null exporter means no safe return path.
            if (!IsAvailable) logger.LogWarning("Native tray is unavailable; the window will not be hidden.");
        }
        catch (Exception exception)
        {
            _icon?.Dispose();
            logger.LogWarning(exception, "Could not create the native tray; the window will not be hidden.");
        }
    }

    private static WindowIcon CreateIcon()
    {
        using var resource = AssetLoader.Open(new Uri("avares://MeshCoreMessenger.Desktop/Assets/Icons/chats.png"));
        if (OperatingSystem.IsMacOS()) return new WindowIcon(resource);
        // Reuse the application's chat glyph on a contrasting background in either Windows taskbar theme.
        using var glyph = new Bitmap(resource);
        using var bitmap = new RenderTargetBitmap(new PixelSize(64, 64), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            context.DrawEllipse(new SolidColorBrush(Color.Parse("#287BC1")), null, new Point(32, 32), 31, 31);
            var bounds = new Rect(10, 10, 44, 44);
            using (context.PushOpacityMask(new ImageBrush(glyph) { Stretch = Stretch.Fill }, bounds))
                context.DrawRectangle(Brushes.White, null, bounds);
        }
        using var encoded = new MemoryStream();
        bitmap.Save(encoded, PngBitmapEncoderOptions.Default);
        encoded.Position = 0;
        return new WindowIcon(encoded);
    }

    internal bool IsAvailable => _icon is { IsVisible: true, NativeMenuExporter: not null };
    internal NativeMenu? Menu => _icon?.Menu;

    public void Dispose()
    {
        TrayIcon.SetIcons(_application, null);
        _icon?.Dispose();
    }
}
