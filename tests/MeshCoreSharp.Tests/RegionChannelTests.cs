using MeshCoreSharp;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;

internal static class RegionChannelTests
{
    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("REGION4 concurrent channels retain distinct scopes and timestamps", ConcurrentChannels),
        ("REGION4 legacy private and default mutation cannot interleave scope operation", PrivateAndDefault),
        ("REGION4 force-unscoped and accepted send with failed cleanup", UnscopedAndCleanup),
        ("REGION4 old firmware retains legacy sends and rejects explicit scope", OldFirmware),
        ("REGION4 unsupported SET never silently downgrades explicit region", Unsupported),
        ("REGION4 safe reads and push continue during scope operation", SafeRead),
    ];
    internal static void Check(bool condition) { if (!condition) throw new Exception("Region channel regression"); }
    internal static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T error) { return error; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
    internal static async Task<byte[]> Sent(TestTransport transport) =>
        await transport.Sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    internal static MeshCoreClient Client(TestTransport transport) => new(transport,
        new MeshCoreClientOptions { AutoReceiveMessages = false, CommandTimeout = TimeSpan.FromSeconds(2) });
    internal static async Task Start(MeshCoreClient client, TestTransport transport, byte version = 14)
    {
        await client.ConnectAsync(); await client.StartAsync();
        var query = client.GetDeviceInfoAsync(); Check((await Sent(transport))[0] == 22);
        var device = new byte[80]; device[0] = 13; device[1] = version; transport.Emit(device); await query;
    }
    internal static async Task CompleteChannel(TestTransport transport, FloodScopeSelection scope, byte index)
    {
        var set = await Sent(transport);
        Check(set[0] == 54 && set[1] == (scope.Mode == FloodScopeMode.ForceUnscoped ? 1 : 0));
        if (scope.Key is not null) Check(set.AsSpan(2).SequenceEqual(scope.Key.ToArray()));
        transport.Emit([0]);
        var send = await Sent(transport); Check(send[0] == 3 && send[2] == index); transport.Emit([0]);
        Check((await Sent(transport)).SequenceEqual(new byte[] { 54, 0 })); transport.Emit([0]);
    }
    private static async Task ConcurrentChannels()
    {
        var transport = new TestTransport(); await using var client = Client(transport); await Start(client, transport);
        var a = FloodScopeSelection.ExplicitRegion(RegionKeys.DerivePublic("A"));
        var b = FloodScopeSelection.ExplicitRegion(RegionKeys.DerivePublic("B"));
        var first = client.SendChannelTextAsync(0, "first", 123, a);
        var second = client.SendChannelTextAsync(1, "second", 124, b);
        await CompleteChannel(transport, a, 0); Check((await first).Timestamp == 123);
        await CompleteChannel(transport, b, 1); Check((await second).Timestamp == 124);
    }
    private static async Task PrivateAndDefault()
    {
        var transport = new TestTransport(); await using var client = Client(transport); await Start(client, transport);
        var first = client.SendChannelTextAsync(0, "first", 1, FloodScopeSelection.ForceUnscoped);
        Check((await Sent(transport)).SequenceEqual(new byte[] { 54, 1 }));
        var privateSend = client.SendTextAsync(new byte[32], "private", 2, 0);
        var mutation = client.SetDefaultFloodScopeAsync(RegionDefinition.Public("default"));
        Check(!transport.Sent.Reader.TryRead(out _));
        transport.Emit([0]); Check((await Sent(transport))[0] == 3); transport.Emit([0]);
        Check((await Sent(transport)).SequenceEqual(new byte[] { 54, 0 })); transport.Emit([0]); await first;
        // Private and mutation both wait outside CommandGate; either may acquire ScopeGate next.
        var command = await Sent(transport);
        if (command[0] == 63) { transport.Emit([0]); await mutation; command = await Sent(transport); }
        Check(command.SequenceEqual(new byte[] { 54, 0 })); transport.Emit([0]);
        Check((await Sent(transport))[0] == 2); transport.Emit([6, 1, 0, 0, 0, 0, 0, 0, 0, 0]);
        Check((await Sent(transport)).SequenceEqual(new byte[] { 54, 0 })); transport.Emit([0]);
        var result = await privateSend; Check((await result.Delivery).Status == MessageDeliveryStatus.NotExpected);
        if (!mutation.IsCompleted) { Check((await Sent(transport))[0] == 63); transport.Emit([0]); await mutation; }
    }
    private static async Task UnscopedAndCleanup()
    {
        var transport = new TestTransport(); await using var client = Client(transport); await Start(client, transport);
        var set = client.SetDefaultFloodScopeAsync(RegionDefinition.Public("default"));
        await Sent(transport); transport.Emit([0]); await set;
        var send = client.SendChannelTextAsync(1, "text", 7, FloodScopeSelection.ForceUnscoped);
        Check((await Sent(transport)).SequenceEqual(new byte[] { 54, 1 })); transport.Emit([0]);
        Check((await Sent(transport))[0] == 3); transport.Emit([0]); await Sent(transport); transport.Emit([1, 4]);
        var result = await send; Check(result.Timestamp == 7 && result.ScopeCleanupError is MeshCoreCommandException);
        Check(!client.IsStarted && !transport.Sent.Reader.TryRead(out _));
    }
    private static async Task OldFirmware()
    {
        var transport = new TestTransport(); await using var client = Client(transport); await Start(client, transport, 7);
        await Throws<NotSupportedException>(() => client.SendChannelTextAsync(0, "text", 1, FloodScopeSelection.ForceUnscoped));
        Check(!transport.Sent.Reader.TryRead(out _));
        var legacy = client.SendChannelTextAsync(0, "text", 2); Check((await Sent(transport))[0] == 3);
        transport.Emit([0]); await legacy;
    }
    private static async Task Unsupported()
    {
        var transport = new TestTransport(); await using var client = Client(transport); await Start(client, transport);
        var send = client.SendChannelTextAsync(0, "text", 1, FloodScopeSelection.ExplicitRegion(RegionKeys.DerivePublic("A")));
        await Sent(transport); transport.Emit([1, 1]);
        await Throws<NotSupportedException>(async () => await send); Check(!transport.Sent.Reader.TryRead(out _));
        var legacy = client.SendChannelTextAsync(0, "text", 2); Check((await Sent(transport))[0] == 3);
        transport.Emit([0]); await legacy;
    }
    private static async Task SafeRead()
    {
        var transport = new TestTransport(); await using var client = Client(transport); await Start(client, transport);
        var send = client.SendChannelTextAsync(0, "text", 1, FloodScopeSelection.ForceUnscoped);
        await Sent(transport); var read = client.GetDeviceTimeAsync(); transport.Emit([0]);
        // The queued read takes CommandGate between SET and SEND while ScopeGate stays held.
        Check((await Sent(transport))[0] == 5); transport.Emit([0x88, 1]); transport.Emit([9, 1, 0, 0, 0]); await read;
        Check((await Sent(transport))[0] == 3); transport.Emit([0]); await Sent(transport); transport.Emit([0]); await send;
    }
}
