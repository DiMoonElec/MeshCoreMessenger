using MeshCoreSharp;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;
using MeshCoreSharp.Protocol.Commands;
using MeshCoreSharp.Protocol.Packets;
using MeshCoreSharp.Protocol.Parsing;

internal static class RegionDefaultTests
{
    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("REGION2 default wire lengths, padding and authoritative custom key", Wire),
        ("REGION2 default API matches response with interleaved push", Routing),
        ("REGION2 default errors, malformed, cancellation and timeout", Failures),
    ];
    private static void Check(bool value) { if (!value) throw new Exception("Default scope regression"); }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T error) { return error; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
    private static byte[] Fixture()
    {
        var bytes = new byte[48]; bytes[0] = 0x1C;
        System.Text.Encoding.UTF8.GetBytes("$custom").CopyTo(bytes, 1);
        bytes.AsSpan(32).Fill(0xA7);
        return bytes;
    }
    private static async Task Wire()
    {
        var decoder = new CompanionPacketDecoder();
        Check(((DefaultFloodScopePacket)decoder.Decode(new byte[] { 0x1C })).Scope.State == NodeFloodScopeState.KnownEmpty);
        var bytes = Fixture();
        var scope = ((DefaultFloodScopePacket)decoder.Decode(bytes)).Scope;
        Check(scope.Name == "$custom" && scope.Key!.ToArray().All(b => b == 0xA7));
        bytes[32] = 0;
        Check(scope.Key!.ToArray()[0] == 0xA7);
        bytes[15] = 0xFF; // stale bytes after NUL from firmware strcpy are not part of the name
        Check(((DefaultFloodScopePacket)decoder.Decode(bytes)).Scope.Name == "$custom");
        for (var length = 2; length < 48; length++)
            await Throws<MeshCoreProtocolException>(() => Task.FromResult(decoder.Decode(Fixture()[..length])));
        await Throws<MeshCoreProtocolException>(() => Task.FromResult(decoder.Decode(new byte[] { }.Concat(Fixture()).Append((byte)0).ToArray())));
        bytes = Fixture(); bytes.AsSpan(1, 31).Fill(65);
        await Throws<MeshCoreProtocolException>(() => Task.FromResult(decoder.Decode(bytes)));
        bytes = Fixture(); bytes[1] = 0xFF;
        await Throws<MeshCoreProtocolException>(() => Task.FromResult(decoder.Decode(bytes)));
        var command = CompanionCommands.SetDefaultFloodScope(RegionDefinition.Public("foo"));
        Check(command.Length == 48 && command[0] == 0x3F && command[1] == 'f');
        Check(command.AsSpan(4, 28).ToArray().All(b => b == 0));
        Check(command.AsSpan(32).SequenceEqual(Convert.FromHexString("5BCADC2B60BE87D52F213E0411B5438E")));
        Check(CompanionCommands.ClearDefaultFloodScope().SequenceEqual(new byte[] { 0x3F }));
    }
    private static MeshCoreClient Client(TestTransport transport) => new(transport,
        new MeshCoreClientOptions { AutoReceiveMessages = false, CommandTimeout = TimeSpan.FromMilliseconds(150) });
    private static async Task<byte[]> Sent(TestTransport transport) =>
        await transport.Sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    private static async Task Routing()
    {
        var transport = new TestTransport(); await using var client = Client(transport);
        await client.ConnectAsync(); await client.StartAsync();
        var push = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PushPacketReceived += (_, _) => push.TrySetResult();
        var get = client.GetDefaultFloodScopeAsync();
        Check((await Sent(transport)).SequenceEqual(new byte[] { 0x40 }));
        transport.Emit([0x88, 1]); await push.Task;
        Check(!get.IsCompleted); transport.Emit(Fixture());
        Check((await get).Name == "$custom");
        var set = client.SetDefaultFloodScopeAsync(RegionDefinition.Public("foo"));
        Check((await Sent(transport)).Length == 48); transport.Emit([0]); await set;
        var clear = client.ClearDefaultFloodScopeAsync();
        Check((await Sent(transport)).SequenceEqual(new byte[] { 0x3F })); transport.Emit([0]); await clear;
        Check(!transport.Sent.Reader.TryRead(out _));
        await Throws<ArgumentOutOfRangeException>(() => client.SetDefaultFloodScopeAsync(RegionDefinition.Public(new string('a', 31))));
        Check(!transport.Sent.Reader.TryRead(out _));
    }
    private static async Task Failures()
    {
        var transport = new TestTransport(); await using var client = Client(transport);
        await client.ConnectAsync(); await client.StartAsync();
        foreach (var code in new byte[] { 1, 6 })
        {
            var get = client.GetDefaultFloodScopeAsync(); await Sent(transport); transport.Emit([1, code]);
            var error = await Throws<MeshCoreCommandException>(async () => await get);
            Check(error.ErrorCode == (MeshCoreErrorCode)code);
        }
        var malformed = client.GetDefaultFloodScopeAsync(); await Sent(transport); transport.Emit([0x1C, 0]);
        await Throws<MeshCoreProtocolException>(async () => await malformed);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Throws<OperationCanceledException>(() => client.ClearDefaultFloodScopeAsync(cts.Token));
        Check(!transport.Sent.Reader.TryRead(out _));
        var cancel = new CancellationTokenSource();
        var pending = client.GetDefaultFloodScopeAsync(cancel.Token); await Sent(transport); cancel.Cancel();
        var canceled = await Throws<OperationCanceledException>(async () => await pending);
        Check(canceled.CancellationToken == cancel.Token); cancel.Dispose();
        var timeout = client.GetDefaultFloodScopeAsync(); await Sent(transport);
        await Throws<MeshCoreTimeoutException>(async () => await timeout);
    }
}
