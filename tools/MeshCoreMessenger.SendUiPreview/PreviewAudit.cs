using Avalonia;
using Avalonia.Controls;
using Avalonia.Logging;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeshCoreMessenger.Desktop.ViewModels;
using MeshCoreMessenger.Desktop.Views.Chat;

namespace MeshCoreMessenger.SendUiPreview;

internal static class PreviewAudit
{
    public static async Task RunAsync(PreviewWindow window)
    {
        var previousSink = Logger.Sink;
        var sink = new BindingLogSink();
        Logger.Sink = sink;
        try
        {
            var tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
            var output = Path.Combine(Path.GetTempPath(), "meshcore-d1-preview");
            Directory.CreateDirectory(output);
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            foreach (var width in new[] { 420, 960 })
            foreach (var tab in new[] { 0, 1 })
            {
                window.RequestedThemeVariant = theme;
                window.Width = width;
                tabs.SelectedIndex = tab;
                await Task.Delay(150);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                window.UpdateLayout();
                var messages = window.GetVisualDescendants().OfType<MessageView>().ToArray();
                Require(messages.Length >= 5, "Preview did not populate production message controls.");
                foreach (var view in messages)
                {
                    var model = (HistoryMessageListItem)view.DataContext!;
                    var children = view.GetVisualDescendants().OfType<Control>().ToArray();
                    var bubble = children.OfType<Border>().Single(c => c.Classes.Contains("message-bubble"));
                    var metadata = children.OfType<TextBlock>().Single(c => c.Classes.Contains("message-metadata"));
                    Require(metadata.Text == model.MetadataText, "Metadata binding failed.");
                    Require(metadata.TranslatePoint(default, view)!.Value.Y >=
                        bubble.TranslatePoint(default, view)!.Value.Y + bubble.Bounds.Height, "Metadata is inside the bubble.");
                    Require(bubble.Background is not null && metadata.Foreground is not null, "Theme resource is missing.");
                    var panel = children.OfType<StackPanel>().Single(c => c.Classes.Contains("message"));
                    Require(panel.HorizontalAlignment == (model.IsOutgoing ? Avalonia.Layout.HorizontalAlignment.Right : Avalonia.Layout.HorizontalAlignment.Left),
                        "Message direction alignment failed.");
                    panel.ContextMenu!.Open(panel);
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                    var menus = panel.ContextMenu.Items.OfType<MenuItem>().ToArray();
                    Require(menus[2].IsVisible == model.RetryVisible && menus[2].IsEnabled == model.CanRetry,
                        "Retry availability binding failed.");
                    panel.ContextMenu.Close();
                }
                var composer = window.GetVisualDescendants().OfType<ComposerView>().Single();
                Require(!composer.GetVisualDescendants().OfType<Button>().Single().IsEnabled, "Preview enabled real send.");
                using var image = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
                image.Render(window);
                image.Save(Path.Combine(output, $"{theme}-{width}-{tab}.png"), PngBitmapEncoderOptions.Default);
                Console.WriteLine($"PASS {theme}, {width} DIP, {(tab == 0 ? "public" : "private")}: {messages.Length} messages");
            }
            // Exercise the actual production menu handler and its cancellation/confirmation path.
            var retryView = window.GetVisualDescendants().OfType<MessageView>()
                .First(v => ((HistoryMessageListItem)v.DataContext!).CanRetry);
            var retryMessage = (HistoryMessageListItem)retryView.DataContext!;
            var id = retryMessage.Id;
            var before = retryMessage.Presentation;
            var panelForRetry = retryView.GetVisualDescendants().OfType<StackPanel>().Single(c => c.Classes.Contains("message"));
            var menu = panelForRetry.ContextMenu!;
            menu.Open(panelForRetry);
            var retryItem = menu.Items.OfType<MenuItem>().Last();
            retryItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Task.Delay(100);
            var dialog = window.OwnedWindows.OfType<MessageActionDialog>().Single();
            Require(dialog.IsConfirmation, "Menu did not request confirmation.");
            Require(dialog.ActualThemeVariant == window.ActualThemeVariant, "Dialog lost owner theme.");
            dialog.Close(false);
            await Task.Delay(100);
            Require(retryMessage.Presentation == before, "Cancelling retry changed metadata.");
            menu.Open(panelForRetry);
            retryItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Task.Delay(100);
            window.OwnedWindows.OfType<MessageActionDialog>().Single().Close(true);
            await Task.Delay(100);
            Require(retryMessage.Id == id && retryMessage.Presentation.AttemptNumber == before.AttemptNumber + 1,
                "Confirmed retry did not update the same message.");
            Require(!retryMessage.RetryVisible, "Retry remained visible after the business presentation disabled it.");
            menu.Close();
            Require(sink.Errors.Count == 0, string.Join("\n", sink.Errors));
            Console.WriteLine($"PASS resources/bindings, retry cancel/confirm and same bubble. Screenshots: {output}");
        }
        finally { Logger.Sink = previousSink; }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class BindingLogSink : ILogSink
    {
        public List<string> Errors { get; } = [];
        public bool IsEnabled(LogEventLevel level, string area) => area == "Binding" && level >= LogEventLevel.Warning;
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) => Errors.Add(messageTemplate);
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) =>
            Errors.Add($"{messageTemplate}: {string.Join(", ", propertyValues)}");
    }
}
