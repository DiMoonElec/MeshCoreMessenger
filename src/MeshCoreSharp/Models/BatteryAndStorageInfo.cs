namespace MeshCoreSharp.Models;

public sealed record BatteryAndStorageInfo(
    ushort BatteryMillivolts,
    uint? UsedStorageKb,
    uint? TotalStorageKb);
