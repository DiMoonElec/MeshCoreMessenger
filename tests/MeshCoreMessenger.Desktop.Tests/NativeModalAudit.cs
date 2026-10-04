using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MeshCoreMessenger.Desktop.ViewModels;
using MeshCoreMessenger.Desktop.Views;
using MeshCoreMessenger.Desktop.Views.Dialogs;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed partial class UiWorkspaceIntegrationTests
{
    public static class NativeModalAudit
    {
        public static async Task RunAsync()
        {
            var output = Path.Combine(Path.GetTempPath(), "meshcore-modal-cards");
            Directory.CreateDirectory(output);
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            foreach (var width in new[] { 420, 960 })
            {
                await using var w = await Workspace.CreateAsync();
                await w.Root.StopAsync();
                w.Root = w.CreateRoot(dispatcher: new NativeDispatcher());
                await w.Root.LoadAsync();
                w.Root.Shell.SelectSection(ShellSection.PrivateChats);
                var window = new MainWindow { DataContext = w.Root, MinWidth = 0, Width = width, Height = 540, RequestedThemeVariant = theme };
                window.Show(); window.Activate();
                try
                {
                    await SettleAsync(window);
                    await w.Root.Chats.Private.SelectConversationAsync(w.Root.Chats.Private.SelectedConversation);
                    await SettleAsync(window);
                    var input = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "MessageInput" && t.IsEffectivelyVisible);
                    input.Focus();
                    w.Root.Chats.Private.ContactDetailsCommand.Execute(null);
                    await UntilAsync(() => w.Root.Modal.Active is ContactDetailsCardViewModel { HasDetails: true });
                    await SettleAsync(window);
                    var host = window.FindControl<ModalHostView>("ModalHost")!;
                    if (!host.GetVisualDescendants().OfType<ContactDetailsCardView>().Any()) throw new InvalidOperationException("Contact content template was not resolved.");
                    var close = host.FindControl<Button>("CloseButton")!;
                    var card = host.FindControl<Border>("Card")!;
                    if (!close.IsFocused || input.IsEffectivelyEnabled) throw new InvalidOperationException("Modal focus or background isolation failed.");
                    for (var step = 0; step < 20; step++)
                    {
                        var focused = window.FocusManager!.GetFocusedElement() as InputElement;
                        focused!.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Tab,
                            KeyModifiers = step < 10 ? KeyModifiers.None : KeyModifiers.Shift });
                        await SettleAsync(window);
                        if (window.FocusManager.GetFocusedElement() is not Visual focus || !focus.GetVisualAncestors().Contains(host))
                            throw new InvalidOperationException("Tab focus escaped the modal.");
                    }
                    var position = card.TranslatePoint(default, window)!.Value;
                    if (card.Bounds.Width <= 100 || card.Bounds.Height <= 100 || position.X < 19 || position.Y < 19 ||
                        position.X + card.Bounds.Width > window.ClientSize.Width - 19 || position.Y + card.Bounds.Height > window.ClientSize.Height - 19)
                        throw new InvalidOperationException($"Card exceeds viewport: {card.Bounds}, at {position}, window {window.ClientSize}");
                    using (var bitmap = new RenderTargetBitmap(new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height)))
                    {
                        bitmap.Render(window);
                        bitmap.Save(Path.Combine(output, $"contact-{theme}-{width}.png"), PngBitmapEncoderOptions.Default);
                    }
                    window.Height = 440;
                    await SettleAsync(window);
                    if (card.Bounds.Height > window.ClientSize.Height - 39) throw new InvalidOperationException("Card failed to adapt after resizing.");
                    close.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                    await SettleAsync(window);
                    if (w.Root.Modal.IsOpen || !input.IsFocused || !input.IsEffectivelyEnabled) throw new InvalidOperationException("Escape/focus restoration failed.");
                    w.Root.Chats.Private.ContactDetailsCommand.Execute(null);
                    await SettleAsync(window);
                    typeof(Button).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(close, null);
                    await SettleAsync(window);
                    if (w.Root.Modal.IsOpen) throw new InvalidOperationException("Close button failed.");
                    Console.WriteLine($"Modal/{theme}/{width}: content, bounds, resize, focus, Escape and close passed.");
                }
                finally { window.Close(); await w.Root.StopAsync(); }
            }
            Console.WriteLine($"Modal screenshots: {output}");
        }
    }
}
