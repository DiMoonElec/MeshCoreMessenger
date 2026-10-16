using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;
using MeshCoreSharp.Protocol.Packets;
using MeshCoreSharp.Protocol.Parsing;
using MeshCoreSharp.Runtime;

internal static class RegionRemoteTests
{
    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("REGION9 remote matcher requires RX bind and rejects wrong or late tags", Matching),
        ("REGION9 remote gate spans final response and permits local commands and ACK", Gate),
        ("REGION9 immediate error, final deadline, cancellation and disconnect cleanup", Failures),
        ("REGION9 MSG_SENT hook binds final response before transport write returns", Early),
    ];
    private static void Check(bool value) => RegionChannelTests.Check(value);
    private static Task<T> Throws<T>(Func<Task> action) where T : Exception => RegionChannelTests.Throws<T>(action);
    private static BinaryResponsePacket Packet(uint tag) => (BinaryResponsePacket)new CompanionPacketDecoder().Decode(RegionWireTests.BinaryFrame("A", tag));
    private static async Task Matching()
    {
        var clock = new ManualTimeProvider();
        using var waiter = new BinaryResponseWaiter(TimeSpan.FromSeconds(2), clock);
        Check(!waiter.Observe(Packet(7))); // No arbitrary response caching before MSG_SENT.
        waiter.Bind(new(true, 7, 500)); Check(!waiter.Observe(Packet(8)));
        Check(waiter.Observe(Packet(7)) && (await waiter.Completion).Tag == 7);
        Check(!waiter.Observe(Packet(7)));
        using var timed = new BinaryResponseWaiter(TimeSpan.FromSeconds(1), clock);
        timed.Bind(new(true, 9, 500)); clock.Advance(TimeSpan.FromSeconds(1));
        await Throws<MeshCoreTimeoutException>(async () => await timed.Completion);
        Check(!timed.Observe(Packet(9)));
    }
    private static async Task Gate()
    {
        var router = new PacketRouter(); var transport = new TestTransport(); await transport.ConnectAsync();
        using var dispatcher = new CommandDispatcher(transport, router);
        var coordinator = new MeshRequestCoordinator(router, default);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = coordinator.ExecuteBinaryAsync((bind, _) =>
        { bind(new(false, 1, 1000)); entered.TrySetResult(); return Task.CompletedTask; }, null, default);
        await entered.Task;
        var secondEntered = false;
        var second = coordinator.ExecuteBinaryAsync((bind, _) =>
        { secondEntered = true; bind(new(false, 2, 1000)); router.Route(Packet(2)); return Task.CompletedTask; }, null, default);
        Check(!secondEntered);
        var local = dispatcher.SendAsync<OkPacket>(CommandType.SetDeviceTime, new byte[] { 6, 0, 0, 0, 0 }, "local", TimeSpan.FromSeconds(1));
        await transport.Sent.Reader.ReadAsync();
        var decoder = new CompanionPacketDecoder();
        Check(!router.Route(decoder.Decode(new byte[] { 0x82, 1, 0, 0, 0, 1, 0, 0, 0 })));
        router.Route(decoder.Decode(new byte[] { 0 })); await local;
        router.Route(Packet(1)); Check((await first).Tag == 1); Check((await second).Tag == 2);
    }
    private static async Task Failures()
    {
        var router = new PacketRouter(); using var session = new CancellationTokenSource();
        var coordinator = new MeshRequestCoordinator(router, session.Token);
        await Throws<MeshCoreCommandException>(() => coordinator.ExecuteBinaryAsync((_, _) =>
            throw new MeshCoreCommandException(CommandType.SendAnonymousRequest, MeshCoreErrorCode.TableFull), null, default));
        await Throws<MeshCoreTimeoutException>(() => coordinator.ExecuteBinaryAsync((bind, _) =>
        { bind(new(true, 3, 0)); return Task.CompletedTask; }, TimeSpan.FromMilliseconds(10), default));
        using var caller = new CancellationTokenSource();
        var canceled = coordinator.ExecuteBinaryAsync((bind, _) =>
        { bind(new(true, 4, 0)); caller.Cancel(); return Task.CompletedTask; }, null, caller.Token);
        await Throws<OperationCanceledException>(async () => await canceled);
        var pending = coordinator.ExecuteBinaryAsync((bind, _) =>
        { bind(new(true, 5, 0)); return Task.CompletedTask; }, null, default);
        var queued = coordinator.ExecuteBinaryAsync((_, _) => throw new Exception("Old queued remote request entered"), null, default);
        router.FailCurrent(new MeshCoreTransportException("disconnected")); session.Cancel();
        try { await pending; throw new Exception("Unexpected remote success"); }
        catch (Exception error) when (error is MeshCoreTransportException or OperationCanceledException) { }
        await Throws<OperationCanceledException>(async () => await queued);
    }

    private static async Task Early()
    {
        var router = new PacketRouter(); var transport = new TestTransport(); await transport.ConnectAsync();
        using var dispatcher = new CommandDispatcher(transport, router);
        var coordinator = new MeshRequestCoordinator(router, default);
        var decoder = new CompanionPacketDecoder();
        transport.OnSend = (_, _) =>
        {
            router.Route(decoder.Decode(new byte[] { 6, 0, 7, 0, 0, 0, 100, 0, 0, 0 }));
            router.Route(Packet(7));
            return ValueTask.CompletedTask;
        };
        var response = await coordinator.ExecuteBinaryAsync(async (bind, ct) =>
        {
            await dispatcher.SendAsync<MessageSentPacket>(CommandType.SendAnonymousRequest, new byte[] { 57 },
                "remote", TimeSpan.FromSeconds(1), ct, type => type == PacketType.MessageSent,
                onAccepted: packet => bind(packet.Info), invalidateOnUncertainResponse: true);
        }, null, default);
        Check(response.Tag == 7);
    }
}
