namespace MeshCoreSharp.Models;

public sealed record SelfInfo(
    AdvertisementType AdvertisementType,
    byte TxPowerDbm,
    byte MaxTxPowerDbm,
    ReadOnlyMemory<byte> PublicKey,
    double AdvertisementLatitude,
    double AdvertisementLongitude,
    byte MultiAcks,
    byte AdvertisementLocationPolicy,
    byte TelemetryModeRaw,
    bool ManualAddContacts,
    double RadioFrequencyMHz,
    double RadioBandwidthKHz,
    byte SpreadingFactor,
    byte CodingRate,
    string Name)
{
    public string PublicKeyHex => Convert.ToHexString(PublicKey.Span);
    public TelemetryMode BaseTelemetryMode => (TelemetryMode)(TelemetryModeRaw & 0b11);
    public TelemetryMode LocationTelemetryMode => (TelemetryMode)((TelemetryModeRaw >> 2) & 0b11);
    public TelemetryMode EnvironmentTelemetryMode => (TelemetryMode)((TelemetryModeRaw >> 4) & 0b11);
}
