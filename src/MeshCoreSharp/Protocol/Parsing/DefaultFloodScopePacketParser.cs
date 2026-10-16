using System.Text;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol.Packets;

namespace MeshCoreSharp.Protocol.Parsing;

internal sealed class DefaultFloodScopePacketParser : IPacketParser
{
    public PacketType Type => PacketType.DefaultFloodScope;

    public CompanionPacket Parse(ReadOnlyMemory<byte> frame)
    {
        if (frame.Length == 1) return new DefaultFloodScopePacket(frame, NodeFloodScope.KnownEmpty);
        if (frame.Length != 1 + ProtocolLimits.RegionNameSize + ProtocolLimits.RegionKeySize)
            throw new MeshCoreProtocolException("Default scope must contain exactly 1 or 48 bytes.");
        var nameField = frame.Span.Slice(1, ProtocolLimits.RegionNameSize);
        var end = nameField.IndexOf((byte)0);
        // Firmware uses strcpy when replacing preferences; bytes after NUL may be stale.
        if (end < 0)
            throw new MeshCoreProtocolException("Default scope name must be NUL-terminated.");
        string name;
        try { name = new UTF8Encoding(false, true).GetString(nameField[..end]); }
        catch (DecoderFallbackException) { throw new MeshCoreProtocolException("Default scope name is not valid UTF-8."); }
        return new DefaultFloodScopePacket(frame, NodeFloodScope.FromPreferences(name,
            new RegionKey(frame.Span[(1 + ProtocolLimits.RegionNameSize)..])));
    }
}
