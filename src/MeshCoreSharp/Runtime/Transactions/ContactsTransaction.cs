using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;
using MeshCoreSharp.Protocol.Packets;

namespace MeshCoreSharp.Runtime.Transactions;

internal sealed class ContactsTransaction : ICommandTransaction, IDisposable
{
    private readonly object _sync = new();
    private readonly TaskCompletionSource<CompanionPacket> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Contact> _contacts = [];
    private readonly TimeProvider _clock;
    private readonly ITimer _timer;
    private readonly TimeSpan _inactivityTimeout;
    private readonly TimeSpan _absoluteTimeout;
    private readonly long _startedAt;
    private long _lastActivity;
    private bool _receiving;
    private bool _disposed;

    public ContactsTransaction(TimeSpan inactivityTimeout, TimeSpan absoluteTimeout, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _inactivityTimeout = inactivityTimeout;
        _absoluteTimeout = absoluteTimeout;
        _startedAt = _lastActivity = _clock.GetTimestamp();
        _timer = _clock.CreateTimer(OnTimeout, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        ScheduleTimeout();
    }

    public Task<CompanionPacket> Completion => _completion.Task;

    public IReadOnlyList<Contact> GetContacts()
    {
        lock (_sync)
            return Array.AsReadOnly(_contacts.ToArray());
    }

    public bool TryAccept(CompanionPacket packet)
    {
        lock (_sync)
        {
            if (_disposed || _completion.Task.IsCompleted)
                return false;

            switch (packet)
            {
                case ErrorPacket:
                    _completion.TrySetResult(packet);
                    return true;
                case ContactStartPacket when !_receiving:
                    _receiving = true;
                    break;
                case ContactPacket contact when _receiving:
                    _contacts.Add(contact.Contact);
                    break;
                case ContactEndPacket when _receiving:
                    _completion.TrySetResult(packet);
                    return true;
                case ContactStartPacket or ContactPacket or ContactEndPacket:
                    _completion.TrySetException(new MeshCoreProtocolException(
                        $"Unexpected {packet.Type} in contacts stream ({(_receiving ? "receiving" : "waiting for start")})."));
                    return true;
                default:
                    return false;
            }

            _lastActivity = _clock.GetTimestamp();
            ScheduleTimeout();
            return true;
        }
    }

    public bool TryFailMalformed(PacketType type, Exception exception)
    {
        if (type is not (PacketType.ContactStart or PacketType.Contact or PacketType.ContactEnd))
            return false;

        Fail(exception);
        return true;
    }

    public void Fail(Exception exception)
    {
        lock (_sync)
            _completion.TrySetException(exception);
    }

    private void OnTimeout(object? state)
    {
        lock (_sync)
        {
            if (_disposed || _completion.Task.IsCompleted)
                return;

            var now = _clock.GetTimestamp();
            if (_clock.GetElapsedTime(_startedAt, now) >= _absoluteTimeout)
                _completion.TrySetException(new MeshCoreTimeoutException("GetContactsAsync (absolute)", _absoluteTimeout));
            else if (_clock.GetElapsedTime(_lastActivity, now) >= _inactivityTimeout)
                _completion.TrySetException(new MeshCoreTimeoutException("GetContactsAsync (inactivity)", _inactivityTimeout));
            else
                ScheduleTimeout(); // A previously queued timer callback may run after a reset.
        }
    }

    private void ScheduleTimeout()
    {
        var now = _clock.GetTimestamp();
        var idle = _inactivityTimeout - _clock.GetElapsedTime(_lastActivity, now);
        var absolute = _absoluteTimeout - _clock.GetElapsedTime(_startedAt, now);
        var remaining = idle < absolute ? idle : absolute;
        _timer.Change(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _timer.Dispose();
        }
    }
}
