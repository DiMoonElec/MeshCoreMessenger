using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed partial class SessionCommandGatewayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductionSenderBlocksClearBeforeAndAfterPreparedCommit(bool afterPrepare)
    {
        await using var f = await Fixture.CreateAsync(); await f.Connect();
        var drafts = new DraftWriteTracker(f.Storage.Drafts, TimeProvider.System);
        var request = await PrivateRequest(f, drafts);
        var store = new GatedPrepareStore(f.Storage.OutgoingMessages, afterPrepare);
        var sender = new MessageService(f.Gateway, store, f.Storage.Directories, f.Storage.ConversationDirectory,
            drafts, new PassthroughOutgoingTextProcessor(), TimeProvider.System, f.Storage.Drafts, f.Operations);
        var sending = sender.SendPrivateAsync(request, cancellationToken: CancellationToken);
        await store.Entered.Task.WaitAsync(CancellationToken);
        try
        {
            var draft = (await f.Storage.Drafts.GetAsync(request.Draft.Target, CancellationToken))!;
            Assert.Equal(afterPrepare ? 1 : 0, (await f.Storage.History.GetMessagesAsync(f.NodeId, draft.ConversationId, null, 10, CancellationToken)).Count);
            var clear = new HistoryClearService(f.Storage.HistoryClear, f.Operations, new(f.Storage.ReadStates), f.Outgoing);
            Assert.NotNull(await clear.GetUnavailableReasonAsync(f.NodeId, draft.ConversationId, CancellationToken));
            await Assert.ThrowsAsync<InvalidOperationException>(() => clear.ClearAsync(f.NodeId, draft.ConversationId, CancellationToken));
            Assert.Equal(0, f.Clients.Current!.Tx);
        }
        finally { store.Proceed.TrySetResult(); }
        await sending;
    }

    private sealed class GatedPrepareStore(IOutgoingMessageStore inner, bool afterPrepare) : IOutgoingMessageStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Proceed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event EventHandler<OutgoingMessageCommit>? MessageCommitted { add => inner.MessageCommitted += value; remove => inner.MessageCommitted -= value; }
        public async Task<PreparedOutgoingMessage> PrepareAsync(PrepareOutgoingMessage message, CancellationToken cancellationToken = default)
        {
            var prepared = afterPrepare ? await inner.PrepareAsync(message, cancellationToken) : null;
            Entered.TrySetResult();
            await Proceed.Task.WaitAsync(cancellationToken);
            return prepared ?? await inner.PrepareAsync(message, cancellationToken);
        }
        public Task<bool> TransitionAsync(OutgoingAttemptTransition transition, CancellationToken cancellationToken = default) => inner.TransitionAsync(transition, cancellationToken);
        public Task<StoredOutgoingMessage> GetAsync(Guid nodeId, Guid messageId, CancellationToken cancellationToken = default) => inner.GetAsync(nodeId, messageId, cancellationToken);
        public Task<IReadOnlyList<OutgoingAttemptSnapshot>> GetAttemptsAsync(Guid nodeId, Guid messageId, CancellationToken cancellationToken = default) => inner.GetAttemptsAsync(nodeId, messageId, cancellationToken);
    }
}
