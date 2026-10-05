using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreSharp;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;

namespace MeshCoreMessenger.Core.Application;

public sealed record SessionCommandOwner(Guid NodeId, Guid SessionId, long Generation, string SenderName);
public abstract record SessionCommandTarget;
public sealed record NodeCommandTarget : SessionCommandTarget;
public sealed record ContactCommandTarget(ReadOnlyMemory<byte> PublicKey) : SessionCommandTarget;
public sealed record ChannelCommandTarget(OutgoingRecipient Recipient) : SessionCommandTarget;
public sealed record ChannelSlotCommandTarget(byte Slot) : SessionCommandTarget;

public interface ISessionCommandGateway
{
    SessionCommandLease Acquire(Guid nodeId, SessionCommandTarget target, CancellationToken cancellationToken = default);
}

/// <summary>Application-only command access. A lease never exposes a client or consumes session events.</summary>
public sealed class SessionCommandGateway(
    IDirectoryStore directories, IConversationDirectoryReader directory,
    IOutgoingMessageStore messages, IDurableOutgoingWrites outgoing, TimeProvider timeProvider) : ISessionCommandGateway
{
    private readonly object _gate = new();
    private SessionCommandScope? _active;

    public SessionCommandLease Acquire(Guid nodeId, SessionCommandTarget target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_active is null || _active.Owner.NodeId != nodeId) throw new InvalidOperationException("No Online command session for this node.");
            return _active.Acquire(CopyTarget(target), cancellationToken);
        }
    }

    private static SessionCommandTarget CopyTarget(SessionCommandTarget target) => target switch
    {
        NodeCommandTarget => new NodeCommandTarget(),
        ContactCommandTarget contact when contact.PublicKey.Length == ProtocolLimits.PublicKeySize => new ContactCommandTarget(contact.PublicKey.ToArray()),
        ChannelCommandTarget channel when channel.Recipient.Kind == ConversationKind.Channel && channel.Recipient.Identity.Length == 32 &&
            channel.Recipient.ChannelBindingId is not null && channel.Recipient.Slot is not null && channel.Recipient.BindingGeneration is > 0 =>
            new ChannelCommandTarget(channel.Recipient with { Identity = channel.Recipient.Identity.ToArray() }),
        ChannelSlotCommandTarget slot => slot,
        _ => throw new ArgumentException("An exact command target is required.", nameof(target)),
    };

    internal SessionCommandScope Open(SessionCommandOwner owner, ICompanionClient client)
    {
        lock (_gate)
        {
            if (_active is not null && !_active.OperationsCompleted) throw new InvalidOperationException("The previous command scope has not drained.");
            if (outgoing.IsPaused) throw new OutgoingPersistenceException(new InvalidOperationException("Outgoing writes are paused."));
            return _active = new SessionCommandScope(owner, client, directories, directory, messages, outgoing, timeProvider);
        }
    }

    internal void Close(SessionCommandScope scope)
    {
        lock (_gate)
        {
            scope.Close();
            // Keep the old scope until its operations drain; Acquire rejects its closed admission.
        }
    }
}

