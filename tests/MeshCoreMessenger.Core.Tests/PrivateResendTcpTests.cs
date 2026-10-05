using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed partial class SessionCommandGatewayTests
{
    [Fact]
    public async Task ManualPrivateResendStartsFreshCycleUsesCurrentRouteAndPreservesDraftAndSeparateAcks()
    {
        await using var f = await TcpPrivateFixture.CreateAsync(false, 100, autoRetries: true);
        const string text = "Отправить еще раз 👋";
        var original = await f.Send(text, 0xA1, 1);
        await WaitCycle(f, original.MessageId, PrivateDeliveryState.Unconfirmed);
        var oldTransmissions = f.Server.PrivateTransmissions.ToArray(); Assert.Equal(3, oldTransmissions.Length);
        var stored = await f.Storage.OutgoingMessages.GetAsync(f.Node, original.MessageId, CancellationToken);
        var target = new DraftTarget(f.Node, stored.ConversationId, ConversationKind.Contact, stored.Recipient.Identity.ToArray());
        await f.Drafts.LoadTextAsync(target, CancellationToken);
        f.Drafts.Update(target, "Сохранённый черновик", 2); await f.Drafts.FlushAsync(target, CancellationToken);
        f.Drafts.Update(target, "Ещё не сохранённый черновик", 3);
        f.Server.ContactRoutes[0xA1] = 0;
        f.Server.AutoAcknowledgePrivate = true;
        var owner = f.Supervisor.Snapshot;
        var fresh = await f.Sender.SendPrivateAsNewAsync(new(f.Node, owner.SessionId!.Value, owner.Generation,
            original.MessageId, new()), CancellationToken);
        await WaitCycle(f, fresh.MessageId, PrivateDeliveryState.Delivered);
        Assert.NotEqual(original.MessageId, fresh.MessageId);
        var transmissions = f.Server.PrivateTransmissions.ToArray(); Assert.Equal(4, transmissions.Length);
        var resend = transmissions[^1]; Assert.Equal(text, resend.Text); Assert.Equal(0, resend.Attempt);
        Assert.Equal(0, resend.RouteDescriptor); Assert.True(resend.Timestamp > oldTransmissions[0].Timestamp);
        Assert.NotEqual(oldTransmissions[0].ExpectedAck, resend.ExpectedAck);
        Assert.Equal(PrivateDeliveryState.Unconfirmed, (await f.Storage.OutgoingMessages.GetPrivateCycleAsync(f.Node, original.MessageId, CancellationToken))!.State);
        Assert.Equal("Ещё не сохранённый черновик", await f.Drafts.LoadTextAsync(target, CancellationToken));
        Assert.Equal("Сохранённый черновик", (await f.Storage.Drafts.GetAsync(target, CancellationToken))!.Text);
        var freshCycle = (await f.Storage.OutgoingMessages.GetPrivateCycleAsync(f.Node, fresh.MessageId, CancellationToken))!;
        Assert.Equal(5, freshCycle.PlannedAttemptCount); Assert.Equal(1, freshCycle.PreparedAttemptCount);
        await f.Server.SendAcknowledgementAsync(oldTransmissions[0].ExpectedAck);
        await WaitCycle(f, original.MessageId, PrivateDeliveryState.Delivered);
        Assert.Equal(PrivateDeliveryState.Delivered, (await f.Storage.OutgoingMessages.GetPrivateCycleAsync(f.Node, fresh.MessageId, CancellationToken))!.State);
        Assert.Equal(2, (await f.Storage.History.GetMessagesAsync(f.Node, stored.ConversationId, null, 20, CancellationToken)).Count(m => m.Text == text));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Sender.SendPrivateAsNewAsync(new(f.Node,
            owner.SessionId.Value, owner.Generation, original.MessageId, new()), CancellationToken));
        Assert.Equal(4, f.Server.PrivateTransmissions.Count);
    }

    [Fact]
    public async Task StaleSessionCannotAdmitPrivateResend()
    {
        await using var f = await TcpPrivateFixture.CreateAsync(false, 100, autoRetries: true);
        var original = await f.Send("stale resend", 0xA1, 1); await WaitCycle(f, original.MessageId, PrivateDeliveryState.Unconfirmed);
        var owner = f.Supervisor.Snapshot;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Sender.SendPrivateAsNewAsync(new(f.Node,
            Guid.NewGuid(), owner.Generation, original.MessageId, new()), CancellationToken));
        Assert.Equal(3, f.Server.PrivateTransmissions.Count);
    }
}
