namespace MeshCoreSharp.Protocol;

public static class ProtocolLimits
{
    public const int PublicKeySize = 32;
    public const int MessageContactPrefixSize = 6;
    public const int MessageSenderPrefixSize = 4;
    public const int ContactPathSize = 64;
    public const int ContactNameSize = 32;
    public const int MaxContactNameUtf8Bytes = ContactNameSize - 1;
    public const int ChannelNameSize = 32;
    public const int MaxChannelNameUtf8Bytes = ChannelNameSize - 1;
    public const int ChannelSecretSize = 16;
    public const int MaxTextBytes = 160;
    internal const int ExpectedAckTableSize = 8;

    public const int CurrentFirmwareMaxFrameSize = 176;

    // meshcore_py currently uses 300 as a defensive receive-side framing ceiling.
    public const int DefaultStreamDecoderSafetyLimit = 300;
}
