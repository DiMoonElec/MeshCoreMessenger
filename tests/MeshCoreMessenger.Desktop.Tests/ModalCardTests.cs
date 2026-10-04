using Microsoft.Extensions.Logging.Abstractions;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.ViewModels;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed class ModalCardTests
{
    [Fact]
    public async Task SingleCardClosePolicyResultAndDisposal()
    {
        var host = new ModalHostViewModel();
        var card = new Card();
        var result = host.ShowAsync(card);
        Assert.Throws<InvalidOperationException>(() => { _ = host.ShowAsync(new Card()); });
        Assert.False(host.Close(ModalCloseReason.Backdrop));
        card.AllowClose = false;
        Assert.False(host.Close(ModalCloseReason.Escape));
        Assert.False(result.IsCompleted);
        card.AllowClose = true;
        Assert.True(host.Close(ModalCloseReason.Completed, "saved"));
        Assert.Equal(new ModalResult(ModalCloseReason.Completed, "saved"), await result);
        Assert.Equal(1, card.Disposals);
        Assert.False(host.Close(ModalCloseReason.Escape));
        var blocked = new Card { AllowClose = false };
        var pending = host.ShowAsync(blocked);
        host.Close(ModalCloseReason.Shutdown);
        Assert.Equal(ModalCloseReason.Shutdown, (await pending).Reason);
        Assert.True(host.IsClosed);
    }

    [Fact]
    public async Task ClosingDuringReadCancelsAndNeverPublishesLateData()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<ContactDetailsProjection?>();
        CancellationToken captured = default;
        var reader = new Reader((_, _, token) => { captured = token; entered.SetResult(); return late.Task; });
        var key = new byte[32];
        var card = Create(reader, Guid.NewGuid(), key);
        var loading = card.RefreshAsync();
        await entered.Task;
        card.Dispose();
        Assert.True(captured.IsCancellationRequested);
        late.SetResult(Facts(Guid.NewGuid(), key));
        await loading;
        Assert.Null(card.Details);
    }

    [Fact]
    public async Task ReadFailureCanBeRetriedAndOlderReadCannotOverwriteNewerFacts()
    {
        var node = Guid.NewGuid(); var key = new byte[32];
        var old = new TaskCompletionSource<ContactDetailsProjection?>();
        var call = 0;
        var reader = new Reader((n, k, _) =>
        {
            Assert.Equal(node, n); Assert.Equal(key, k.ToArray());
            return ++call switch
            {
                1 => Task.FromException<ContactDetailsProjection?>(new IOException("disk")),
                2 => old.Task,
                _ => Task.FromResult<ContactDetailsProjection?>(Facts(node, key) with { OutPathLength = 0 }),
            };
        });
        using var card = Create(reader, node, key);
        await card.RefreshAsync(); Assert.True(card.HasError);
        var pending = card.RefreshAsync();
        await card.RefreshAsync();
        old.SetResult(Facts(node, key) with { OutPathLength = 255 });
        await pending;
        Assert.False(card.HasError); Assert.False(card.IsLoading);
        Assert.Equal("напрямую", card.Route);
    }

    private static ContactDetailsCardViewModel Create(Reader reader, Guid node, byte[] key) =>
        new(node, key, reader, null, new InlineDispatcher(), NullLogger.Instance, task => task);
    private static ContactDetailsProjection Facts(Guid node, byte[] key) =>
        new(node, key, "Alice", 1, 0, new byte[64], null, true, null, null, null, DateTimeOffset.UtcNow);
    private sealed class Card() : ModalCardViewModel("Example")
    {
        public bool AllowClose { get; set; } = true;
        public int Disposals { get; private set; }
        public override bool CanClose(ModalCloseReason reason) => AllowClose;
        public override void Dispose() => Disposals++;
    }
    private sealed class InlineDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); action(); return Task.CompletedTask; }
    }
    private sealed class Reader(Func<Guid, ReadOnlyMemory<byte>, CancellationToken, Task<ContactDetailsProjection?>> read) : IConversationDirectoryReader
    {
        public Task<ContactDetailsProjection?> GetContactDetailsAsync(Guid node, ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default) => read(node, key, cancellationToken);
        public Task<ConversationDirectoryPage> GetPageAsync(Guid nodeId, ConversationDirectorySection section, ConversationDirectoryCursor? after, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ConversationDirectoryPage> SearchPageAsync(Guid nodeId, ConversationDirectorySection section, string query, ConversationDirectoryCursor? after, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ChannelDetailsProjection?> GetChannelDetailsAsync(Guid nodeId, ReadOnlyMemory<byte> keyFingerprint, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

public sealed partial class UiWorkspaceIntegrationTests
{
    [Fact]
    public async Task PrivateDetailsOpensOfflineCapturedContactAndShutdownClosesCard()
    {
        await using var w = await Workspace.CreateAsync();
        var root = w.Root;
        Assert.Equal(ConnectionSupervisorState.Offline, w.Supervisor.Snapshot.State);
        var item = root.Chats.Private.Menu.Items.Single(i => i.Action == ConversationMenuAction.Details);
        Assert.Equal("О контакте", item.Header);
        Assert.True(item.Command.CanExecute(null));
        item.Command.Execute(null);
        var card = Assert.IsType<ContactDetailsCardViewModel>(root.Modal.Active);
        await UntilAsync(() => card.HasDetails);
        Assert.Equal("Personal chat", card.Details!.Name);
        Assert.Equal("Компаньон", card.Details.Type);
        await root.SelectViewedNodeAsync(root.KnownNodes.Single(n => n.Id == w.B.NodeId), Token);
        Assert.Same(card, root.Modal.Active);
        Assert.False(card.HasDescription);
        Assert.DoesNotContain(root.Chats.Public.Menu.Items, i => i.Header == "О контакте");
        var session = Guid.NewGuid();
        await w.Storage.Sessions.CreateAsync(new SessionRecord(session, w.ProfileId, w.A.NodeId, Now, null, null), Token);
        var key = Enumerable.Repeat((byte)9, 32).ToArray();
        await w.Storage.Directories.UpdateContactRouteAsync(w.A.NodeId, session, key, new byte[64], 255, Now.AddMinutes(1), Token);
        await UntilAsync(() => card.Route.Contains("flood"));
        await w.Storage.Directories.UpdateContactRouteAsync(w.A.NodeId, session, key, new byte[64], 0, Now.AddMinutes(2), Token);
        await UntilAsync(() => card.Route == "напрямую");
        await root.StopAsync();
        Assert.True(root.Modal.IsClosed);
    }
}
