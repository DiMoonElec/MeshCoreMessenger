using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;
using MeshCoreSharp.Protocol.Packets;
using static RegionChannelTests;

internal static class RegionRequestTests
{
    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("REGION10 directed requests preserve reply paths and full target; empty and partial CSV", Requests),
        ("REGION10 binary response immediately after MSG_SENT is retained", Early),
        ("REGION10 unsupported, table-full, final timeout, malformed and cancellation differ", Failures),
    ];
    private static void Check(bool condition) => RegionChannelTests.Check(condition);
    private static Task<T> Throws<T>(Func<Task> action) where T : Exception => RegionChannelTests.Throws<T>(action);
    internal static byte[] Acceptance(uint tag = 7) => [6, 0, (byte)tag, (byte)(tag >> 8), (byte)(tag >> 16), (byte)(tag >> 24), 0xE8, 3, 0, 0];
    private static async Task Requests()
    {
        var transport = new TestTransport(); await using var client = Client(transport); await Start(client, transport);
        var key = Enumerable.Repeat((byte)0xA7, 32).ToArray();
        foreach (var path in new[] { ReplyPath.ZeroHop, new ReplyPath(0x42, new byte[] { 1, 2, 3, 4 }) })
        foreach (var csv in new[] { "", "*,A,$private" })
        {
            var request = client.RequestRegionsAsync(key, path);
            var command = await Sent(transport);
            Check(command[0] == 57 && command.AsSpan(1, 32).SequenceEqual(key) && command[33] == 1 && command[34] == path.Descriptor);
            Check(command.AsSpan(35).SequenceEqual(path.ToArray()));
            transport.Emit(Acceptance()); transport.Emit(RegionWireTests.BinaryFrame(csv, 7));
            var result = await request;
            Check(result.IsPotentiallyIncomplete && result.RemoteClock == 0xAABBCCDD && result.Tag == 7);
            Check(result.Names.Count == (csv.Length == 0 ? 0 : 3));
            Check(!transport.Sent.Reader.TryRead(out _)); // No contact read/write or route policy.
        }
        await Throws<ArgumentException>(() => client.RequestRegionsAsync(new byte[8], ReplyPath.ZeroHop));
        await Throws<ArgumentOutOfRangeException>(() => client.RequestRegionsAsync(key, ReplyPath.ZeroHop, TimeSpan.Zero));
        Check(!transport.Sent.Reader.TryRead(out _));
    }
    private static async Task Early()
    {
        var transport = new TestTransport(); await using var client = Client(transport); await Start(client, transport);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var final = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PushPacketReceived += (_, args) => { if (args.Packet is BinaryResponsePacket) final.TrySetResult(); };
        transport.OnSend = async (frame, ct) =>
        {
            Check(frame.Span[0] == 57);
            transport.Emit(Acceptance()); transport.Emit(RegionWireTests.BinaryFrame("A", 7));
            await release.Task.WaitAsync(ct);
        };
        var request = client.RequestRegionsAsync(new byte[32], ReplyPath.ZeroHop);
        await final.Task.WaitAsync(TimeSpan.FromSeconds(2)); Check(!request.IsCompleted); release.TrySetResult();
        Check((await request).Names.Single() == "A");
    }
    private static async Task Failures()
    {
        var transport = new TestTransport(); await using var client = Client(transport); await Start(client, transport);
        foreach (var code in new[] { MeshCoreErrorCode.UnsupportedCommand, MeshCoreErrorCode.TableFull })
        {
            var request = client.RequestRegionsAsync(new byte[32], ReplyPath.ZeroHop); await Sent(transport); transport.Emit([1, (byte)code]);
            Check((await Throws<MeshCoreCommandException>(async () => await request)).ErrorCode == code);
        }
        var timeout = client.RequestRegionsAsync(new byte[32], ReplyPath.ZeroHop, TimeSpan.FromMilliseconds(20));
        await Sent(transport); transport.Emit(Acceptance()); await Throws<MeshCoreTimeoutException>(async () => await timeout);
        var malformed = client.RequestRegionsAsync(new byte[32], ReplyPath.ZeroHop);
        await Sent(transport); transport.Emit(Acceptance(8)); transport.Emit(RegionWireTests.BinaryFrame("A,,B", 8));
        await Throws<MeshCoreProtocolException>(async () => await malformed);
        using var cts = new CancellationTokenSource();
        var canceled = client.RequestRegionsAsync(new byte[32], ReplyPath.ZeroHop, cts.Token);
        await Sent(transport); transport.Emit(Acceptance(9));
        var barrier = client.GetDeviceTimeAsync(); await Sent(transport); transport.Emit([9, 0, 0, 0, 0]); await barrier;
        cts.Cancel(); await Throws<OperationCanceledException>(async () => await canceled);
        Check(client.IsStarted && !transport.Sent.Reader.TryRead(out _));
    }
}
