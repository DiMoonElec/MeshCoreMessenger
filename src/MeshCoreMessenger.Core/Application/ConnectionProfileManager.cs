using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;

namespace MeshCoreMessenger.Core.Application;

public sealed class ConnectionProfileManager(
    IConnectionProfileStore profiles,
    ISettingsStore settings,
    TimeProvider timeProvider) : IConnectionProfileManager
{
    internal const string SelectedProfileSettingKey = "selected-connection-profile";

    public Task<IReadOnlyList<ConnectionProfile>> GetProfilesAsync(
        CancellationToken cancellationToken = default) =>
        profiles.GetAllAsync(cancellationToken);

    public async Task<ConnectionProfile?> GetSelectedProfileAsync(
        CancellationToken cancellationToken = default)
    {
        var value = await settings.GetAsync(SelectedProfileSettingKey, cancellationToken).ConfigureAwait(false);
        return Guid.TryParse(value, out var id)
            ? await profiles.GetAsync(id, cancellationToken).ConfigureAwait(false)
            : null;
    }

    public async Task<ConnectionProfile> SaveAndSelectAsync(
        ConnectionProfileDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var existing = draft.Id is { } id && id != Guid.Empty
            ? await profiles.GetAsync(id, cancellationToken).ConfigureAwait(false)
            : null;
        var now = timeProvider.GetUtcNow();
        var profile = new ConnectionProfile
        {
            Id = existing?.Id ?? (draft.Id is { } requestedId && requestedId != Guid.Empty
                ? requestedId
                : Guid.NewGuid()),
            Name = draft.Name?.Trim() ?? string.Empty,
            Transport = draft.Transport,
            TcpHost = draft.Transport == ConnectionTransportKind.Tcp ? draft.TcpHost?.Trim() : null,
            TcpPort = draft.Transport == ConnectionTransportKind.Tcp ? draft.TcpPort : null,
            SerialPortName = draft.Transport == ConnectionTransportKind.Serial ? draft.SerialPortName?.Trim() : null,
            BaudRate = draft.Transport == ConnectionTransportKind.Serial ? draft.BaudRate : null,
            DtrEnable = draft.DtrEnable,
            RtsEnable = draft.RtsEnable,
            OpenDelayMilliseconds = draft.OpenDelayMilliseconds,
            CommandTimeoutMilliseconds = draft.CommandTimeoutMilliseconds,
            AcknowledgementTimeoutMilliseconds = draft.AcknowledgementTimeoutMilliseconds,
            AutoConnect = draft.AutoConnect,
            Reconnect = draft.Reconnect,
            ExpectedNodePublicKey = existing?.ExpectedNodePublicKey?.ToArray(),
            CreatedUtc = existing?.CreatedUtc ?? now,
            UpdatedUtc = now,
        };

        ConnectionProfileValidator.Validate(profile);
        await profiles.SaveAsync(profile, cancellationToken).ConfigureAwait(false);
        await settings.SetAsync(SelectedProfileSettingKey, profile.Id.ToString("D"), cancellationToken)
            .ConfigureAwait(false);
        return profile;
    }

    public async Task<ConnectionProfile> UpdateExpectedNodePublicKeyAsync(
        Guid profileId,
        ReadOnlyMemory<byte> publicKey,
        CancellationToken cancellationToken = default)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("Connection profile ID must not be empty.", nameof(profileId));
        }

        if (publicKey.Length != 32)
        {
            throw new ArgumentException("Expected node public key must contain exactly 32 bytes.", nameof(publicKey));
        }

        var existing = await profiles.GetAsync(profileId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Connection profile '{profileId:D}' was not found.");
        var updated = existing with
        {
            ExpectedNodePublicKey = publicKey.ToArray(),
            UpdatedUtc = timeProvider.GetUtcNow(),
        };
        await profiles.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }
}
