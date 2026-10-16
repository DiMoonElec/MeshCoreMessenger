using System.Buffers.Binary;
using MeshCoreSharp;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;
using MeshCoreSharp.Transport.Serial;

internal static class RegionAcceptanceTests
{
    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("REGION11 fragmented stream carries scopes, ACK, automatic drain and both discovery operations", Stream),
        ("REGION11 definite local transport rejection cleans up without invalidating session", LocalRejection),
        ("REGION11 duplicate ACK hook failure consumes MSG_SENT without invalidating command stream", DuplicateAck),
    ];
    private static void Check(bool value) => RegionChannelTests.Check(value);
    private static byte[] Wire(byte[] payload)
    {
        var frame = new byte[payload.Length + 3]; frame[0] = 0x3E;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1), (ushort)payload.Length);
        payload.CopyTo(frame, 3); return frame;
    }
    private static async Task Stream()
    {
        var port = new FakeSerialConnection(); var types = new List<CommandType>();
        var textQueued = false; var notified = false;
        void Reply(params byte[][] packets)
        {
            var stream = packets.SelectMany(Wire).ToArray();
            // Fragment marker/UInt16 header; coalesce the remaining payload and following frames.
            port.Emit(stream[..1]); port.Emit(stream[1..2]); port.Emit(stream[2..]);
        }
        port.OnWrite = wire =>
        {
            Check(wire[0] == 0x3C && BinaryPrimitives.ReadUInt16LittleEndian(wire.AsSpan(1)) == wire.Length - 3);
            var command = wire.AsSpan(3); var type = (CommandType)command[0]; types.Add(type);
            switch (type)
            {
                case CommandType.AppStart:
                    var self = new byte[58]; self[0] = 5; Reply(self); break;
                case CommandType.DeviceQuery:
                    var device = new byte[80]; device[0] = 13; device[1] = 14; Reply(device); break;
                case CommandType.GetDefaultFloodScope: Reply([0x1C]); break;
                case CommandType.SetFloodScopeKey:
                    if (!notified) { notified = true; textQueued = true; Reply([0x83], [0]); }
                    else Reply([0]);
                    break;
                case CommandType.SyncNextMessage:
                    if (textQueued) { textQueued = false; Reply(MessageFixtures.Text(PacketType.ContactMessageReceivedV3)); }
                    else Reply([0x0A]);
                    break;
                case CommandType.SendChannelTextMessage: Reply([0x88, 1], [0]); break;
                case CommandType.SendTextMessage:
                    Reply(RegionRequestTests.Acceptance(7), [0x82, 7, 0, 0, 0, 1, 0, 0, 0]); break;
                case CommandType.SendControlData:
                    var tag = BinaryPrimitives.ReadUInt32LittleEndian(command[3..]);
                    Reply(RegionWireTests.Neighbor(tag), [0]); break;
                case CommandType.SendAnonymousRequest:
                    Reply(RegionRequestTests.Acceptance(8), RegionWireTests.BinaryFrame("*,A,$private", 8)); break;
                default: throw new Exception($"Unexpected REGION integration command {type}");
            }
        };
        await using var transport = new SerialMeshCoreTransport(new SerialMeshCoreTransportOptions
        { PortName = "region-fake", OpenDelay = TimeSpan.Zero, ReadTimeout = 20 }, _ => port);
        await using var client = new MeshCoreClient(transport);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.MessageReceived += (_, _) => received.TrySetResult();
        await client.ConnectAsync(); await client.StartAsync(); await client.GetDeviceInfoAsync();
        await client.GetDefaultFloodScopeAsync();
        Check(!types.Contains(CommandType.SetFloodScopeKey) && !types.Contains(CommandType.SetDefaultFloodScope));
        var a = await client.SendChannelTextAsync(0, "A", 1, FloodScopeSelection.ExplicitRegion(RegionKeys.DerivePublic("A")));
        var b = await client.SendChannelTextAsync(1, "B", 2, FloodScopeSelection.ForceUnscoped);
        var privateSend = await client.SendTextAsync(new byte[32], "private", 3, 0);
        Check(a.TextBudget!.Scope.EffectiveScope == EffectiveFloodScope.Scoped && b.TextBudget!.Scope.EffectiveScope == EffectiveFloodScope.Unscoped);
        Check((await privateSend.Delivery).Status == MessageDeliveryStatus.Confirmed);
        Check((await client.DiscoverRepeaterNeighborsAsync(new() { CollectionWindow = TimeSpan.FromMilliseconds(40) })).Neighbors.Count == 1);
        Check((await client.RequestRegionsAsync(new byte[32], ReplyPath.ZeroHop)).Names.Count == 3);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await client.DisconnectAsync(); await client.FlushEventsAsync();
        Check(types.Count(t => t == CommandType.SendChannelTextMessage) == 2 && types.Count(t => t == CommandType.SendTextMessage) == 1);
        Check(typeof(MeshCoreClient).Assembly.GetName().Name == "MeshCoreSharp");
    }

    private static async Task LocalRejection()
    {
        var transport = new TestTransport(); await using var client = RegionChannelTests.Client(transport);
        await RegionChannelTests.Start(client, transport);
        var resets = 0;
        transport.OnSend = (frame, _) =>
        {
            if (frame.Span[0] == 54) { resets++; transport.Emit([0]); return ValueTask.CompletedTask; }
            return ValueTask.FromException(new ArgumentOutOfRangeException("frame", "MTU changed before native write"));
        };
        await RegionChannelTests.Throws<ArgumentOutOfRangeException>(() => client.SendChannelTextAsync(0, "a", 1, FloodScopeSelection.ForceUnscoped));
        Check(resets == 2 && client.IsStarted);
    }

    private static async Task DuplicateAck()
    {
        var transport = new TestTransport(); await using var client = RegionChannelTests.Client(transport);
        await RegionChannelTests.Start(client, transport);
        var first = client.SendTextAsync(new byte[32], "first", 1, 0);
        await RegionChannelTests.Sent(transport); transport.Emit([0]); await RegionChannelTests.Sent(transport); transport.Emit(RegionRequestTests.Acceptance(7));
        await RegionChannelTests.Sent(transport); transport.Emit([0]); var accepted = await first;
        var second = client.SendTextAsync(new byte[32], "second", 2, 0);
        await RegionChannelTests.Sent(transport); transport.Emit([0]); await RegionChannelTests.Sent(transport); transport.Emit(RegionRequestTests.Acceptance(7));
        Check((await RegionChannelTests.Sent(transport)).SequenceEqual(new byte[] { 54, 0 })); transport.Emit([0]);
        await RegionChannelTests.Throws<MeshCoreSharp.Exceptions.MeshCoreProtocolException>(async () => await second);
        await RegionChannelTests.Throws<MeshCoreSharp.Exceptions.MeshCoreProtocolException>(async () => await accepted.Delivery);
        Check(client.IsStarted);
    }
}
