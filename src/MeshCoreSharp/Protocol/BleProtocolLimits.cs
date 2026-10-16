namespace MeshCoreSharp.Protocol;

// Companion 1.18 / 07a833bb: BaseChatMesh, MeshCore.h, Packet.cpp and MyMesh.cpp.
// These limits apply only to the BLE policy; the established stream limits remain unchanged.
internal static class BleProtocolLimits
{
    internal const int PrivateTextHeader = 13;
    internal const int ChannelTextHeader = 7;
    internal const int RadioTextHeader = 5;
    internal const int TextTypeAndTimestamp = 5;
    internal const int SenderSeparator = 2;
    internal const int RadioHeader = 2;
    internal const int RegionTransportCodes = 4;
    internal const int RadioPayload = 184;
    internal const int RadioPacket = 255;
    internal const int CipherBlock = 16;
    internal const int PrivateEncryptedOverhead = 4; // destination/source hash + MAC
    internal const int ChannelEncryptedOverhead = 3; // channel hash + MAC
    internal const int LegacyPrivateTextHeader = 13;
    internal const int V3PrivateTextHeader = 16;
    internal const int LegacyChannelTextHeader = 8;
    internal const int V3ChannelTextHeader = 11;
}
