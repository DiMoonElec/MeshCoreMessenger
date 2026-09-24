namespace MeshCoreSharp.Models;

/// <summary>Local radio packet counters. Values can wrap or reset after reboot.</summary>
public sealed record PacketStats(uint Received, uint Sent, uint SentFlood, uint SentDirect,
    uint ReceivedFlood, uint ReceivedDirect, uint ReceiveErrors);
