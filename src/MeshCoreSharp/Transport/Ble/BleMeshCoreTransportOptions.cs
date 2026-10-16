using MeshCoreSharp.Protocol;

namespace MeshCoreSharp.Transport.Ble;

/// <summary>Settings for one explicitly selected BLE Companion endpoint.</summary>
public sealed record BleMeshCoreTransportOptions
{
    /// <summary>Opaque platform device ID. It is neither a display name nor a MeshCore node identity.</summary>
    public required string DeviceId { get; init; }
    public TimeSpan OpenTimeout { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>Deadline for one GATT write after acquiring the write gate.</summary>
    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public int ReceiveQueueCapacity { get; init; } = 256;
    /// <summary>Maximum complete logical frame; the default matches the current firmware baseline.</summary>
    public int MaximumFrameSize { get; init; } = ProtocolLimits.CurrentFirmwareMaxFrameSize;
    /// <summary>Host UI dispatcher for Windows device access/consent. Required by the Windows GATT backend.</summary>
    public IBleDeviceAccessDispatcher? DeviceAccessDispatcher { get; init; }

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DeviceId);
        // Stay within the range accepted by CancellationTokenSource timers.
        if (OpenTimeout <= TimeSpan.Zero || OpenTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(OpenTimeout));
        if (WriteTimeout <= TimeSpan.Zero || WriteTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(WriteTimeout));
        if (ReceiveQueueCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(ReceiveQueueCapacity));
        if (MaximumFrameSize <= 0 || MaximumFrameSize > ushort.MaxValue - BleGattProfile.AttributeValueOverhead)
            throw new ArgumentOutOfRangeException(nameof(MaximumFrameSize));
    }
}
