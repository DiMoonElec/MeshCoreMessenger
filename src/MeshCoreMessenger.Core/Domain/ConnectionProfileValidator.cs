namespace MeshCoreMessenger.Core.Domain;

internal static class ConnectionProfileValidator
{
    public static void Validate(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Id == Guid.Empty)
        {
            throw new ArgumentException("Connection profile ID must not be empty.", nameof(profile));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(profile.Name);
        if (profile.OpenDelayMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(profile), "Open delay must not be negative.");
        }

        if (profile.CommandTimeoutMilliseconds <= 0 || profile.AcknowledgementTimeoutMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(profile), "Connection timeouts must be positive.");
        }

        switch (profile.Transport)
        {
            case ConnectionTransportKind.Tcp when
                !string.IsNullOrWhiteSpace(profile.TcpHost) &&
                profile.TcpPort is >= 1 and <= 65_535 &&
                profile.SerialPortName is null && profile.BaudRate is null:
            case ConnectionTransportKind.Serial when
                !string.IsNullOrWhiteSpace(profile.SerialPortName) &&
                profile.BaudRate > 0 && profile.TcpHost is null && profile.TcpPort is null:
                return;
            default:
                throw new ArgumentException("Connection profile contains invalid or mixed transport settings.", nameof(profile));
        }
    }
}
