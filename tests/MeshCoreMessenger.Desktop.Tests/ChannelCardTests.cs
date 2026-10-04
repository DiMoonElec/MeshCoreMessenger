using Microsoft.Extensions.Logging.Abstractions;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.ViewModels;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed partial class UiWorkspaceIntegrationTests
{
    [Fact]
    public async Task PublicCardReadsCurrentMetadataAndRetainsChannelAcrossNodeSelection()
    {
        await using var w = await Workspace.CreateAsync();
        var root = w.Root;
        var fingerprint = Enumerable.Repeat((byte)42, 32).ToArray();
        await w.Storage.Directories.ApplySnapshotAsync(w.A.NodeId, w.A.SessionId, [],
            [new DirectoryChannelSnapshot(0, "#public-test", fingerprint, ChannelAccessKind.PublicOrHashtag),
             new DirectoryChannelSnapshot(3, "#public-test", fingerprint, ChannelAccessKind.PublicOrHashtag)], Now.AddMinutes(1), Token);
        var item = root.Chats.Public.Menu.Items.Single(i => i.Action == ConversationMenuAction.Details);
        Assert.Equal("О канале", item.Header);
        Assert.True(item.Command.CanExecute(null));
        item.Command.Execute(null);
        var card = Assert.IsType<ChannelDetailsCardViewModel>(root.Modal.Active);
        await UntilAsync(() => card.HasDetails);
        Assert.Equal("#public-test", card.Details!.Name);
        Assert.Equal("Публичный / hashtag", card.Details.Access);
        Assert.Equal("0, 3", card.Details.Slots);
        Assert.Equal("Настроен на ноде", card.Details.Presence);
        Assert.Equal(Convert.ToHexString(fingerprint).ToLowerInvariant(), card.Fingerprint);
        Assert.False(card.HasDescription);
        await root.SelectViewedNodeAsync(root.KnownNodes.Single(n => n.Id == w.B.NodeId), Token);
        Assert.Same(card, root.Modal.Active);
        await card.RefreshAsync();
        Assert.Equal("#public-test", card.Details.Name); // B has the same fingerprint, but another name/slot configuration.
        Assert.Equal("0, 3", card.Details.Slots);
        root.Modal.CloseCommand.Execute(null);
        Assert.True(root.Modal.IsClosed);
        await root.StopAsync();
    }
}

public sealed class ChannelCardTests
{
    [Fact]
    public async Task ClosingChannelCardCancelsReadAndRejectsLateResult()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<ChannelDetailsProjection?>();
        var node = Guid.NewGuid(); var fingerprint = new byte[32];
        CancellationToken token = default;
        var reader = new Reader((n, f, ct) =>
        {
            Assert.Equal(node, n); Assert.Equal(fingerprint, f.ToArray());
            token = ct; entered.SetResult(); return pending.Task;
        });
        var card = Create(reader, node, fingerprint);
        var load = card.RefreshAsync();
        await entered.Task;
        card.Dispose();
        Assert.True(token.IsCancellationRequested);
        pending.SetResult(Facts(node, fingerprint));
        await load;
        Assert.Null(card.Details);
    }

    [Fact]
    public async Task MissingOrFailedReadCanBeRetriedForStoredInactiveChannel()
    {
        var node = Guid.NewGuid(); var fingerprint = new byte[32]; var reads = 0;
        var reader = new Reader((_, _, _) => ++reads switch
        {
            1 => Task.FromResult<ChannelDetailsProjection?>(null),
            2 => Task.FromException<ChannelDetailsProjection?>(new IOException("disk")),
            _ => Task.FromResult<ChannelDetailsProjection?>(Facts(node, fingerprint)),
        });
        using var card = Create(reader, node, fingerprint);
        await card.RefreshAsync(); Assert.True(card.HasError); Assert.False(card.IsLoading);
        await card.RetryCommand.ExecuteAsync(null); Assert.True(card.HasError);
        await card.RetryCommand.ExecuteAsync(null);
        Assert.False(card.HasError); Assert.True(card.HasDetails);
        Assert.Equal("Общий секрет", card.Details!.Access);
        Assert.Equal("Нет активных слотов", card.Details.Slots);
        Assert.Contains("сейчас не настроен", card.Details.Presence);
    }
    private static ChannelDetailsProjection Facts(Guid node, byte[] fingerprint) =>
        new(Guid.NewGuid(), node, "Stored channel", fingerprint, ChannelAccessKind.SharedSecret, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    private static ChannelDetailsCardViewModel Create(Reader reader, Guid node, byte[] fingerprint) =>
        new(node, fingerprint, reader, new InlineDispatcher(), NullLogger.Instance, task => task);
    private sealed class InlineDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); action(); return Task.CompletedTask; }
    }
    private sealed class Reader(Func<Guid, ReadOnlyMemory<byte>, CancellationToken, Task<ChannelDetailsProjection?>> read) : IConversationDirectoryReader
    {
        public Task<ChannelDetailsProjection?> GetChannelDetailsAsync(Guid nodeId, ReadOnlyMemory<byte> fingerprint, CancellationToken cancellationToken = default) => read(nodeId, fingerprint, cancellationToken);
        public Task<ContactDetailsProjection?> GetContactDetailsAsync(Guid nodeId, ReadOnlyMemory<byte> publicKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ConversationDirectoryPage> GetPageAsync(Guid nodeId, ConversationDirectorySection section, ConversationDirectoryCursor? after, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ConversationDirectoryPage> SearchPageAsync(Guid nodeId, ConversationDirectorySection section, string query, ConversationDirectoryCursor? after, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
