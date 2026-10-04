using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using Microsoft.Data.Sqlite;
using MeshCoreSharp;
using MeshCoreSharp.Models;

namespace MeshCoreMessenger.Core.Application;

/// <summary>
/// Core workflow ownership for one immutable session/target. Use RunAsync for the whole workflow,
/// including its status writes. Observers are owned here, independently of UI selection.
/// </summary>
public sealed class SessionCommandLease : IAsyncDisposable
{
    private readonly SessionCommandScope _scope;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationToken _token;
    private readonly HashSet<Task> _work = [];
    private readonly SemaphoreSlim _statusGate = new(1, 1);
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _disposeTask;
    private bool _finished;
    private bool _sendInvoked;
    private bool _sendingCommitted;
    private StoredOutgoingMessage? _message;
    private OutgoingAttemptSnapshot? _attempt;
    private SendAttemptState _plannedState;
    private AckExpectation _expectation;
    private DateTimeOffset _lastUtc;

    internal SessionCommandLease(SessionCommandScope scope, SessionCommandTarget target,
        CancellationToken sessionStop, CancellationToken callerStop)
    {
        _scope = scope;
        Target = target;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(sessionStop, callerStop);
        _token = _lifetime.Token;
    }

    public SessionCommandOwner Owner => _scope.Owner;
    public SessionCommandTarget Target { get; }
    public CancellationToken Token => _token;
    public bool WasInvoked { get; private set; }
    internal Task Completion => _completion.Task;

    /// <summary>Registers a complete application workflow before it starts, including detached ACK work.</summary>
    public Task<T> RunAsync<T>(Func<SessionCommandLease, CancellationToken, Task<T>> workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        return OwnAsync(() => workflow(this, Token), requireAdmission: true);
    }

    public Task ObserveAsync(Func<SessionCommandLease, CancellationToken, Task> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        return OwnAsync(async () => { await observer(this, Token).ConfigureAwait(false); return true; }, requireAdmission: true);
    }

    /// <summary>Binds only a real persisted Prepared attempt of this exact session and recipient.</summary>
    public Task BindOutgoingAsync(Guid messageId, Guid attemptId) => OwnAsync(async () =>
    {
        var message = await _scope.Messages.GetAsync(Owner.NodeId, messageId, Token).ConfigureAwait(false);
        var attempts = await _scope.Messages.GetAttemptsAsync(Owner.NodeId, messageId, Token).ConfigureAwait(false);
        var attempt = attempts.SingleOrDefault(item => item.Id == attemptId);
        if (attempt is null || attempt.SessionId != Owner.SessionId || message.SessionId != Owner.SessionId || attempt.State != SendAttemptState.Prepared || !Matches(message.Recipient))
            throw new InvalidOperationException("Prepared attempt belongs to another session/recipient or has already started.");
        lock (_scope.Gate)
        {
            _scope.EnsureOpen();
            EnsureUsable();
            if (_message is not null) throw new InvalidOperationException("A lease already owns an outgoing attempt.");
            _scope.ClaimAttempt(attemptId);
            _message = message;
            _attempt = attempt;
            _plannedState = attempt.State;
            _lastUtc = attempt.StartedUtc;
        }
        return true;
    }, requireAdmission: true);

    public Task<bool> TransitionAsync(SendAttemptState expectedState, SendAttemptState state,
        AckExpectation expectation = AckExpectation.LegacyUnknown, long? wireTimestamp = null,
        ReadOnlyMemory<byte>? expectedAck = null, int? roundTripMilliseconds = null, string? errorCode = null) => OwnAsync(async () =>
    {
        await _statusGate.WaitAsync().ConfigureAwait(false);
        try
        {
            OutgoingAttemptTransition transition;
            lock (_scope.Gate)
            {
                if (_attempt is null) throw new InvalidOperationException("Bind a persisted attempt before recording status.");
                if (_plannedState != expectedState) return false;
                if (!SendAttemptTransitions.Allows(_plannedState, _expectation, state)) throw new InvalidOperationException("Invalid outgoing state transition.");
                if (roundTripMilliseconds < 0 || !Enum.IsDefined(expectation)) throw new ArgumentException("Invalid attempt metadata.");
                if (state == SendAttemptState.Accepted && (expectation == AckExpectation.LegacyUnknown ||
                    expectation == AckExpectation.Expected && (expectedAck is null || expectedAck.Value.Length != sizeof(uint))))
                    throw new ArgumentException("Accepted requires explicit ACK expectation and the expected tag.");
                if (state == SendAttemptState.Accepted && !_sendInvoked) throw new InvalidOperationException("Accepted requires an actual send API invocation.");
                if (state == SendAttemptState.Sending) { _scope.EnsureOpen(); Token.ThrowIfCancellationRequested(); }
                transition = NewTransition(expectedState, state, expectation, wireTimestamp, expectedAck, roundTripMilliseconds, errorCode);
                // Retain logical ordering even if this write pauses. Cleanup queues after it, never ahead of it.
                _plannedState = state;
                if (state == SendAttemptState.Accepted) _expectation = expectation;
            }
            var applied = await _scope.Outgoing.SaveAsync(transition, CancellationToken.None).ConfigureAwait(false);
            if (state == SendAttemptState.Sending && applied) { lock (_scope.Gate) _sendingCommitted = true; }
            return applied;
        }
        finally { _statusGate.Release(); }
    }, requireAdmission: false);

