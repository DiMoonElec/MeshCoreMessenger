namespace MeshCoreSharp.Models;

/// <summary>A channel message accepted by the Companion. Channels have no delivery ACK.</summary>
public sealed record ChannelMessageSendResult(byte ChannelIndex, uint Timestamp);
