namespace MeshCoreSharp.Models;

/// <summary>Local battery, uptime, firmware error flags and outbound queue length.</summary>
public sealed record CoreStats(ushort BatteryMillivolts, uint UptimeSeconds, ushort ErrorFlags, byte OutboundQueueLength);