    internal async Task<bool> IsDeliveryCommittedAsync()
    {
        if (_attempt is null || _message is null) return false;
        var attempts = await _scope.Messages.GetAttemptsAsync(Owner.NodeId, _message.MessageId, CancellationToken.None).ConfigureAwait(false);
        return attempts.Any(attempt => attempt.Id == _attempt.Id && attempt.State == SendAttemptState.Delivered);
    }

    public Task<TextMessageSendResult> SendTextAsync() => OwnAsync(async () =>
    {
        if (Target is not ContactCommandTarget contact) throw new InvalidOperationException("Private sending requires a contact target.");
        await ValidateTextSendAsync().ConfigureAwait(false);
        return await _scope.Invoke(this, (client, token) => client.SendTextAsync(contact.PublicKey, _message!.TransmissionText, token), sending: true).ConfigureAwait(false);
    }, requireAdmission: true);

    public Task<ChannelMessageSendResult> SendChannelTextAsync() => OwnAsync(async () =>
    {
        if (Target is not ChannelCommandTarget channel) throw new InvalidOperationException("Channel sending requires a binding target.");
        await ValidateTextSendAsync().ConfigureAwait(false);
        return await _scope.Invoke(this, (client, token) => client.SendChannelTextAsync(channel.Recipient.Slot!.Value, _message!.TransmissionText, token), sending: true).ConfigureAwait(false);
    }, requireAdmission: true);

    public Task SendAdvertisementAsync(AdvertisementMode mode)
    {
        if (Target is not NodeCommandTarget) throw new InvalidOperationException("Advertisement requires a node target.");
        return InvokeVoidAsync((client, token) => client.SendAdvertisementAsync(mode, token));
    }

    public Task AddOrUpdateContactAsync(ContactConfiguration contact)
    {
        ArgumentNullException.ThrowIfNull(contact);
        var captured = contact with { PublicKey = contact.PublicKey.ToArray(), OutPath = contact.OutPath.ToArray() };
        if (Target is not ContactCommandTarget target || !captured.PublicKey.Span.SequenceEqual(target.PublicKey.Span))
            throw new InvalidOperationException("Contact configuration does not match the captured target.");
        return InvokeVoidAsync((client, token) => client.AddOrUpdateContactAsync(captured, token));
    }
    public Task ResetPathAsync()
    {
        if (Target is not ContactCommandTarget contact) throw new InvalidOperationException("Route reset requires a contact target.");
        return InvokeVoidAsync((client, token) => client.ResetPathAsync(contact.PublicKey, token));
    }
    public Task RemoveContactAsync()
    {
        if (Target is not ContactCommandTarget contact) throw new InvalidOperationException("Removing a contact requires a contact target.");
        return InvokeVoidAsync((client, token) => client.RemoveContactAsync(contact.PublicKey, token));
    }
    public Task SetChannelAsync(string name, ReadOnlyMemory<byte> secret)
    {
        var slot = ChannelSlot();
        var captured = secret.ToArray(); // Secret exists only in this owned call; it is never persisted.
        return InvokeChannelMutationAsync((client, token) => client.SetChannelAsync(slot, name, captured, token));
    }
    public Task ClearChannelAsync()
    {
        var slot = ChannelSlot();
        return InvokeChannelMutationAsync((client, token) => client.ClearChannelAsync(slot, token));
    }
    public Task<IReadOnlyList<Contact>> GetContactsAsync() => OwnAsync(
        () => _scope.Invoke(this, (client, token) => client.GetContactsAsync(token)), requireAdmission: true);
    public Task<IReadOnlyList<ChannelInfo>> GetChannelsAsync() => OwnAsync(
        () => _scope.Invoke(this, (client, token) => client.GetChannelsAsync(token)), requireAdmission: true);

    private Task InvokeChannelMutationAsync(Func<ICompanionClient, CancellationToken, Task> call) => OwnAsync(async () =>
    {
        if (Target is ChannelCommandTarget) await _scope.ValidateSendTargetAsync(this).ConfigureAwait(false);
        await _scope.Invoke(this, async (client, token) => { await call(client, token).ConfigureAwait(false); return true; }).ConfigureAwait(false);
        return true;
    }, requireAdmission: true);

    private Task InvokeVoidAsync(Func<ICompanionClient, CancellationToken, Task> call) => OwnAsync(async () =>
    {
        await _scope.Invoke(this, async (client, token) => { await call(client, token).ConfigureAwait(false); return true; }).ConfigureAwait(false);
        return true;
    }, requireAdmission: true);

