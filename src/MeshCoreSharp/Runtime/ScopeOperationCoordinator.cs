using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;
using MeshCoreSharp.Protocol.Commands;
using MeshCoreSharp.Protocol.Packets;

namespace MeshCoreSharp.Runtime;

internal sealed record ScopeOperationResult<T>(T AcceptedSend, Exception? CleanupError)
{
    public bool ResetAccepted => CleanupError is null;
}

/// <summary>Owns SET / immediate SEND / RESET. Never holds a gate while awaiting radio delivery.</summary>
internal sealed class ScopeOperationCoordinator(CommandDispatcher dispatcher, TimeSpan timeout, CancellationToken sessionToken)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal async Task<T> WithGateAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, sessionToken);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            return await action(linked.Token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    internal Task<ScopeOperationResult<T>> ExecuteAsync<T>(FloodScopeSelection scope,
        Func<CancellationToken, Task<T>> send, CancellationToken token) =>
        WithGateAsync(ct => ExecuteUnderGateAsync(scope, send, ct), token);

    internal async Task<ScopeOperationResult<T>> ExecuteUnderGateAsync<T>(FloodScopeSelection scope,
        Func<CancellationToken, Task<T>> send, CancellationToken ct)
    {
        await SetAsync(scope, ct).ConfigureAwait(false);
        T accepted;
        try
        {
            ct.ThrowIfCancellationRequested();
            accepted = await send(ct).ConfigureAwait(false);
        }
        catch
        {
            // Definite send rejection or cancellation before SEND still needs cleanup.
            // Ambiguous dispatcher failures have already stopped this session.
            await TryResetAsync().ConfigureAwait(false);
            throw;
        }
        return new ScopeOperationResult<T>(accepted, await TryResetAsync().ConfigureAwait(false));
    }

    private Task<OkPacket> SetAsync(FloodScopeSelection scope, CancellationToken ct) =>
        dispatcher.SendAsync<OkPacket>(CommandType.SetFloodScopeKey, CompanionCommands.SetFloodScope(scope),
            "SetFloodScope", timeout, ct, type => type == PacketType.Ok, invalidateOnUncertainResponse: true);

    private async Task<Exception?> TryResetAsync()
    {
        if (sessionToken.IsCancellationRequested) return new OperationCanceledException(sessionToken);
        // Independent of caller cancellation; timeout bounds cleanup on a healthy stream.
        using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        cleanup.CancelAfter(timeout);
        try { await SetAsync(FloodScopeSelection.InheritDefault, cleanup.Token).ConfigureAwait(false); return null; }
        catch (Exception error)
        {
            if (!sessionToken.IsCancellationRequested) dispatcher.InvalidateSession(error);
            return error;
        }
    }
}
