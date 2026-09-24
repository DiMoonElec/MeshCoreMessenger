namespace MeshCoreSharp.Models;

public sealed record DeviceInfo(
    byte FirmwareProtocolVersion,
    int? MaxContacts,
    byte? MaxChannels,
    uint? BlePin,
    string? FirmwareBuild,
    string? Model,
    string? SemanticVersion,
    bool? RepeaterMode,
    byte? PathHashMode);
