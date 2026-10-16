using System.Buffers.Binary;
using MeshCoreSharp;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;
using static RegionChannelTests;

internal static class RegionPrivateTests
{
    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("REGION5 scoped private send preserves timestamp attempt and fast ACK", FastAck),
        ("REGION5 private capacity waits outside scope gate and Delivery is independent", Capacity),
        ("REGION5 SET rejection never transmits private text", SetError),
        ("REGION5 accepted private and confirmed ACK survive cleanup failure", CleanupFailure),
    ];
    private static byte[] MessageSent(uint tag)
    {
        var bytes = new byte[10]; bytes[0] = 6; bytes[1] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(2), tag);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(6), 30000);
        return bytes;
    }
    private static byte[] Ack(uint tag)
    {
        var bytes = new byte[9]; bytes[0] = 0x82;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1), tag); return bytes;
    }
    private static async Task FastAck()
    {
        var transport = new TestTransport(); await using var client = Client(transport); await Start(client, transport);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PushPacketReceived += (_, args) => { if (args.Packet.RawType == 0x82) received.TrySetResult(); };
        transport.OnSend = async (frame, ct) =>
        {
            if (frame.Span[0] == 54) transport.Emit([0]);
            else
            {
                RegionChannelTests.Check(frame.Span[0] == 2 && frame.Span[2] == 3 && BinaryPrimitives.ReadUInt32LittleEndian(frame.Span[3..]) == 123);
                transport.Emit(MessageSent(7)); transport.Emit(Ack(7)); await releaseWrite.Task.WaitAsync(ct);
            }
        };
        var pending = client.SendTextAsync(new byte[32], "private", 123, 3,
            FloodScopeSelection.ExplicitRegion(RegionKeys.DerivePublic("A")));
        await received.Task.WaitAsync(TimeSpan.FromSeconds(2)); RegionChannelTests.Check(!pending.IsCompleted);
        releaseWrite.TrySetResult(); var result = await pending;
        RegionChannelTests.Check(result.Timestamp == 123 && result.ScopeCleanupError is null);
        RegionChannelTests.Check((await result.Delivery).Status == MessageDeliveryStatus.Confirmed);
    }
    private static async Task Capacity()
    {
        var transport = new TestTransport();
        await using var client = new MeshCoreClient(transport, new MeshCoreClientOptions
        { AutoReceiveMessages = false, CommandTimeout = TimeSpan.FromSeconds(2), MinimumAckTimeout = TimeSpan.FromSeconds(30), MaximumAckTimeout = TimeSpan.FromSeconds(30) });
        await Start(client, transport);
        var results = new List<TextMessageSendResult>();
        for (uint tag = 1; tag <= 8; tag++)
        {
            var send = client.SendTextAsync(new byte[32], "private", tag, 0, FloodScopeSelection.InheritDefault);
            await Sent(transport); transport.Emit([0]); RegionChannelTests.Check((await Sent(transport))[0] == 2); transport.Emit(MessageSent(tag));
            await Sent(transport); transport.Emit([0]); results.Add(await send);
        }
        var ninth = client.SendTextAsync(new byte[32], "ninth", 9, 0, FloodScopeSelection.ForceUnscoped);
        RegionChannelTests.Check(!transport.Sent.Reader.TryRead(out _));
        var channel = client.SendChannelTextAsync(0, "channel", 10, FloodScopeSelection.ForceUnscoped);
        await CompleteChannel(transport, FloodScopeSelection.ForceUnscoped, 0); await channel;
        RegionChannelTests.Check(!ninth.IsCompleted && results.All(r => !r.Delivery.IsCompleted));
        transport.Emit(Ack(1)); RegionChannelTests.Check((await results[0].Delivery).Status == MessageDeliveryStatus.Confirmed);
        RegionChannelTests.Check((await Sent(transport)).SequenceEqual(new byte[] { 54, 1 })); transport.Emit([0]);
        RegionChannelTests.Check((await Sent(transport))[0] == 2); transport.Emit(MessageSent(9)); await Sent(transport); transport.Emit([0]);
        results.Add(await ninth);
        for (uint tag = 2; tag <= 9; tag++) transport.Emit(Ack(tag));
        foreach (var result in results) RegionChannelTests.Check((await result.Delivery).Status == MessageDeliveryStatus.Confirmed);
    }
    private static async Task SetError()
    {
        var transport = new TestTransport(); await using var client = Client(transport); await Start(client, transport);
        var send = client.SendTextAsync(new byte[32], "private", 1, 0, FloodScopeSelection.ForceUnscoped);
        RegionChannelTests.Check((await Sent(transport))[0] == 54); transport.Emit([1, 6]);
        var error = await RegionChannelTests.Throws<MeshCoreCommandException>(async () => await send);
        RegionChannelTests.Check(error.Command == MeshCoreSharp.Protocol.CommandType.SetFloodScopeKey && !transport.Sent.Reader.TryRead(out _));
    }
    private static async Task CleanupFailure()
    {
        var transport = new TestTransport(); await using var client = Client(transport); await Start(client, transport);
        var send = client.SendTextAsync(new byte[32], "private", 1, 0, FloodScopeSelection.ForceUnscoped);
        await Sent(transport); transport.Emit([0]); await Sent(transport); transport.Emit(MessageSent(42)); transport.Emit(Ack(42));
        await Sent(transport); transport.Emit([1, 4]); var result = await send;
        RegionChannelTests.Check(result.Accepted.ExpectedAck == 42 && result.ScopeCleanupError is MeshCoreCommandException);
        RegionChannelTests.Check((await result.Delivery).Status == MessageDeliveryStatus.Confirmed);
        RegionChannelTests.Check(!client.IsStarted);
    }
}
