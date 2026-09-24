using MeshCoreSharp.Protocol.Encoding;

namespace MeshCoreSharp.Protocol.Commands;

internal static class CompanionCommands
{
    public static byte[] AppStart(string applicationName, byte appProtocolVersion)
    {
        ArgumentNullException.ThrowIfNull(applicationName);

        var writer = new PacketWriter();
        writer.WriteByte((byte)CommandType.AppStart);
        writer.WriteByte(appProtocolVersion);
        writer.WriteBytes(stackalloc byte[6]);
        writer.WriteUtf8(applicationName);
        return Validate(writer.ToArray());
    }

    public static byte[] GetDeviceTime() => [(byte)CommandType.GetDeviceTime];

    public static byte[] GetContacts() => [(byte)CommandType.GetContacts];

    public static byte[] SetDeviceTime(DateTimeOffset value)
    {
        var unixSeconds = value.ToUnixTimeSeconds();
        if (unixSeconds < 0 || unixSeconds > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value), "MeshCore device time must fit in an unsigned 32-bit Unix timestamp.");

        var writer = new PacketWriter();
        writer.WriteByte((byte)CommandType.SetDeviceTime);
        writer.WriteUInt32LittleEndian((uint)unixSeconds);
        return writer.ToArray();
    }

    public static byte[] DeviceQuery(byte appTargetProtocolVersion) =>
        [(byte)CommandType.DeviceQuery, appTargetProtocolVersion];

    public static byte[] GetBatteryAndStorage() => [(byte)CommandType.GetBatteryAndStorage];

    private static byte[] Validate(byte[] command)
    {
        if (command.Length is <= 0 or > ProtocolLimits.CurrentFirmwareMaxFrameSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(command),
                $"Companion command must be 1..{ProtocolLimits.CurrentFirmwareMaxFrameSize} bytes.");
        }

        return command;
    }
}
