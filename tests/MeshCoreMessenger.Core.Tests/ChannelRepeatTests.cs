using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed partial class SessionCommandGatewayTests
{
    [Fact]
    public async Task SendChannelAsNewAfterReconnectCreatesIndependentMessageAndLeavesDraftAndSourceIntact()
    {
        await using var f = await Fixture.CreateAsync(); await f.Connect();
        var drafts = new DraftWriteTracker(f.Storage.Drafts, TimeProvider.System);
        var sender = Sender(f, drafts, new CapturedProcessor());
        var request = await Request(f, drafts);
        var first = await sender.SendChannelAsync(request, cancellationToken: CancellationToken);
        var source = await f.Storage.OutgoingMessages.GetAsync(f.NodeId, first.MessageId, CancellationToken);
        var original = Assert.Single(await f.Storage.OutgoingMessages.GetAttemptsAsync(f.NodeId, first.MessageId, CancellationToken));
        drafts.Update(request.Draft.Target, "Leave draft alone", 2);
        await f.Supervisor.DisconnectAsync(CancellationToken); await f.Connect();
        sender = Sender(f, drafts);
        var owner = f.Supervisor.Snapshot;
        var copy = await sender.SendChannelAsNewAsync(new(f.NodeId, owner.SessionId!.Value, owner.Generation,
            first.MessageId, 1, await f.ChannelRecipient()), CancellationToken);
        Assert.NotEqual(first.MessageId, copy.MessageId);
        Assert.Equal(SendAttemptState.Accepted, copy.State);
        var storedCopy = await f.Storage.OutgoingMessages.GetAsync(f.NodeId, copy.MessageId, CancellationToken);
        Assert.Equal(source.OriginalText, storedCopy.OriginalText);
        Assert.Equal(source.TransmissionText, storedCopy.TransmissionText);
        Assert.Equal(source.ConversationId, storedCopy.ConversationId);
        var attempt = Assert.Single(await f.Storage.OutgoingMessages.GetAttemptsAsync(f.NodeId, copy.MessageId, CancellationToken));
        Assert.Equal(1, attempt.AttemptNumber);
        Assert.True(attempt.WireTimestamp > original.WireTimestamp);
        Assert.Equal(original, Assert.Single(await f.Storage.OutgoingMessages.GetAttemptsAsync(f.NodeId, first.MessageId, CancellationToken)));
        var unchanged = await f.Storage.OutgoingMessages.GetAsync(f.NodeId, first.MessageId, CancellationToken);
        Assert.Equal(source.SessionId, unchanged.SessionId);
        Assert.Equal(source.TransmissionText, unchanged.TransmissionText);
        await drafts.FlushAsync(request.Draft.Target, CancellationToken);
        Assert.Equal("Leave draft alone", (await f.Storage.Drafts.GetAsync(request.Draft.Target, CancellationToken))!.Text);
        sender = Sender(f, drafts); // Recreated allocator must also exceed timestamps of earlier copies.
        var secondCopy = await sender.SendChannelAsNewAsync(new(f.NodeId, owner.SessionId.Value, owner.Generation,
            first.MessageId, 1, await f.ChannelRecipient()), CancellationToken);
        var secondAttempt = Assert.Single(await f.Storage.OutgoingMessages.GetAttemptsAsync(f.NodeId, secondCopy.MessageId, CancellationToken));
        Assert.True(secondAttempt.WireTimestamp > attempt.WireTimestamp);
    }

    [Fact]
    public async Task ChannelRepeatCreatesNewAttemptAfterReconnectAndRejectsStaleDoubleClick()
    {
        await using var f = await Fixture.CreateAsync(); await f.Connect();
        var drafts = new DraftWriteTracker(f.Storage.Drafts, TimeProvider.System);
        var sender = Sender(f, drafts, new CapturedProcessor());
        var first = await sender.SendChannelAsync(await Request(f, drafts), cancellationToken: CancellationToken);
        var original = Assert.Single(await f.Storage.OutgoingMessages.GetAttemptsAsync(f.NodeId, first.MessageId, CancellationToken));
        await f.Supervisor.DisconnectAsync(CancellationToken); await f.Connect();
        sender = Sender(f, drafts); // New service; the original timestamp comes from persisted attempts.
        var owner = f.Supervisor.Snapshot;
        var request = new ChannelRepeatRequest(f.NodeId, owner.SessionId!.Value, owner.Generation, first.MessageId, 1, await f.ChannelRecipient());
        var result = await sender.RepeatChannelAsync(request, CancellationToken);
        Assert.Equal(first.MessageId, result.MessageId);
        Assert.Equal("processed", f.Clients.Current!.SentText);
        var attempts = await f.Storage.OutgoingMessages.GetAttemptsAsync(f.NodeId, first.MessageId, CancellationToken);
        Assert.Equal(2, attempts.Count); Assert.Equal(original, attempts[0]);
        Assert.NotEqual(original.SessionId, attempts[1].SessionId);
        Assert.Equal(original.WireTimestamp, attempts[1].WireTimestamp);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.RepeatChannelAsync(request, CancellationToken));
        Assert.Equal(1, f.Clients.Current.Tx);
    }

    [Fact]
    public async Task RepeatPrepareFailureDoesNotTransmitOrDeleteThePreviousAttempt()
    {
        await using var f = await Fixture.CreateAsync(); await f.Connect();
        var drafts = new DraftWriteTracker(f.Storage.Drafts, TimeProvider.System); var sender = Sender(f, drafts);
        var first = await sender.SendChannelAsync(await Request(f, drafts), cancellationToken: CancellationToken);
        f.Execute("CREATE TRIGGER fail_repeat BEFORE INSERT ON SendAttempts WHEN NEW.AttemptNumber>1 BEGIN SELECT RAISE(ABORT,'disk failure'); END;");
        var owner = f.Supervisor.Snapshot;
        var recipient = await f.ChannelRecipient();
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => sender.RepeatChannelAsync(new(f.NodeId,
            owner.SessionId!.Value, owner.Generation, first.MessageId, 1, recipient), CancellationToken));
        Assert.Equal(1, f.Clients.Current!.Tx);
        Assert.Single(await f.Storage.OutgoingMessages.GetAttemptsAsync(f.NodeId, first.MessageId, CancellationToken));
    }

    [Fact]
    public async Task RepeatKeepsClearGuardAndRejectsConcurrentInvocation()
    {
        await using var f = await Fixture.CreateAsync(); await f.Connect();
        var drafts = new DraftWriteTracker(f.Storage.Drafts, TimeProvider.System); var sender = Sender(f, drafts);
        var first = await sender.SendChannelAsync(await Request(f, drafts), cancellationToken: CancellationToken);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recipient = await f.ChannelRecipient(); var owner = f.Supervisor.Snapshot;
        f.Clients.Current!.ChannelSendAction = () => release.Task;
        var request = new ChannelRepeatRequest(f.NodeId, owner.SessionId!.Value, owner.Generation, first.MessageId, 1, recipient);
        var repeat = sender.RepeatChannelAsync(request, CancellationToken);
        await Until(() => f.Clients.Current.Tx == 2);
        Assert.Throws<InvalidOperationException>(() => f.Operations.BeginClear(f.NodeId, ConversationKind.Channel, recipient.Identity));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.RepeatChannelAsync(request, CancellationToken));
        release.SetResult(); await repeat;
        using var clear = f.Operations.BeginClear(f.NodeId, ConversationKind.Channel, recipient.Identity);
        Assert.Equal(2, (await f.Storage.OutgoingMessages.GetAttemptsAsync(f.NodeId, first.MessageId, CancellationToken)).Count);
    }
    [Fact]
    public async Task RepeatFollowsSameFingerprintToNewSlotAndRejectsUnrelatedChannel()
    {
        await using var f = await Fixture.CreateAsync(); await f.Connect();
        var drafts = new DraftWriteTracker(f.Storage.Drafts, TimeProvider.System); var sender = Sender(f, drafts);
        var first = await sender.SendChannelAsync(await Request(f, drafts), cancellationToken: CancellationToken);
        var old = await f.ChannelRecipient(); var owner = f.Supervisor.Snapshot;
        var other = System.Security.Cryptography.SHA256.HashData(new byte[16]);
        var snapshot = await f.Storage.Directories.ApplySnapshotAsync(f.NodeId, owner.SessionId!.Value, [],
            [new(7, "Other channel", other, ChannelAccessKind.SharedSecret),
             new(8, "Same channel moved", old.Identity.ToArray(), ChannelAccessKind.SharedSecret)], DateTimeOffset.UtcNow, CancellationToken);
        await f.Storage.Directories.CommitPendingChannelTransitionsAsync(snapshot.PendingChannelTransitions, DateTimeOffset.UtcNow, CancellationToken);
        var transitions = await f.Storage.Directories.GetActiveChannelBindingAsync(f.NodeId, 8, CancellationToken);
        var target = new OutgoingRecipient(ConversationKind.Channel, old.Identity, transitions!.Id, 8, transitions.Generation);
        var sent = await sender.RepeatChannelAsync(new(f.NodeId, owner.SessionId.Value, owner.Generation,
            first.MessageId, 1, target), CancellationToken);
        Assert.Equal(SendAttemptState.Accepted, sent.State); Assert.Equal(2, f.Clients.Current!.Tx);
        var wrong = Assert.Single(await sender.GetChannelTargetsAsync(f.NodeId, other, CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.RepeatChannelAsync(new(f.NodeId,
            owner.SessionId.Value, owner.Generation, first.MessageId, 2, wrong), CancellationToken));
        Assert.Equal(2, f.Clients.Current.Tx);
    }

    [Fact]
    public async Task UnknownRepeatRetainsReservedTimestampAndCanOnlyBeSentAgainExplicitly()
    {
        await using var f = await Fixture.CreateAsync(); await f.Connect();
        var drafts = new DraftWriteTracker(f.Storage.Drafts, TimeProvider.System); var sender = Sender(f, drafts);
        var first = await sender.SendChannelAsync(await Request(f, drafts), cancellationToken: CancellationToken);
        var owner = f.Supervisor.Snapshot; var recipient = await f.ChannelRecipient();
        f.Clients.Current!.ChannelSendAction = () => throw new IOException("Response was lost after invocation");
        var unknown = await sender.RepeatChannelAsync(new(f.NodeId, owner.SessionId!.Value, owner.Generation,
            first.MessageId, 1, recipient), CancellationToken);
        Assert.Equal(SendAttemptState.Unknown, unknown.State);
        var attempts = await f.Storage.OutgoingMessages.GetAttemptsAsync(f.NodeId, first.MessageId, CancellationToken);
        Assert.Equal(attempts[0].WireTimestamp, attempts[1].WireTimestamp);
        f.Clients.Current.ChannelSendAction = null;
        sender = Sender(f, drafts);
        Assert.Equal(2, f.Clients.Current.Tx); // Recreating the service does not replay Unknown.
        await sender.RepeatChannelAsync(new(f.NodeId, owner.SessionId.Value, owner.Generation, first.MessageId, 2, recipient), CancellationToken);
        var third = (await f.Storage.OutgoingMessages.GetAttemptsAsync(f.NodeId, first.MessageId, CancellationToken))[2];
        Assert.Equal(attempts[1].WireTimestamp, third.WireTimestamp);
        Assert.Equal(3, f.Clients.Current.Tx);
    }

}
