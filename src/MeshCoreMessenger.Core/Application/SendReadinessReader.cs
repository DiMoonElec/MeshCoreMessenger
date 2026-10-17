using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;

namespace MeshCoreMessenger.Core.Application;

public enum SendReadiness
{
    Ready, Offline, Synchronizing, NotOnline, WrongNode, NoRecipient, UnsupportedRecipient,
    ContactMissing, AmbiguousPrefix, ChannelMissing, ChooseSlot, StaleBinding, Checking, ReadFailed, UnsupportedScope,
}

/// <summary>Immutable recipient capture for read-only presentation checks, not a wire admission lease.</summary>
public sealed record SendRecipient(ConversationKind Kind, ReadOnlyMemory<byte> Identity, byte? ChannelSlot = null);
public sealed record SendBudgetReadiness(SendReadiness Readiness, OutgoingTextBudget? Budget);

public interface ISendReadinessReader
{
    async Task<SendBudgetReadiness> ReadBudgetAsync(ConnectionSupervisorSnapshot connection, Guid? viewedNodeId,
        SendRecipient? recipient, CancellationToken cancellationToken = default) => new(await ReadAsync(connection, viewedNodeId, recipient, cancellationToken), null);
    Task<SendReadiness> ReadAsync(ConnectionSupervisorSnapshot connection, Guid? viewedNodeId,
        SendRecipient? recipient, CancellationToken cancellationToken = default);
}

/// <summary>Uses committed node-scoped directory data. It never sends commands or reads the session event channel.</summary>
public sealed class SendReadinessReader(IDirectoryStore directories, IConversationDirectoryReader directory, IMessageService? messages = null) : ISendReadinessReader
{
    public async Task<SendBudgetReadiness> ReadBudgetAsync(ConnectionSupervisorSnapshot connection, Guid? viewedNodeId,
        SendRecipient? recipient, CancellationToken cancellationToken = default)
    {
        var readiness = await ReadAsync(connection, viewedNodeId, recipient, cancellationToken).ConfigureAwait(false);
        if (readiness != SendReadiness.Ready || messages is null) return new(readiness, null);
        OutgoingRecipient target;
        if (recipient!.Kind == ConversationKind.Contact) target = new(ConversationKind.Contact, recipient.Identity.ToArray());
        else
        {
            var candidates = await messages.GetChannelTargetsAsync(viewedNodeId!.Value, recipient.Identity, cancellationToken).ConfigureAwait(false);
            var selected = recipient.ChannelSlot is { } slot ? candidates.SingleOrDefault(c => c.Slot == slot) : candidates.Count == 1 ? candidates[0] : null;
            if (selected is null) return new(SendReadiness.StaleBinding, null);
            target = selected;
        }
        var budget = await messages.GetTextBudgetAsync(viewedNodeId!.Value, target, cancellationToken).ConfigureAwait(false);
        return budget.SessionId == connection.SessionId && budget.Generation == connection.Generation
            ? new(budget.ScopeSupport == RegionSupport.Unsupported ? SendReadiness.UnsupportedScope : SendReadiness.Ready, budget)
            : new(SendReadiness.NotOnline, null);
    }

    public static SendReadiness? CheckContext(ConnectionSupervisorSnapshot connection, Guid? viewedNodeId, SendRecipient? recipient)
    {
        if (connection.State == ConnectionSupervisorState.Offline) return SendReadiness.Offline;
        if (connection.State == ConnectionSupervisorState.Synchronizing) return SendReadiness.Synchronizing;
        if (connection.State != ConnectionSupervisorState.Online || connection.SessionId is null) return SendReadiness.NotOnline;
        if (viewedNodeId is null || connection.NodeId != viewedNodeId) return SendReadiness.WrongNode;
        if (recipient is null) return SendReadiness.NoRecipient;
        if (recipient.Kind is not (ConversationKind.Contact or ConversationKind.Channel)) return SendReadiness.UnsupportedRecipient;
        return null;
    }

    public async Task<SendReadiness> ReadAsync(ConnectionSupervisorSnapshot connection, Guid? viewedNodeId,
        SendRecipient? recipient, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CheckContext(connection, viewedNodeId, recipient) is { } unavailable) return unavailable;
        var target = recipient! with { Identity = recipient!.Identity.ToArray() };
        if (target.Kind == ConversationKind.Contact)
        {
            if (target.Identity.Length != ProtocolLimits.PublicKeySize) return SendReadiness.UnsupportedRecipient;
            var contacts = await directories.GetCurrentContactsByPrefixAsync(viewedNodeId!.Value,
                target.Identity[..ProtocolLimits.MessageContactPrefixSize], cancellationToken).ConfigureAwait(false);
            if (contacts.Count > 1) return SendReadiness.AmbiguousPrefix;
            return contacts.Count == 1 && contacts[0].PresentOnNode &&
                contacts[0].ContactType == (int)AdvertisementType.Chat &&
                contacts[0].PublicKey.AsSpan().SequenceEqual(target.Identity.Span)
                ? SendReadiness.Ready : SendReadiness.ContactMissing;
        }

        var channel = await directory.GetChannelDetailsAsync(viewedNodeId!.Value, target.Identity, cancellationToken).ConfigureAwait(false);
        if (channel is null || channel.ActiveSlots.Count == 0) return SendReadiness.ChannelMissing;
        if (target.ChannelSlot is null && channel.ActiveSlots.Count > 1) return SendReadiness.ChooseSlot;
        var slot = target.ChannelSlot ?? channel.ActiveSlots[0];
        if (!channel.ActiveSlots.Contains(slot)) return SendReadiness.StaleBinding;
        var binding = await directories.GetActiveChannelBindingAsync(viewedNodeId.Value, slot, cancellationToken).ConfigureAwait(false);
        return binding is not null && binding.NodeId == viewedNodeId && binding.ChannelId == channel.Id && binding.UnboundUtc is null
            ? SendReadiness.Ready : SendReadiness.StaleBinding;
    }
}
