namespace MeshCoreSharp.Transport.Ble;

internal static class BleGattProfile
{
    internal static readonly Guid Service = new("6E400001-B5A3-F393-E0A9-E50E24DCCA9E");
    internal static readonly Guid AppToCompanion = new("6E400002-B5A3-F393-E0A9-E50E24DCCA9E");
    internal static readonly Guid CompanionToApp = new("6E400003-B5A3-F393-E0A9-E50E24DCCA9E");
    internal const int AttributeValueOverhead = 3;
    internal const ushort MinimumAttMtu = 23;
}
