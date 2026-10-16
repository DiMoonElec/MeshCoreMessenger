using System.Buffers.Binary;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol.Parsing;
using MeshCoreSharp.Runtime;
using static RegionChannelTests;

internal static class RegionNeighborTests
{
    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("REGION8 collector deduplication, identity filter, memory bound and deadline", Collector),
        ("REGION8 early responses survive slow transport write; pushes remain visible", Early),
        ("REGION8 collection window allows local commands and rejects concurrent scan", Window),
        ("REGION8 cancel, disconnect and reconnect remove the old collector", Lifecycle),
    ];
    private static void Check(bool value) => RegionChannelTests.Check(value);
    private static Task<T> Throws<T>(Func<Task> action) where T : Exception => RegionChannelTests.Throws<T>(action);
    private static async Task Collector()
    {
        var clock = new ManualTimeProvider(); var decoder = new CompanionPacketDecoder();
        using var collector = new NeighborDiscoveryCollector(123, 1, TimeSpan.FromSeconds(3), clock);
        Check(!collector.Observe(decoder.Decode(RegionWireTests.Neighbor(124))));
        Check(!collector.Observe(decoder.Decode(RegionWireTests.Neighbor(123, keyLength: 8))));
        Check(!collector.Observe(decoder.Decode(RegionWireTests.Neighbor(123, type: 0x91))));
        Check(collector.Observe(decoder.Decode(RegionWireTests.Neighbor(123))));
        Check(collector.Observe(decoder.Decode(RegionWireTests.Neighbor(123))));
        collector.Observe(decoder.Decode(RegionWireTests.Neighbor(123, key: 1)));
        clock.Advance(TimeSpan.FromSeconds(2)); Check(!collector.Completion.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        var result = await collector.Completion; Check(result.Neighbors.Count == 1 && result.CapacityReached);
        Check(!collector.Observe(decoder.Decode(RegionWireTests.Neighbor(123, key: 2))));
        using var empty = new NeighborDiscoveryCollector(1, 1, TimeSpan.FromSeconds(1), clock);
        clock.Advance(TimeSpan.FromSeconds(1)); Check((await empty.Completion).Neighbors.Count == 0);
    }
    private static async Task Early()
    {
        var transport = new TestTransport(); await using var client = Client(transport); await Start(client, transport);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pushes = 0;
        client.PushPacketReceived += (_, _) => { if (Interlocked.Increment(ref pushes) == 4) observed.TrySetResult(); };
        transport.OnSend = async (frame, ct) =>
        {
            Check(frame.Span[0] == 55 && frame.Span[1] == 0x80 && frame.Span[2] == 4);
            var tag = BinaryPrimitives.ReadUInt32LittleEndian(frame.Span[3..]); Check(tag != 0);
            transport.Emit(RegionWireTests.Neighbor(tag)); transport.Emit(RegionWireTests.Neighbor(tag, key: 1));
            transport.Emit(RegionWireTests.Neighbor(tag)); transport.Emit([0x88, 1]); transport.Emit([0]);
            await release.Task.WaitAsync(ct);
        };
        var pending = client.DiscoverRepeaterNeighborsAsync(new() { CollectionWindow = TimeSpan.FromMilliseconds(200) });
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(2)); Check(!pending.IsCompleted); release.TrySetResult();
        Check((await pending).Neighbors.Count == 2 && pushes == 4);
    }
    private static async Task Window()
    {
        var transport = new TestTransport(); await using var client = Client(transport); await Start(client, transport);
        var pending = client.DiscoverRepeaterNeighborsAsync(new() { CollectionWindow = TimeSpan.FromMilliseconds(250) });
        await Sent(transport); transport.Emit([0]);
        await Throws<InvalidOperationException>(() => client.DiscoverRepeaterNeighborsAsync());
        var time = client.GetDeviceTimeAsync(); Check((await Sent(transport))[0] == 5);
        transport.Emit([9, 1, 0, 0, 0]); await time; Check(!pending.IsCompleted);
        Check((await pending).Neighbors.Count == 0);
    }
    private static async Task Lifecycle()
    {
        var transport = new TestTransport(); await using var client = Client(transport); await Start(client, transport);
        using var cts = new CancellationTokenSource();
        var pending = client.DiscoverRepeaterNeighborsAsync(new() { CollectionWindow = TimeSpan.FromSeconds(5) }, cts.Token);
        var command = await Sent(transport); var oldTag = BinaryPrimitives.ReadUInt32LittleEndian(command.AsSpan(3));
        transport.Emit([0]);
        var barrier = client.GetDeviceTimeAsync(); await Sent(transport); transport.Emit([9, 1, 0, 0, 0]); await barrier;
        cts.Cancel(); await Throws<OperationCanceledException>(async () => await pending); Check(client.IsStarted);
        pending = client.DiscoverRepeaterNeighborsAsync(new() { CollectionWindow = TimeSpan.FromSeconds(5) });
        await Sent(transport); transport.Emit([0]); await client.DisconnectAsync();
        await Throws<MeshCoreTransportException>(async () => await pending);
        await Start(client, transport);
        pending = client.DiscoverRepeaterNeighborsAsync(new() { CollectionWindow = TimeSpan.FromMilliseconds(100) });
        command = await Sent(transport); var tag = BinaryPrimitives.ReadUInt32LittleEndian(command.AsSpan(3)); Check(tag != oldTag);
        transport.Emit(RegionWireTests.Neighbor(oldTag)); transport.Emit([0]);
        Check((await pending).Neighbors.Count == 0);
    }
}
