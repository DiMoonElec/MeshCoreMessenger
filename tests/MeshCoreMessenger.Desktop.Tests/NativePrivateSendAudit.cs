using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Desktop.Presentation;
using MeshCoreMessenger.Desktop.ViewModels;
using MeshCoreMessenger.Desktop.Views.Chat;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed partial class UiWorkspaceIntegrationTests
{
    public static class NativePrivateSendAudit
    {
        public static async Task RunAsync()
        {
            var output = Path.Combine(Path.GetTempPath(), "meshcore-d6-private-send");
            Directory.CreateDirectory(output);
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                foreach (var width in new[] { 420, 960 })
                {
                    await using var w = await Workspace.CreateAsync(); await w.Root.StopAsync();
                    var sender = new UiChannelSender(w);
                    var session = await OnlineSender(w);
                    var key = Enumerable.Repeat((byte)22, 32).ToArray();
                    await w.Storage.Directories.ApplySnapshotAsync(w.A.NodeId, session,
                        [new(key, "New private peer", 1, 0, new byte[64], Now, 0, 0)],
                        [new(0, "Shared name", w.A.Target.Identity, ChannelAccessKind.Unknown)], DateTimeOffset.UtcNow);
                    w.Root = w.CreateRoot(dispatcher: new NativeDispatcher(), messageService: sender);
                    await w.Root.LoadAsync();
                    var workspace = w.Root.Chats.Private; var composer = workspace.Composer;
                    await workspace.Navigation.RefreshAsync();
                    await workspace.Navigation.SelectConversationAsync(workspace.Navigation.Conversations.Single(c => c.Title == "New private peer"));
                    if (workspace.SelectedConversation!.Id is not null) throw new InvalidOperationException("Audit contact already has history.");
                    w.Root.Shell.SelectSection(ShellSection.PrivateChats);
                    var view = new ConversationView { DataContext = workspace };
                    var window = new Window { Content = view, Width = width, Height = 700,
                        RequestedThemeVariant = theme, Title = "D6 — эмуляция личной отправки, временная SQLite" };
                    window.Show(); window.Activate();
                    try
                    {
                        await SettleAsync(window);
                        await workspace.Navigation.History.JumpToLatestAsync(); await SettleAsync(window);
                        composer.Text = "Тест D6 👋"; await UntilAsync(() => composer.CanSend); await SettleAsync(window);
                        var control = view.GetVisualDescendants().OfType<ComposerView>().Single();
                        var button = control.GetVisualDescendants().OfType<Button>().Single();
                        if (!button.IsEnabled || composer.HasSlotChoice) throw new InvalidOperationException("Private send admission failed.");
                        var editor = control.FindControl<TextBox>("MessageInput")!; editor.Focus();
                        var presenter = editor.GetVisualDescendants().OfType<TextPresenter>().Single();
                        presenter.PreeditText = "я";
                        editor.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
                        presenter.PreeditText = null;
                        editor.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, KeyModifiers = KeyModifiers.Shift });
                        if (sender.Calls != 0) throw new InvalidOperationException("IME/Shift+Enter sent private text.");
                        composer.Text = "Тест D6 👋";
                        editor.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
                        if (composer.SendCommand.ExecutionTask is { } send) await send;
                        await UntilAsync(() => workspace.Navigation.History.Messages.Any(m => m.Body == "Тест D6 👋" && m.Presentation.State == MessageSendDisplayState.AwaitingAck));
                        var bubble = workspace.Navigation.History.Messages.Single(m => m.Body == "Тест D6 👋");
                        if (sender.Calls != 1 || composer.Text != "") throw new InvalidOperationException("Private draft transfer failed.");
                        composer.Text = "Следующее сообщение";
                        await UntilAsync(() => composer.CanSend); await SettleAsync(window);
                        if (!button.IsEnabled) throw new InvalidOperationException("Pending ACK blocked next send.");
                        var attempt = (await w.Storage.OutgoingMessages.GetAttemptsAsync(w.A.NodeId, bubble.Id)).Single();
                        await w.Storage.OutgoingMessages.TransitionAsync(new(w.A.NodeId, bubble.Id, attempt.Id, attempt.SessionId!.Value,
                            SendAttemptState.Accepted, SendAttemptState.Unconfirmed, DateTimeOffset.UtcNow));
                        await UntilAsync(() => bubble.CanSendAsNew); await SettleAsync(window);
                        var message = view.GetVisualDescendants().OfType<MessageView>().Single(v => ReferenceEquals(v.DataContext, bubble));
                        var anchor = message.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Classes.Contains("message"));
                        var menu = anchor.ContextMenu!;
                        menu.Open(anchor); await SettleAsync(window);
                        var resend = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Отправить еще раз"));
                        if (!resend.IsVisible || !resend.IsEnabled) throw new InvalidOperationException("Private resend menu binding failed.");
                        resend.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); menu.Close();
                        await UntilAsync(() => workspace.Navigation.History.Messages.Count(m => m.Body == bubble.Body) == 2);
                        await SettleAsync(window);
                        var fresh = workspace.Navigation.History.Messages.Single(m => m.Body == bubble.Body && m.Id != bubble.Id);
                        if (sender.Calls != 2 || composer.Text != "Следующее сообщение" || fresh.SendAsNewVisible || window.OwnedWindows.Count != 0)
                            throw new InvalidOperationException("Private resend changed draft, requested confirmation or failed to add a pending bubble.");
                        await w.Storage.OutgoingMessages.TransitionAsync(new(w.A.NodeId, bubble.Id, attempt.Id, attempt.SessionId!.Value,
                            SendAttemptState.Unconfirmed, SendAttemptState.Delivered, DateTimeOffset.UtcNow, RoundTripMilliseconds: 25));
                        await UntilAsync(() => bubble.Presentation.State == MessageSendDisplayState.Delivered && !bubble.SendAsNewVisible);
                        if (fresh.Presentation.State != MessageSendDisplayState.AwaitingAck)
                            throw new InvalidOperationException("Late source ACK changed the new message.");
                        await SettleAsync(window);
                        if (workspace.SelectedConversation!.Id is null || w.Root.ErrorMessage is not null)
                            throw new InvalidOperationException("First private send did not reopen materialized history.");
                        if (!ReferenceEquals(bubble, workspace.Navigation.History.Messages.Single(m => m.Id == bubble.Id)))
                            throw new InvalidOperationException("ACK replaced message bubble.");
                        var status = control.GetVisualDescendants().OfType<TextBlock>().Single(c => c.Classes.Contains("composer-status"));
                        if (status.Text != composer.StatusLine || control.Bounds.Width > window.ClientSize.Width)
                            throw new InvalidOperationException("Private composer binding/layout failed.");
                        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.ClientSize.Width, (int)window.ClientSize.Height));
                        bitmap.Render(window); bitmap.Save(Path.Combine(output, $"{theme}-{width}.png"), PngBitmapEncoderOptions.Default);
                        Console.WriteLine($"D6 native {theme} {width}: Enter=1 TX, Shift/IME=0 TX; AwaitingAck → Unconfirmed → Delivered same bubble; real resend menu adds pending bubble, preserves draft; late source ACK does not confirm copy");
                    }
                    finally { await w.Root.StopAsync(); window.Close(); }
                }
            Console.WriteLine($"D6 screenshots: {output}");
        }
    }
}
