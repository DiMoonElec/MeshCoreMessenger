using System.Buffers.Binary;
using System.Text;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Protocol.Packets;

namespace MeshCoreSharp.Models;

/// <summary>An unpaginated, potentially incomplete list of allowed names. Private names do not imply known keys.</summary>
public sealed record RegionNamesResponse(uint Tag, uint RemoteClock, IReadOnlyList<string> Names)
{
    internal static RegionNamesResponse Parse(BinaryResponsePacket packet)
    {
        var payload = packet.GetPayload().AsSpan();
        if (payload.Length < sizeof(uint)) throw new MeshCoreProtocolException("Region response lacks a remote clock.");
        var clock = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        var csv = payload[sizeof(uint)..];
        if (csv.Length > 0 && csv[^1] == 0) csv = csv[..^1];
        if (csv.Contains((byte)0)) throw new MeshCoreProtocolException("Region CSV contains an embedded NUL.");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(csv); }
        catch (DecoderFallbackException) { throw new MeshCoreProtocolException("Region CSV is not valid UTF-8."); }
        var names = text.Length == 0 ? Array.Empty<string>() : text.Split(',');
        if (names.Any(n => n.Length == 0)) throw new MeshCoreProtocolException("Region CSV contains an empty name.");
        return new(packet.Tag, clock, Array.AsReadOnly(names));
    }
}
