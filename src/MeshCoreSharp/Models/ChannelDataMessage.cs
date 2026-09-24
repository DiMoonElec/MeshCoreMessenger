namespace MeshCoreSharp.Models;

/// <summary>A binary channel datagram. Firmware does not supply a timestamp for this packet.</summary>
public sealed record ChannelDataMessage(
    byte ChannelIndex,
    byte PathLength,
    ushort DataType,
    ReadOnlyMemory<byte> Data,
    double? SnrDb) : ReceivedMessage(PathLength, SnrDb);
