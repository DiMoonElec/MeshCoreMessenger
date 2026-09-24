using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;
using MeshCoreSharp.Protocol.Commands;
using MeshCoreSharp.Protocol.Packets;
using MeshCoreSharp.Runtime.Transactions;
using MeshCoreSharp.Transport;

namespace MeshCoreSharp.Runtime;

internal sealed class CommandDispatcher : IDisposable
{
    private readonly IMeshCoreTransport _transport;
    private readonly PacketRouter _router;
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private bool _disposed;

    public CommandDispatcher(IMeshCoreTransport transport, PacketRouter router)
    {
        _transport = transport;
        _router = router;
    }

    public async Task<TPacket> SendAsync<TPacket>(
        CommandType commandType,
        ReadOnlyMemory<byte> command,
        string operationName,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        Func<PacketType, bool>? acceptsType = null,
        Func<TPacket, bool>? acceptsPacket = null,
        Action<TPacket>? onAccepted = null)
        where TPacket : CompanionPacket
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (command.Length is <= 0 or > ProtocolLimits.CurrentFirmwareMaxFrameSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(command),
                $"Companion command must be 1..{ProtocolLimits.CurrentFirmwareMaxFrameSize} bytes.");
        }

        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var transaction = new SinglePacketTransaction<TPacket>(acceptsType, acceptsPacket, onAccepted);

        try
        {
            // Critical protocol ordering: install the response matcher before
            // writing the command. A local companion can answer immediately.
            _router.Register(transaction);

            await _transport.SendAsync(command, cancellationToken).ConfigureAwait(false);

            CompanionPacket response;
            try
            {
                response = await transaction.Completion
                    .WaitAsync(timeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new MeshCoreTimeoutException(operationName, timeout);
            }

            if (response is ErrorPacket error)
                throw new MeshCoreCommandException(commandType, error.ErrorCode);

            if (response is not TPacket typed)
            {
                throw new MeshCoreProtocolException(
                    $"Operation '{operationName}' received unexpected packet type 0x{response.RawType:X2}.");
            }

            return typed;
        }
        finally
        {
            _router.Unregister(transaction);
            _commandGate.Release();
        }
    }

    public Task<MessageQueuePacket> SyncNextMessageAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        SendAsync<MessageQueuePacket>(CommandType.SyncNextMessage, CompanionCommands.SyncNextMessage(),
            nameof(SyncNextMessageAsync), timeout, cancellationToken, MessageQueuePacket.IsResponseType);

    public async Task<IReadOnlyList<Contact>> SendContactsAsync(
        ReadOnlyMemory<byte> command,
        TimeSpan inactivityTimeout,
        TimeSpan absoluteTimeout,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ContactsTransaction? transaction = null;
        try
        {
            transaction = new ContactsTransaction(inactivityTimeout, absoluteTimeout);
            _router.Register(transaction);
            await _transport.SendAsync(command, cancellationToken).ConfigureAwait(false);

            var response = await transaction.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (response is ErrorPacket error)
                throw new MeshCoreCommandException(CommandType.GetContacts, error.ErrorCode);

            return transaction.GetContacts();
        }
        finally
        {
            if (transaction is not null)
            {
                _router.Unregister(transaction);
                transaction.Dispose();
            }
            _commandGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _commandGate.Dispose();
    }
}
