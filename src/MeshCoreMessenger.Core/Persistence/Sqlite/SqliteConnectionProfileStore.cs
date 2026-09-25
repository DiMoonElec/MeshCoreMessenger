using System.Globalization;
using Microsoft.Data.Sqlite;
using MeshCoreMessenger.Core.Domain;

namespace MeshCoreMessenger.Core.Persistence.Sqlite;

internal sealed class SqliteConnectionProfileStore(DatabaseWorker writer, DatabaseReader reader) : IConnectionProfileStore
{
    public Task<ConnectionProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Connection profile ID must not be empty.", nameof(id));
        }

        return reader.ExecuteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Id, Name, Transport, TcpHost, TcpPort, SerialPortName, BaudRate,
                       DtrEnable, RtsEnable, OpenDelayMilliseconds, CommandTimeoutMilliseconds,
                       AcknowledgementTimeoutMilliseconds, AutoConnect, Reconnect,
                       ExpectedNodePublicKey, CreatedUtc, UpdatedUtc
                FROM ConnectionProfiles
                WHERE Id = $id;
                """;
            command.Parameters.AddWithValue("$id", id.ToString("D"));

            using var result = command.ExecuteReader();
            if (!result.Read())
            {
                return null;
            }

            return new ConnectionProfile
            {
                Id = Guid.Parse(result.GetString(0)),
                Name = result.GetString(1),
                Transport = (ConnectionTransportKind)result.GetInt32(2),
                TcpHost = result.IsDBNull(3) ? null : result.GetString(3),
                TcpPort = result.IsDBNull(4) ? null : result.GetInt32(4),
                SerialPortName = result.IsDBNull(5) ? null : result.GetString(5),
                BaudRate = result.IsDBNull(6) ? null : result.GetInt32(6),
                DtrEnable = result.GetBoolean(7),
                RtsEnable = result.GetBoolean(8),
                OpenDelayMilliseconds = result.GetInt32(9),
                CommandTimeoutMilliseconds = result.GetInt32(10),
                AcknowledgementTimeoutMilliseconds = result.GetInt32(11),
                AutoConnect = result.GetBoolean(12),
                Reconnect = result.GetBoolean(13),
                ExpectedNodePublicKey = result.IsDBNull(14) ? null : (byte[])result.GetValue(14),
                CreatedUtc = DateTimeOffset.Parse(result.GetString(15), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                UpdatedUtc = DateTimeOffset.Parse(result.GetString(16), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            };
        }, cancellationToken);
    }

    public Task SaveAsync(ConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        Validate(profile);

        return writer.ExecuteAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ConnectionProfiles (
                    Id, Name, Transport, TcpHost, TcpPort, SerialPortName, BaudRate,
                    DtrEnable, RtsEnable, OpenDelayMilliseconds, CommandTimeoutMilliseconds,
                    AcknowledgementTimeoutMilliseconds, AutoConnect, Reconnect,
                    ExpectedNodePublicKey, CreatedUtc, UpdatedUtc)
                VALUES (
                    $id, $name, $transport, $tcpHost, $tcpPort, $serialPortName, $baudRate,
                    $dtrEnable, $rtsEnable, $openDelayMilliseconds, $commandTimeoutMilliseconds,
                    $acknowledgementTimeoutMilliseconds, $autoConnect, $reconnect,
                    $expectedNodePublicKey, $createdUtc, $updatedUtc)
                ON CONFLICT(Id) DO UPDATE SET
                    Name = excluded.Name,
                    Transport = excluded.Transport,
                    TcpHost = excluded.TcpHost,
                    TcpPort = excluded.TcpPort,
                    SerialPortName = excluded.SerialPortName,
                    BaudRate = excluded.BaudRate,
                    DtrEnable = excluded.DtrEnable,
                    RtsEnable = excluded.RtsEnable,
                    OpenDelayMilliseconds = excluded.OpenDelayMilliseconds,
                    CommandTimeoutMilliseconds = excluded.CommandTimeoutMilliseconds,
                    AcknowledgementTimeoutMilliseconds = excluded.AcknowledgementTimeoutMilliseconds,
                    AutoConnect = excluded.AutoConnect,
                    Reconnect = excluded.Reconnect,
                    ExpectedNodePublicKey = excluded.ExpectedNodePublicKey,
                    UpdatedUtc = excluded.UpdatedUtc;
                """;
            command.Parameters.AddWithValue("$id", profile.Id.ToString("D"));
            command.Parameters.AddWithValue("$name", profile.Name);
            command.Parameters.AddWithValue("$transport", (int)profile.Transport);
            command.Parameters.AddWithValue("$tcpHost", (object?)profile.TcpHost ?? DBNull.Value);
            command.Parameters.AddWithValue("$tcpPort", (object?)profile.TcpPort ?? DBNull.Value);
            command.Parameters.AddWithValue("$serialPortName", (object?)profile.SerialPortName ?? DBNull.Value);
            command.Parameters.AddWithValue("$baudRate", (object?)profile.BaudRate ?? DBNull.Value);
            command.Parameters.AddWithValue("$dtrEnable", profile.DtrEnable);
            command.Parameters.AddWithValue("$rtsEnable", profile.RtsEnable);
            command.Parameters.AddWithValue("$openDelayMilliseconds", profile.OpenDelayMilliseconds);
            command.Parameters.AddWithValue("$commandTimeoutMilliseconds", profile.CommandTimeoutMilliseconds);
            command.Parameters.AddWithValue("$acknowledgementTimeoutMilliseconds", profile.AcknowledgementTimeoutMilliseconds);
            command.Parameters.AddWithValue("$autoConnect", profile.AutoConnect);
            command.Parameters.AddWithValue("$reconnect", profile.Reconnect);
            command.Parameters.Add("$expectedNodePublicKey", SqliteType.Blob).Value =
                (object?)profile.ExpectedNodePublicKey ?? DBNull.Value;
            command.Parameters.AddWithValue("$createdUtc", profile.CreatedUtc.ToString("O"));
            command.Parameters.AddWithValue("$updatedUtc", profile.UpdatedUtc.ToString("O"));
            command.ExecuteNonQuery();
            return true;
        }, cancellationToken);
    }

    private static void Validate(ConnectionProfile profile)
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

        if (profile.ExpectedNodePublicKey is { Length: not 32 })
        {
            throw new ArgumentException("Expected node public key must contain exactly 32 bytes.", nameof(profile));
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
