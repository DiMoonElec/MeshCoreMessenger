using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging;
using MeshCoreMessenger.Desktop.ViewModels;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Desktop.Views.Chat;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed partial class UiWorkspaceIntegrationTests
{
    private static async Task SeedSecondPublicChat(Workspace w)
    {
        var fingerprint = Enumerable.Repeat((byte)43, 32).ToArray();
        var snapshot = await w.Storage.Directories.ApplySnapshotAsync(w.A.NodeId, w.A.SessionId,
            [new(Enumerable.Repeat((byte)9, 32).ToArray(), "Personal chat", 1, 0, new byte[64], Now, 0, 0)],
            [new(0, "Chat A", w.A.Target.Identity, ChannelAccessKind.Unknown), new(1, "Chat B", fingerprint, ChannelAccessKind.Unknown)], Now, Token);
        await w.Storage.IncomingMessages.StoreAsync(new(Guid.NewGuid(), w.A.SessionId, w.A.NodeId,
            new MeshCoreSharp.Models.ChannelMessage(1, 1, MeshCoreSharp.Models.MessageTextType.Plain, Now, "B fixture", 0),
            Now, snapshot.ActiveBindings.Single(b => b.Slot == 1), null), Token);
    }

    public static class NativeSwitchAudit
    {
        public static async Task RunAsync()
        {
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                await using var w = await Workspace.CreateAsync();
                await w.Root.StopAsync();
                await SeedSecondPublicChat(w);
                var sender = new UiChannelSender(w);
                var logger = new SwitchLogger();
                w.Root = w.CreateRoot(dispatcher: new NativeDispatcher(), messageService: sender, logger: logger);
                await w.Root.LoadAsync();
                await OnlineSender(w);
                w.Root.Shell.SelectSection(ShellSection.PublicChats);
                var workspace = w.Root.Chats.Public;
                var view = new ChatsView { DataContext = workspace };
                var window = new Window { Content = view, Width = 1000, Height = 700, RequestedThemeVariant = theme };
                window.Show();
                try
                {
                    await UntilAsync(() => workspace.Navigation.Conversations.Count == 2);
                    var a = workspace.Navigation.Conversations.Single(c => c.Entry.Identity.AsSpan().SequenceEqual(w.A.Target.Identity));
                    var b = workspace.Navigation.Conversations.Single(c => !c.Entry.Identity.AsSpan().SequenceEqual(w.A.Target.Identity));
                    await workspace.SelectConversationAsync(a);
                    await workspace.Navigation.History.JumpToLatestAsync();
                    await SettleAsync(window);
                    workspace.Composer.Text = "A first";
                    await UntilAsync(() => workspace.Composer.CanSend);
                    await workspace.Composer.SendCommand.ExecuteAsync(null);
                    await Task.Delay(150);
                    await SettleAsync(window);
                    var list = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "ConversationList");
                    list.SelectedItem = workspace.Navigation.Conversations.Single(c => c.StableKey == b.StableKey);
                    await UntilAsync(() => workspace.SelectedConversation?.StableKey == b.StableKey && workspace.Navigation.Draft.Capture().Target.Identity.AsSpan().SequenceEqual(b.Entry.Identity));
                    workspace.Composer.Text = "B draft";
                    await UntilAsync(() => workspace.Composer.Readiness is SendReadiness.Ready or SendReadiness.ReadFailed);
                    if (!workspace.Composer.CanSend)
                        throw new InvalidOperationException($"A -> B disables send: {workspace.Composer.StatusLine}");
                    await workspace.Composer.SendCommand.ExecuteAsync(null);
                    list.SelectedItem = workspace.Navigation.Conversations.Single(c => c.StableKey == a.StableKey);
                    await UntilAsync(() => workspace.SelectedConversation?.StableKey == a.StableKey && workspace.Navigation.Draft.Capture().Target.Identity.AsSpan().SequenceEqual(a.Entry.Identity));
                    workspace.Composer.Text = "A second";
                    await UntilAsync(() => workspace.Composer.CanSend);
                    await workspace.Composer.SendCommand.ExecuteAsync(null);
                    if (sender.Calls != 3 || sender.LastSlot != 0) throw new InvalidOperationException("Wrong send count/slot after switching.");
                    if (logger.Errors.Count > 0) throw new AggregateException(logger.Errors);
                    Console.WriteLine($"D5 native {theme}: A send -> B send -> A send without incoming events OK");
                }
                finally { await w.Root.StopAsync(); window.Close(); }
            }
        }
    }
    private sealed class SwitchLogger : ILogger<MainWindowViewModel>
    {
        public System.Collections.Concurrent.ConcurrentQueue<Exception> Errors { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is null) return;
            Errors.Enqueue(exception);
            Console.WriteLine($"Switch audit error: {exception}");
        }
    }

}
