namespace MeshCoreSharp.Models;

/// <summary>Local radio measurements and cumulative airtime in whole seconds.</summary>
public sealed record RadioStats(short NoiseFloorDbm, sbyte LastRssiDbm, double LastSnrDb,
    uint TransmitAirtimeSeconds, uint ReceiveAirtimeSeconds);
