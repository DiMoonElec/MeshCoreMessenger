using System.Buffers.Binary;
using System.Text;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol.Commands;
using MeshCoreSharp.Protocol.Packets;
using MeshCoreSharp.Protocol.Parsing;

internal static class RegionWireTests
{
    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("REGION7 discovery wire fixtures full and prefix identities with signed signals", Discovery),
        ("REGION7 binary header and region clock CSV variants", Binary),
        ("REGION7 packed reply paths validate and reverse multi-byte hops", Paths),
    ];
    private static void Check(bool condition) => RegionChannelTests.Check(condition);
    private static Task Reject(Action action)
    {
        try { action(); } catch (Exception error) when (error is MeshCoreProtocolException or ArgumentException) { return Task.CompletedTask; }
        throw new Exception("Invalid REGION wire accepted");
    }
    internal static byte[] Neighbor(uint tag = 0x12345678, byte key = 0xA7, int keyLength = 32, byte type = 0x92)
    {
        var frame = new byte[10 + keyLength];
        frame[0] = 0x8E; frame[1] = 0xE3; frame[2] = 0x9D; frame[3] = 0;
        frame[4] = type; frame[5] = 0xF3;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(6), tag);
        frame.AsSpan(10).Fill(key); return frame;
    }
    private static async Task Discovery()
    {
        Check(CompanionCommands.DiscoverRepeaterNeighbors(0x12345678).SequenceEqual(new byte[] { 55, 0x80, 4, 0x78, 0x56, 0x34, 0x12 }));
        Check(CompanionCommands.DiscoverRepeaterNeighbors(1, 0x44332211).AsSpan(7).SequenceEqual(new byte[] { 0x11, 0x22, 0x33, 0x44 }));
        var decoder = new CompanionPacketDecoder();
        foreach (var length in new[] { 8, 32 })
        {
            var bytes = Neighbor(keyLength: length);
            var packet = (ControlDataPacket)decoder.Decode(bytes); var neighbor = packet.Neighbor!;
            Check(neighbor.HasFullPublicKey == (length == 32));
            Check(neighbor.Tag == 0x12345678 && neighbor.ReceivedSnr == -7.25 && neighbor.InboundSnr == -3.25 && neighbor.ReceivedRssi == -99);
            bytes[10] = 0; var key = neighbor.GetKeyBytes(); key[0] = 0;
            Check(neighbor.GetKeyBytes()[0] == 0xA7);
        }
        for (var length = 1; length < 42; length++)
            if (length != 18) await Reject(() => decoder.Decode(Neighbor()[..length]));
        Check(((ControlDataPacket)decoder.Decode(new byte[] { 0x8E, 0, 0, 0, 0xA0 })).Neighbor is null);
        await Reject(() => CompanionCommands.DiscoverRepeaterNeighbors(0));
    }
    internal static byte[] BinaryFrame(string csv, uint tag = 0x12345678)
    {
        var frame = new byte[10 + Encoding.UTF8.GetByteCount(csv)]; frame[0] = 0x8C;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(2), tag);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(6), 0xAABBCCDD);
        Encoding.UTF8.GetBytes(csv).CopyTo(frame.AsSpan(10)); return frame;
    }
    private static async Task Binary()
    {
        var decoder = new CompanionPacketDecoder();
        foreach (var csv in new[] { "", "*", "A,#B,$private,Русь" })
        foreach (var nul in Enumerable.Range(0, 16).Select(length => new string('\0', length)))
        {
            var result = RegionNamesResponse.Parse((BinaryResponsePacket)decoder.Decode(BinaryFrame(csv + nul)));
            Check(result.Tag == 0x12345678 && result.RemoteClock == 0xAABBCCDD);
            Check(result.Names.SequenceEqual(csv.Length == 0 ? Array.Empty<string>() : csv.Split(',')));
        }
        for (var length = 1; length < 10; length++)
            await Reject(() => RegionNamesResponse.Parse((BinaryResponsePacket)decoder.Decode(BinaryFrame("")[..length])));
        var invalid = BinaryFrame("a"); invalid[10] = 0xFF;
        await Reject(() => RegionNamesResponse.Parse((BinaryResponsePacket)decoder.Decode(invalid)));
        foreach (var csv in new[] { "a\0b", "a\0b\0\0", "a,", ",a", "a,,b" })
            await Reject(() => RegionNamesResponse.Parse((BinaryResponsePacket)decoder.Decode(BinaryFrame(csv))));
        // Generic binary data remains binary: no CSV interpretation is imposed on unrelated request types.
        Check(((BinaryResponsePacket)decoder.Decode(invalid)).GetPayload()[4] == 0xFF);
        // CatCore BLE, 2026-10-17: encrypted reply includes two zero padding bytes.
        var hardware = RegionNamesResponse.Parse((BinaryResponsePacket)decoder.Decode(Convert.FromHexString(
            "8C0001B4D26AD423D36A2A2C72752C72752D726F732C72752D726F732D726E640000")));
        Check(hardware.Tag == 0x6AD2B401 && hardware.RemoteClock == 1792222164 &&
            hardware.Names.SequenceEqual(new[] { "*", "ru", "ru-ros", "ru-ros-rnd" }));
    }
    private static async Task Paths()
    {
        foreach (var size in new[] { 1, 2, 3 })
        {
            var input = Enumerable.Range(1, size * 3).Select(n => (byte)n).ToArray();
            var path = new ReplyPath((byte)(((size - 1) << 6) | 3), input);
            var expected = Enumerable.Range(0, 3).Reverse().SelectMany(hop => input.Skip(hop * size).Take(size));
            Check(path.ReverseHops().ToArray().SequenceEqual(expected));
            Check(path.ReverseHops().ReverseHops().ToArray().SequenceEqual(input));
            var command = CompanionCommands.RequestRegions(new byte[32], path);
            Check(command[0] == 57 && command[33] == 1 && command[34] == path.Descriptor && command.AsSpan(35).SequenceEqual(input));
            input[0] = 0; Check(path.ToArray()[0] == 1);
        }
        Check(CompanionCommands.RequestRegions(new byte[32], ReplyPath.ZeroHop).Length == 35);
        await Reject(() => new ReplyPath(0xFF, new byte[64]));
        await Reject(() => new ReplyPath(0x80 | 22, new byte[66]));
        await Reject(() => new ReplyPath(0x40 | 33, new byte[66]));
        await Reject(() => new ReplyPath(3, new byte[2]));
        await Reject(() => CompanionCommands.RequestRegions(new byte[8], ReplyPath.ZeroHop));
    }
}
