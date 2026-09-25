using MeshCoreSharp.Protocol.Encoding;
using MeshCoreSharp.Models;

namespace MeshCoreSharp.Protocol.Commands;

internal static class CompanionCommands
{
    private static readonly System.Text.UTF8Encoding StrictUtf8 = new(false, true);

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

    public static byte[] SendAdvertisement(AdvertisementMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        return [(byte)CommandType.SendSelfAdvertisement, (byte)mode];
    }

    public static byte[] GetChannel(byte index) => [(byte)CommandType.GetChannel, index];

    public static byte[] SetChannel(byte index, string name, ReadOnlySpan<byte> secret)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Contains('\0')) throw new ArgumentException("Channel name must not contain NUL.", nameof(name));
        if (secret.Length != ProtocolLimits.ChannelSecretSize)
            throw new ArgumentException($"Channel secret must be exactly {ProtocolLimits.ChannelSecretSize} bytes.", nameof(secret));

        var nameBytes = StrictUtf8.GetBytes(name);
        if (nameBytes.Length > ProtocolLimits.MaxChannelNameUtf8Bytes)
            throw new ArgumentOutOfRangeException(nameof(name),
                $"Channel name exceeds the {ProtocolLimits.MaxChannelNameUtf8Bytes}-byte UTF-8 storage limit.");

        var command = new byte[2 + ProtocolLimits.ChannelNameSize + ProtocolLimits.ChannelSecretSize];
        command[0] = (byte)CommandType.SetChannel;
        command[1] = index;
        nameBytes.CopyTo(command.AsSpan(2, ProtocolLimits.ChannelNameSize));
        secret.CopyTo(command.AsSpan(2 + ProtocolLimits.ChannelNameSize, ProtocolLimits.ChannelSecretSize));
        return Validate(command);
    }

    public static byte[] ClearChannel(byte index) =>
        SetChannel(index, string.Empty, stackalloc byte[ProtocolLimits.ChannelSecretSize]);

    public static byte[] GetStats(StatsType type)
    {
        if (!Enum.IsDefined(type)) throw new ArgumentOutOfRangeException(nameof(type));
        return [(byte)CommandType.GetStats, (byte)type];
    }

    public static byte[] SyncNextMessage() => [(byte)CommandType.SyncNextMessage];

    public static byte[] SendText(ReadOnlySpan<byte> publicKey, string text, uint timestamp)
    {
        if (publicKey.Length != ProtocolLimits.PublicKeySize)
            throw new ArgumentException("A full 32-byte recipient public key is required.", nameof(publicKey));
        var bytes = EncodeText(text, ProtocolLimits.MaxTextBytes);
        var writer = new PacketWriter();
        writer.WriteByte((byte)CommandType.SendTextMessage);
        writer.WriteByte(0); // Plain text.
        writer.WriteByte(0); // First attempt; no automatic retries.
        writer.WriteUInt32LittleEndian(timestamp);
        writer.WriteBytes(publicKey[..ProtocolLimits.MessageContactPrefixSize]);
        writer.WriteBytes(bytes);
        return Validate(writer.ToArray());
    }

    public static byte[] SendChannelText(byte channelIndex, string senderName, string text, uint timestamp)
    {
        ArgumentNullException.ThrowIfNull(senderName);
        var prefixBytes = System.Text.Encoding.UTF8.GetByteCount(senderName) + 2; // "name: " inserted by firmware.
        var bytes = EncodeText(text, ProtocolLimits.MaxTextBytes - prefixBytes);
        var writer = new PacketWriter();
        writer.WriteByte((byte)CommandType.SendChannelTextMessage);
        writer.WriteByte(0);
        writer.WriteByte(channelIndex);
        writer.WriteUInt32LittleEndian(timestamp);
        writer.WriteBytes(bytes);
        return Validate(writer.ToArray());
    }

    private static byte[] EncodeText(string text, int maxBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        if (text.Contains('\0')) throw new ArgumentException("Text must not contain NUL.", nameof(text));
        var bytes = new System.Text.UTF8Encoding(false, true).GetBytes(text);
        if (bytes.Length > maxBytes)
            throw new ArgumentOutOfRangeException(nameof(text), $"Text exceeds the {maxBytes}-byte UTF-8 limit.");
        return bytes;
    }

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