    private byte ChannelSlot() => Target switch
    {
        ChannelSlotCommandTarget slot => slot.Slot,
        ChannelCommandTarget channel => channel.Recipient.Slot!.Value,
        _ => throw new InvalidOperationException("Channel mutation requires a captured slot."),
    };

    private async Task ValidateTextSendAsync()
    {
        if (_message is null) throw new InvalidOperationException("Sending requires a persisted attempt.");
        var limit = Target is ChannelCommandTarget ? TextMessageValidator.GetChannelTextLimit(Owner.SenderName) : MeshCoreSharp.Protocol.ProtocolLimits.MaxTextBytes;
        if (!TextMessageValidator.Validate(_message.TransmissionText, limit).IsValid) throw new InvalidOperationException("Stored text exceeds the current session's UTF-8 budget.");
        await _scope.ValidateSendTargetAsync(this).ConfigureAwait(false);
    }

    private bool Matches(OutgoingRecipient recipient) => Target switch
    {
        ContactCommandTarget contact => recipient.Kind == ConversationKind.Contact && recipient.Identity.Span.SequenceEqual(contact.PublicKey.Span),
        ChannelCommandTarget channel => recipient.Kind == ConversationKind.Channel && recipient.Identity.Span.SequenceEqual(channel.Recipient.Identity.Span) &&
            recipient.ChannelBindingId == channel.Recipient.ChannelBindingId && recipient.Slot == channel.Recipient.Slot && recipient.BindingGeneration == channel.Recipient.BindingGeneration,
        _ => false,
    };

    internal void EnsureUsable() { if (_finished) throw new ObjectDisposedException(nameof(SessionCommandLease)); }
    internal void MarkInvoked(bool sending)
    {
        if (sending)
        {
            if (_plannedState != SendAttemptState.Sending || !_sendingCommitted || _sendInvoked)
                throw new InvalidOperationException("One text API invocation requires a committed Sending attempt.");
            _sendInvoked = true;
        }
        WasInvoked = true;
    }

    private Task<T> OwnAsync<T>(Func<Task<T>> action, bool requireAdmission)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_scope.Gate)
        {
            EnsureUsable();
            if (requireAdmission) { _scope.EnsureOpen(); Token.ThrowIfCancellationRequested(); }
            if (_disposeTask is not null && _work.Count == 0) throw new ObjectDisposedException(nameof(SessionCommandLease));
            _work.Add(completion.Task);
        }
        _ = Task.Run(async () =>
        {
            try { completion.TrySetResult(await action().ConfigureAwait(false)); }
            catch (OperationCanceledException exception) { completion.TrySetCanceled(exception.CancellationToken); }
            catch (Exception exception)
            {
                if (exception is SqliteException or DatabaseStorageException) _scope.Outgoing.ReportFailure(exception);
                completion.TrySetException(exception);
                _ = completion.Task.Exception; // Detached observers are owned and their exceptions are observed here.
            }
            finally { lock (_scope.Gate) _work.Remove(completion.Task); }
        });
        return completion.Task;
    }

    public ValueTask DisposeAsync()
    {
        lock (_scope.Gate) return new ValueTask(_disposeTask ??= Task.Run(FinishAsync));
    }

    private async Task FinishAsync()
    {
        try
        {
            while (true)
            {
                Task[] work;
                lock (_scope.Gate)
                {
                    work = _work.ToArray();
                    if (work.Length == 0) { _finished = true; break; }
                }
                try { await Task.WhenAll(work).ConfigureAwait(false); }
                catch { /* Caller owns the operation error; cleanup still records its uncertainty. */ }
            }
            if (_attempt is not null && (_plannedState == SendAttemptState.Sending || _plannedState == SendAttemptState.Accepted && _expectation == AckExpectation.Expected))
            {
                var state = !_sendInvoked && _plannedState == SendAttemptState.Sending ? SendAttemptState.Failed : SendAttemptState.Unknown;
                await _scope.Outgoing.SaveAsync(NewTransition(_plannedState, state, errorCode: _sendInvoked ? "SessionCommandIncomplete" : "LeaseClosedBeforeInvocation"), CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _scope.Release(this, _attempt?.Id);
            _lifetime.Dispose();
            _statusGate.Dispose();
            _completion.TrySetResult();
        }
    }

    private OutgoingAttemptTransition NewTransition(SendAttemptState expected, SendAttemptState state,
        AckExpectation expectation = AckExpectation.LegacyUnknown, long? wireTimestamp = null,
        ReadOnlyMemory<byte>? expectedAck = null, int? roundTripMilliseconds = null, string? errorCode = null)
    {
        var now = _scope.TimeProvider.GetUtcNow();
        if (now > _lastUtc) _lastUtc = now;
        return new(Owner.NodeId, _message!.MessageId, _attempt!.Id, Owner.SessionId, expected, state,
            _lastUtc, expectation, wireTimestamp, expectedAck, roundTripMilliseconds, errorCode);
    }
}
