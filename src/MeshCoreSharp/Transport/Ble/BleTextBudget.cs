using System.Text;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;

namespace MeshCoreSharp.Transport.Ble;

public enum BleTextMessageKind { Private, Channel }

/// <summary>BLE-only text limits in UTF-8 bytes. An unknown flood scope is conservatively treated as scoped.</summary>
public sealed record BleTextBudget(int MaximumTextBytes, int AttPayloadBytes, int CompanionHeaderBytes,
    int SenderPrefixBytes, int RegionOverheadBytes, int RadioTextBytes)
{
    /// <summary>Unknown scope uses the conservative scoped radio overhead.</summary>
    public static BleTextBudget Calculate(ushort mtu, BleTextMessageKind kind, EffectiveFloodScope scope,
        string? senderName = null, int maximumFrameSize = ProtocolLimits.CurrentFirmwareMaxFrameSize)
    {
        if (!Enum.IsDefined(scope)) throw new ArgumentOutOfRangeException(nameof(scope));
        return Calculate(mtu, kind, senderName, scope != EffectiveFloodScope.Unscoped, maximumFrameSize);
    }

    public static BleTextBudget Calculate(ushort mtu, BleTextMessageKind kind, string? senderName = null,
        bool regionScoped = true, int maximumFrameSize = ProtocolLimits.CurrentFirmwareMaxFrameSize)
    {
        if (mtu < BleGattProfile.MinimumAttMtu) throw new ArgumentOutOfRangeException(nameof(mtu));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (maximumFrameSize <= 0) throw new ArgumentOutOfRangeException(nameof(maximumFrameSize));
        var prefix = kind == BleTextMessageKind.Channel
            ? new UTF8Encoding(false, true).GetByteCount(senderName ?? throw new ArgumentNullException(nameof(senderName))) + BleProtocolLimits.SenderSeparator : 0;
        var header = kind == BleTextMessageKind.Private ? BleProtocolLimits.PrivateTextHeader : BleProtocolLimits.ChannelTextHeader;
        var region = regionScoped ? BleProtocolLimits.RegionTransportCodes : 0;
        // Use the maximum supported route length: callers need not have a cached contact/path to send.
        var radioPayload = Math.Min(BleProtocolLimits.RadioPayload,
            BleProtocolLimits.RadioPacket - BleProtocolLimits.RadioHeader - ProtocolLimits.ContactPathSize - region);
        var encryption = kind == BleTextMessageKind.Private
            ? BleProtocolLimits.PrivateEncryptedOverhead : BleProtocolLimits.ChannelEncryptedOverhead;
        var plainBytes = (radioPayload - encryption) / BleProtocolLimits.CipherBlock * BleProtocolLimits.CipherBlock;
        var radioText = Math.Max(0, Math.Min(ProtocolLimits.MaxTextBytes, plainBytes - BleProtocolLimits.RadioTextHeader) - prefix);
        var payload = mtu - BleGattProfile.AttributeValueOverhead;
        return new(Math.Max(0, Math.Min(radioText, Math.Min(payload, maximumFrameSize) - header)),
            payload, header, prefix, region, radioText);
    }
}

/// <summary>Reads the current BLE session budget without opening a connection or sending a command.</summary>
public interface IBleTextBudgetProvider
{
    BleTextBudget GetTextBudget(BleTextMessageKind kind, string? senderName = null, bool regionScoped = true);
}

internal interface IBleClientPolicy : IBleTextBudgetProvider
{
    void SetSelfInfo(SelfInfo self);
}
