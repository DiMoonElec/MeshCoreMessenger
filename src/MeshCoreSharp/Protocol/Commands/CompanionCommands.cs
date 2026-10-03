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

    public static byte[] AddOrUpdateContact(ContactConfiguration contact)
    {
        ArgumentNullException.ThrowIfNull(contact);
        if (contact.PublicKey.Length != ProtocolLimits.PublicKeySize)
            throw new ArgumentException($"Contact public key must be exactly {ProtocolLimits.PublicKeySize} bytes.", nameof(contact));
        if (contact.AdvertisementType == AdvertisementType.None || !Enum.IsDefined(contact.AdvertisementType))
            throw new ArgumentOutOfRangeException(nameof(contact), "A persistent contact must have a supported advertisement type.");
        if (contact.OutPath.Length != ProtocolLimits.ContactPathSize)
            throw new ArgumentException($"Contact path field must be exactly {ProtocolLimits.ContactPathSize} bytes.", nameof(contact));
        ValidatePathLength(contact.OutPathLength, nameof(contact));
        ArgumentNullException.ThrowIfNull(contact.Name);
        if (contact.Name.Contains('\0'))
            throw new ArgumentException("Contact name must not contain NUL.", nameof(contact));
        var nameBytes = StrictUtf8.GetBytes(contact.Name);
        if (nameBytes.Length > ProtocolLimits.MaxContactNameUtf8Bytes)
            throw new ArgumentOutOfRangeException(nameof(contact),
                $"Contact name exceeds the {ProtocolLimits.MaxContactNameUtf8Bytes}-byte UTF-8 storage limit.");

        var latitude = EncodeCoordinate(contact.AdvertisementLatitude, -90, 90, nameof(contact));
        var longitude = EncodeCoordinate(contact.AdvertisementLongitude, -180, 180, nameof(contact));
        var writer = new PacketWriter();
        writer.WriteByte((byte)CommandType.AddOrUpdateContact);
        writer.WriteBytes(contact.PublicKey.Span);
        writer.WriteByte((byte)contact.AdvertisementType);
        writer.WriteByte(contact.Flags);
        writer.WriteByte(contact.OutPathLength);
        writer.WriteBytes(contact.OutPath.Span);
        writer.WriteBytes(nameBytes);
        writer.WriteBytes(stackalloc byte[ProtocolLimits.ContactNameSize - nameBytes.Length]);
        writer.WriteUInt32LittleEndian(contact.LastAdvertTimestamp);
        writer.WriteInt32LittleEndian(latitude);
        writer.WriteInt32LittleEndian(longitude);
        return Validate(writer.ToArray());
    }

    public static byte[] ResetPath(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != ProtocolLimits.PublicKeySize)
            throw new ArgumentException($"Contact public key must be exactly {ProtocolLimits.PublicKeySize} bytes.", nameof(publicKey));
        var writer = new PacketWriter();
        writer.WriteByte((byte)CommandType.ResetPath);
        writer.WriteBytes(publicKey);
        return Validate(writer.ToArray());
    }

    public static byte[] RemoveContact(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != ProtocolLimits.PublicKeySize)
            throw new ArgumentException($"Contact public key must be exactly {ProtocolLimits.PublicKeySize} bytes.", nameof(publicKey));
        var writer = new PacketWriter();
        writer.WriteByte((byte)CommandType.RemoveContact);
        writer.WriteBytes(publicKey);
        return Validate(writer.ToArray());
    }

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
        var bytes = TextMessageValidator.EncodeText(text, ProtocolLimits.MaxTextBytes);
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
        var bytes = TextMessageValidator.EncodeText(text, TextMessageValidator.GetChannelTextLimit(senderName));
        var writer = new PacketWriter();
        writer.WriteByte((byte)CommandType.SendChannelTextMessage);
        writer.WriteByte(0);
        writer.WriteByte(channelIndex);
        writer.WriteUInt32LittleEndian(timestamp);
        writer.WriteBytes(bytes);
        return Validate(writer.ToArray());
    }

    private static int EncodeCoordinate(double value, double minimum, double maximum, string parameterName)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(parameterName,
                $"Coordinate must be finite and between {minimum} and {maximum} degrees.");
        return checked((int)Math.Round(value * 1_000_000d, MidpointRounding.AwayFromZero));
    }

    private static void ValidatePathLength(byte pathLength, string parameterName)
    {
        if (pathLength == byte.MaxValue) return;
        var hashSize = (pathLength >> 6) + 1;
        var hashCount = pathLength & 0x3F;
        if ((pathLength >> 6) == 3 || hashCount * hashSize > ProtocolLimits.ContactPathSize)
            throw new ArgumentOutOfRangeException(parameterName, "Contact path descriptor is invalid.");
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
