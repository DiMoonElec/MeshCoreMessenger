using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MeshCoreMessenger.Desktop.Presentation;
using MeshCoreMessenger.Desktop.Views.Chat;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed partial class UiWorkspaceIntegrationTests
{
    public static class NativeSendAudit
    {
        public static async Task RunAsync()
        {
            var output = Path.Combine(Path.GetTempPath(), "meshcore-d5-send");
            Directory.CreateDirectory(output);
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                foreach (var width in new[] { 420, 960 })
                {
                    await using var w = await Workspace.CreateAsync();
                    await w.Root.StopAsync();
                    var sender = new UiChannelSender(w);
                    w.Root = w.CreateRoot(dispatcher: new NativeDispatcher(), messageService: sender);
                    await w.Root.LoadAsync();
                    await OnlineSender(w, multipleSlots: true);
                    var workspace = w.Root.Chats.Public;
                    var composer = workspace.Composer;
                    var view = new ConversationView { DataContext = workspace };
                    var window = new Window
                    {
                        Content = view,
                        Width = width,
                        Height = 700,
                        RequestedThemeVariant = theme,
                        Title = "D5 — эмуляция отправки, временная SQLite"
                    };
                    window.Show(); window.Activate();
                    try
                    {
                        await SettleAsync(window);
                        await workspace.Navigation.History.JumpToLatestAsync();
                        await SettleAsync(window);
                        composer.Text = "Тест 👋";
                        await SettleAsync(window);
                        var control = view.GetVisualDescendants().OfType<ComposerView>().Single();
                        var button = control.GetVisualDescendants().OfType<Button>().Single();
                        var choice = control.GetVisualDescendants().OfType<ComboBox>().Single();
                        if (button.IsEnabled || !choice.IsVisible) throw new InvalidOperationException("Ambiguous slot enabled send.");
                        choice.SelectedItem = (byte)0;
                        await UntilAsync(() => composer.CanSend);
                        await SettleAsync(window);
                        if (!button.IsEnabled) throw new InvalidOperationException($"Selected slot did not enable send: slot={composer.SelectedSlot}, choice={choice.SelectedItem}, readiness={composer.Readiness}, text={composer.Text}, edit={composer.CanEdit}, capture={composer.SendCapture is not null}, status={composer.StatusLine}");
                        var editor = control.FindControl<TextBox>("MessageInput")!;
                        editor.Focus();
                        var presenter = editor.GetVisualDescendants().OfType<TextPresenter>().Single();
                        presenter.PreeditText = "я";
                        editor.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
                        if (sender.Calls != 0) throw new InvalidOperationException("IME Enter transmitted text.");
                        presenter.PreeditText = null;
                        editor.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, KeyModifiers = KeyModifiers.Shift });
                        if (sender.Calls != 0) throw new InvalidOperationException("Shift+Enter transmitted text.");
                        composer.Text = "Тест 👋";
                        editor.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
                        if (composer.SendCommand.ExecutionTask is { } send) await send;
                        await UntilAsync(() => workspace.Navigation.History.Messages.Any(m => m.Body == "Тест 👋" && m.Presentation.State == MessageSendDisplayState.AcceptedByNode));
                        await SettleAsync(window);
                        var bubble = workspace.Navigation.History.Messages.Single(m => m.Body == "Тест 👋");
                        if (sender.Calls != 1 || bubble.Presentation.State != MessageSendDisplayState.AcceptedByNode || composer.Text != "")
                            throw new InvalidOperationException("Native send/commit/draft/status audit failed.");
                        var status = control.GetVisualDescendants().OfType<TextBlock>().Single(c => c.Classes.Contains("composer-status"));
                        if (status.Text != composer.StatusLine || control.Bounds.Width > window.ClientSize.Width)
                            throw new InvalidOperationException("Composer binding/layout failed.");
                        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height));
                        bitmap.Render(window); bitmap.Save(Path.Combine(output, $"{theme}-{width}.png"), PngBitmapEncoderOptions.Default);
                        Console.WriteLine($"D5 native {theme} {width}: Enter=1 TX, Shift/IME=0 TX; one AcceptedByNode bubble; draft cleared; slot/bindings OK");
                    }
                    finally { await w.Root.StopAsync(); window.Close(); }
                }
            Console.WriteLine($"D5 screenshots: {output}");
        }
    }
}
