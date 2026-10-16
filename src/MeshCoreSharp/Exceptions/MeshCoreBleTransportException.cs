namespace MeshCoreSharp.Exceptions;

public enum MeshCoreBleFailureReason
{
    UnsupportedPlatform,
    AdapterUnavailable,
    PairingRequired,
    AccessDenied,
    MissingService,
    MissingCharacteristic,
    InsufficientMtu,
    DeviceUnreachable,
    TransportFailure,
    RadioOff,
    UnsupportedCharacteristics,
    ReceiveQueueOverflow,
    InvalidFrame,
    WriteOutcomeUnknown,
}

/// <summary>A BLE failure with platform-neutral diagnostics. Never include pairing secrets in the message.</summary>
public sealed class MeshCoreBleTransportException : MeshCoreTransportException
{
    public MeshCoreBleTransportException(
        MeshCoreBleFailureReason reason,
        string message,
        Exception? innerException = null,
        int? nativeHResult = null,
        int? nativeStatus = null,
        byte? protocolError = null)
        : base(message, innerException)
    {
        if (!Enum.IsDefined(reason))
            throw new ArgumentOutOfRangeException(nameof(reason));
        Reason = reason;
        NativeHResult = nativeHResult;
        NativeStatus = nativeStatus;
        ProtocolError = protocolError;
        if (nativeHResult is { } hResult)
            HResult = hResult;
    }

    public MeshCoreBleFailureReason Reason { get; }
    public int? NativeHResult { get; }
    /// <summary>Original numeric platform GATT/adapter status, when available.</summary>
    public int? NativeStatus { get; }
    public byte? ProtocolError { get; }
}