internal sealed class SessionCommandScope(
    SessionCommandOwner owner, ICompanionClient client, IDirectoryStore directories,
    IConversationDirectoryReader directory, IOutgoingMessageStore messages,
    IDurableOutgoingWrites outgoing, TimeProvider timeProvider)
{
    internal readonly object Gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<SessionCommandLease> _leases = [];
    private bool _closed;
    private int _disposed;
    private readonly HashSet<Guid> _attempts = [];
    private bool _operationsCompleted;
    internal bool OperationsCompleted { get { lock (Gate) return _operationsCompleted; } }
    private Task _cancellation = Task.CompletedTask;
    internal SessionCommandOwner Owner { get; } = owner;
    internal IOutgoingMessageStore Messages { get; } = messages;
    internal IDurableOutgoingWrites Outgoing { get; } = outgoing;
    internal TimeProvider TimeProvider { get; } = timeProvider;
    internal DateTimeOffset? EstimateAcknowledgementDeadline(uint suggested) => client.GetAcknowledgementWaitDuration(suggested) is { } wait
        ? TimeProvider.GetUtcNow().Add(wait) : null;

    internal SessionCommandLease Acquire(SessionCommandTarget target, CancellationToken cancellationToken)
    {
        lock (Gate)
        {
            EnsureOpen();
            var lease = new SessionCommandLease(this, target, _stop.Token, cancellationToken);
            _leases.Add(lease);
            return lease;
        }
    }

    internal void EnsureOpen()
    {
        if (_closed) throw new InvalidOperationException("Command lease belongs to a closed session.");
        if (Outgoing.IsPaused) throw new OutgoingPersistenceException(new InvalidOperationException("Outgoing writes are paused."));
    }

    internal Task<T> Invoke<T>(SessionCommandLease lease, Func<ICompanionClient, CancellationToken, Task<T>> call, bool sending = false)
    {
        lock (Gate)
        {
            EnsureOpen();
            lease.EnsureUsable();
            lease.Token.ThrowIfCancellationRequested();
            lease.MarkInvoked(sending);
            // Invocation and closing admission share this lock. No async result/ACK is awaited here.
            return call(client, lease.Token);
        }
    }

    internal async Task ValidateSendTargetAsync(SessionCommandLease lease)
    {
        if (lease.Target is ContactCommandTarget contact)
        {
            var contacts = await directories.GetCurrentContactsByPrefixAsync(Owner.NodeId,
                contact.PublicKey[..ProtocolLimits.MessageContactPrefixSize], lease.Token).ConfigureAwait(false);
            if (contacts.Count != 1 || contacts[0].ContactType != (int)AdvertisementType.Chat ||
                !contacts[0].PresentOnNode || !contacts[0].PublicKey.AsSpan().SequenceEqual(contact.PublicKey.Span))
                throw new InvalidOperationException("Contact is missing, unsupported or ambiguous.");
        }
        else if (lease.Target is ChannelCommandTarget channel)
        {
            var recipient = channel.Recipient;
            var binding = await directories.GetActiveChannelBindingAsync(Owner.NodeId, recipient.Slot!.Value, lease.Token).ConfigureAwait(false);
            var details = await directory.GetChannelDetailsAsync(Owner.NodeId, recipient.Identity, lease.Token).ConfigureAwait(false);
            if (binding is null || details is null || binding.Id != recipient.ChannelBindingId || binding.Generation != recipient.BindingGeneration ||
                binding.ChannelId != details.Id || binding.NodeId != Owner.NodeId || binding.UnboundUtc is not null)
                throw new InvalidOperationException("Channel binding has changed.");
        }
        else throw new InvalidOperationException("Sending requires a captured contact or channel binding.");
    }

    internal void ClaimAttempt(Guid attemptId)
    {
        if (!_attempts.Add(attemptId)) throw new InvalidOperationException("Another lease already owns this attempt.");
    }
    internal void Release(SessionCommandLease lease, Guid? attemptId)
    {
        lock (Gate)
        {
            _leases.Remove(lease);
            if (attemptId is { } id) _attempts.Remove(id);
        }
    }

    internal void Close()
    {
        lock (Gate)
        {
            if (_closed) return;
            _closed = true;
            // CancelAsync keeps user/library cancellation callbacks outside the supervisor control loop.
            var cancellation = _stop.CancelAsync();
            var leases = _leases.ToArray();
            _cancellation = Task.Run(async () =>
            {
                Exception? cancellationError = null;
                try { await cancellation.ConfigureAwait(false); }
                catch (Exception exception) { cancellationError = exception; }
                foreach (var lease in leases)
                {
                    try { await lease.DisposeAsync().ConfigureAwait(false); }
                    catch (OutgoingPersistenceException) { /* The durable tracker retains this write and exposes it at the barrier. */ }
                }
                if (cancellationError is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cancellationError).Throw();
            });
        }
    }

    internal async Task DrainAsync(CancellationToken cancellationToken)
    {
        Close();
        Task[] completions;
        lock (Gate) completions = _leases.Select(lease => lease.Completion).Append(_cancellation).ToArray();
        var drain = Task.WhenAll(completions);
        try { await drain.WaitAsync(cancellationToken).ConfigureAwait(false); }
        finally
        {
            if (drain.IsCompleted)
            {
                lock (Gate) _operationsCompleted = true;
                if (Interlocked.Exchange(ref _disposed, 1) == 0) _stop.Dispose();
            }
        }
        await Outgoing.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
