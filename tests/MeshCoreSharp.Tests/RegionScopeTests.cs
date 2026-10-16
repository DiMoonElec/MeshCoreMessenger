using MeshCoreSharp;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol.Commands;

internal static class RegionScopeTests
{
    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("REGION3 scope wire modes and cancellation before TX", BeforeTx),
        ("REGION3 SET error never invokes SEND and releases gate", SetError),
        ("REGION3 canceled SEND cleans up independently of caller", CleanupCancellation),
        ("REGION3 accepted SEND survives reset error and closes admission", CleanupFailure),
        ("REGION3 late OK cannot release queued command after SET timeout", LateOk),
        ("REGION3 disconnect releases operation and gate waiters", Disconnect),
        ("REGION3 queued cancellation and cancellation after SEND acceptance", QueuedAndAcceptedCancellation),
        ("REGION3 cancellation during SET invalidates instead of sending RESET", SetCancellation),
        ("REGION3 reset timeout preserves accepted result", ResetTimeout),
    ];
    private static void Check(bool condition) { if (!condition) throw new Exception("Scope lifecycle regression"); }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T error) { return error; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
    private static MeshCoreClient Client(TestTransport transport) => new(transport,
        new MeshCoreClientOptions { AutoReceiveMessages = false, CommandTimeout = TimeSpan.FromMilliseconds(250) });
    private static async Task Start(MeshCoreClient client) { await client.ConnectAsync(); await client.StartAsync(); }
    private static async Task<byte[]> Sent(TestTransport transport) =>
        await transport.Sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    private static async Task BeforeTx()
    {
        Check(CompanionCommands.SetFloodScope(FloodScopeSelection.InheritDefault).SequenceEqual(new byte[] { 54, 0 }));
        Check(CompanionCommands.SetFloodScope(FloodScopeSelection.ForceUnscoped).SequenceEqual(new byte[] { 54, 1 }));
        var key = RegionKeys.DerivePublic("foo");
        var command = CompanionCommands.SetFloodScope(FloodScopeSelection.ExplicitRegion(key));
        Check(command.Length == 18 && command.AsSpan(2).SequenceEqual(key.ToArray()));
        var transport = new TestTransport(); await using var client = Client(transport); await Start(client);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Throws<OperationCanceledException>(() => client.ExecuteScopeOperationAsync(FloodScopeSelection.ForceUnscoped,
            _ => Task.FromResult(1), cts.Token));
        Check(!transport.Sent.Reader.TryRead(out _));
    }
    private static async Task SetError()
    {
        var transport = new TestTransport(); await using var client = Client(transport); await Start(client);
        var invoked = false;
        var first = client.ExecuteScopeOperationAsync(FloodScopeSelection.ForceUnscoped,
            _ => { invoked = true; return Task.FromResult(1); });
        await Sent(transport); transport.Emit([1, 6]);
        await Throws<MeshCoreCommandException>(async () => await first); Check(!invoked);
        var next = client.ExecuteScopeOperationAsync(FloodScopeSelection.InheritDefault, _ => Task.FromResult(2));
        await Sent(transport); transport.Emit([0]);
        Check((await Sent(transport)).SequenceEqual(new byte[] { 54, 0 })); transport.Emit([0]);
        Check((await next).AcceptedSend == 2);
    }
    private static async Task CleanupCancellation()
    {
        var transport = new TestTransport(); await using var client = Client(transport); await Start(client);
        using var cts = new CancellationTokenSource();
        var pending = client.ExecuteScopeOperationAsync<int>(FloodScopeSelection.ForceUnscoped,
            _ => { cts.Cancel(); return Task.FromCanceled<int>(cts.Token); }, cts.Token);
        await Sent(transport); transport.Emit([0]);
        Check((await Sent(transport)).SequenceEqual(new byte[] { 54, 0 })); transport.Emit([0]);
        await Throws<OperationCanceledException>(async () => await pending);
        Check(client.IsConnected && client.IsStarted);
    }
    private static async Task CleanupFailure()
    {
        var transport = new TestTransport(); await using var client = Client(transport); await Start(client);
        var pending = client.ExecuteScopeOperationAsync(FloodScopeSelection.ForceUnscoped, _ => Task.FromResult("accepted"));
        await Sent(transport); transport.Emit([0]); await Sent(transport); transport.Emit([1, 4]);
        var result = await pending;
        Check(result.AcceptedSend == "accepted" && result.CleanupError is MeshCoreCommandException && !result.ResetAccepted);
        Check(!client.IsStarted);
        await Throws<InvalidOperationException>(() => client.SetDeviceTimeAsync(DateTimeOffset.UtcNow));
    }
    private static async Task LateOk()
    {
        var transport = new TestTransport(); await using var client = Client(transport); await Start(client);
        var invoked = false;
        var pending = client.ExecuteScopeOperationAsync(FloodScopeSelection.ForceUnscoped,
            _ => { invoked = true; return Task.FromResult(1); });
        await Sent(transport);
        var queued = client.SetDeviceTimeAsync(DateTimeOffset.UtcNow);
        await Throws<MeshCoreTimeoutException>(async () => await pending);
        transport.Emit([0]);
        await Throws<MeshCoreTransportException>(async () => await queued);
        Check(!invoked && !transport.Sent.Reader.TryRead(out _));
        await client.ConnectAsync(); await client.StartAsync();
        var read = client.GetDefaultFloodScopeAsync(); await Sent(transport); transport.Emit([0x1C]);
        Check((await read).State == NodeFloodScopeState.KnownEmpty);
    }
    private static async Task Disconnect()
    {
        var transport = new TestTransport(); await using var client = Client(transport); await Start(client);
        var first = client.ExecuteScopeOperationAsync(FloodScopeSelection.ForceUnscoped, _ => Task.FromResult(1));
        await Sent(transport);
        var queued = client.ExecuteScopeOperationAsync(FloodScopeSelection.InheritDefault, _ => Task.FromResult(2));
        await client.DisconnectAsync();
        try { await first; throw new Exception("First operation unexpectedly succeeded"); }
        catch (Exception error) when (error is MeshCoreTransportException or OperationCanceledException) { }
        await Throws<OperationCanceledException>(async () => await queued);
        Check(!transport.Sent.Reader.TryRead(out _));
    }

    private static async Task QueuedAndAcceptedCancellation()
    {
        var transport = new TestTransport(); await using var client = Client(transport); await Start(client);
        using var acceptedCts = new CancellationTokenSource();
        var first = client.ExecuteScopeOperationAsync(FloodScopeSelection.InheritDefault,
            _ => { acceptedCts.Cancel(); return Task.FromResult(42); }, acceptedCts.Token);
        await Sent(transport);
        using var queuedCts = new CancellationTokenSource();
        var queued = client.ExecuteScopeOperationAsync(FloodScopeSelection.ForceUnscoped, _ => Task.FromResult(0), queuedCts.Token);
        queuedCts.Cancel(); await Throws<OperationCanceledException>(async () => await queued);
        transport.Emit([0]); await Sent(transport); transport.Emit([0]);
        Check((await first).AcceptedSend == 42 && !transport.Sent.Reader.TryRead(out _));
    }

    private static async Task SetCancellation()
    {
        var transport = new TestTransport(); await using var client = Client(transport); await Start(client);
        using var cts = new CancellationTokenSource();
        var first = client.ExecuteScopeOperationAsync<int>(FloodScopeSelection.ForceUnscoped,
            _ => throw new Exception("Must not SEND"), cts.Token);
        await Sent(transport); cts.Cancel();
        await Throws<OperationCanceledException>(async () => await first);
        Check(!client.IsStarted && !transport.Sent.Reader.TryRead(out _));
    }

    private static async Task ResetTimeout()
    {
        var transport = new TestTransport(); await using var client = Client(transport); await Start(client);
        var first = client.ExecuteScopeOperationAsync(FloodScopeSelection.ForceUnscoped, _ => Task.FromResult(42));
        await Sent(transport); transport.Emit([0]); await Sent(transport);
        var result = await first;
        Check(result.AcceptedSend == 42 && result.CleanupError is MeshCoreTimeoutException or OperationCanceledException);
        Check(!client.IsStarted && !transport.Sent.Reader.TryRead(out _));
    }
}
