namespace MeshCoreSharp.Protocol;

public static class ProtocolLimits
{
    public const int CurrentFirmwareMaxFrameSize = 176;

    // meshcore_py currently uses 300 as a defensive receive-side framing ceiling.
    public const int DefaultStreamDecoderSafetyLimit = 300;
}
