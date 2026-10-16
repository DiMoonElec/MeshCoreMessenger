using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Core.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed class BleProfileStoreTests
{
    [Theory]
    [InlineData(ConnectionTransportKind.Tcp)]
    [InlineData(ConnectionTransportKind.Serial)]
    [InlineData(ConnectionTransportKind.Ble)]
    public async Task RestoresEveryProfileFieldAfterReopen(ConnectionTransportKind transport)
    {
        using var temporary = new TemporaryStorage();
        var now = DateTimeOffset.UtcNow;
        var profile = new ConnectionProfile
        {
            Id = Guid.NewGuid(), Name = "Profile🐈", Transport = transport,
            TcpHost = transport == ConnectionTransportKind.Tcp ? "host.example" : null,
            TcpPort = transport == ConnectionTransportKind.Tcp ? 4321 : null,
            SerialPortName = transport == ConnectionTransportKind.Serial ? "COM8" : null,
            BaudRate = transport == ConnectionTransportKind.Serial ? 230400 : null,
            BleDeviceId = transport == ConnectionTransportKind.Ble ? "BluetoothLE#offline-device" : null,
            BleDeviceName = transport == ConnectionTransportKind.Ble ? "Нода🐈" : null,
            DtrEnable = false, RtsEnable = true, OpenDelayMilliseconds = 456,
            CommandTimeoutMilliseconds = 7890, AcknowledgementTimeoutMilliseconds = 23456,
            AutoConnect = true, Reconnect = false, CreatedUtc = now.AddDays(-1), UpdatedUtc = now,
        };
        await using (var storage = await LocalStorage.OpenAsync(temporary, Token))
            await storage.ConnectionProfiles.SaveAsync(profile, Token);
        await using var reopened = await LocalStorage.OpenAsync(temporary, Token);
        Assert.Equal(profile, await reopened.ConnectionProfiles.GetAsync(profile.Id, Token));
        Assert.Equal(profile, Assert.Single(await reopened.ConnectionProfiles.GetAllAsync(Token)));
        var updated = profile with { Name = "Updated", BleDeviceName = transport == ConnectionTransportKind.Ble ? null : profile.BleDeviceName };
        await reopened.ConnectionProfiles.SaveAsync(updated, Token);
        Assert.Equal(updated, await reopened.ConnectionProfiles.GetAsync(profile.Id, Token));
    }

    [Fact]
    public async Task SwitchingTransportPreservesIdentitySelectionAndHistoricalSession()
    {
        using var temporary = new TemporaryStorage();
        ConnectionProfile final;
        ConnectionProfile other;
        SessionRecord historical;
        await using (var storage = await LocalStorage.OpenAsync(temporary, Token))
        {
            var manager = new ConnectionProfileManager(storage.ConnectionProfiles, storage.Settings, TimeProvider.System);
            var draft = new ConnectionProfileDraft
            {
                Name = "BLE", Transport = ConnectionTransportKind.Ble,
                BleDeviceId = "offline-1", BleDeviceName = "Same name",
                AutoConnect = true, Reconnect = false,
            };
            var first = await manager.SaveAndSelectAsync(draft, Token);
            other = await manager.SaveAsync(draft with { BleDeviceId = "offline-2" }, Token);
            Assert.Equal(first, await manager.GetSelectedProfileAsync(Token));
            Assert.Equal(first.BleDeviceName, other.BleDeviceName);
            Assert.NotEqual(first.BleDeviceId, other.BleDeviceId);
            var node = await storage.Nodes.FindOrCreateAsync(new byte[32], "Actual node", first.CreatedUtc, Token);
            historical = new(Guid.NewGuid(), first.Id, node.Id, first.CreatedUtc, null, null);
            await storage.Sessions.CreateAsync(historical, Token);

            var tcp = await manager.SaveAsync(draft with
            {
                Id = first.Id, Transport = ConnectionTransportKind.Tcp, TcpHost = "localhost", TcpPort = 5000,
            }, Token);
            Assert.Null(tcp.BleDeviceId); Assert.Null(tcp.BleDeviceName);
            Assert.Equal(tcp, await storage.ConnectionProfiles.GetAsync(first.Id, Token));
            Assert.Equal(tcp, await manager.GetSelectedProfileAsync(Token));
            final = await manager.SaveAsync(draft with { Id = first.Id, BleDeviceId = "offline-new" }, Token);
            Assert.Equal(first.Id, final.Id);
            Assert.Equal(first.CreatedUtc, final.CreatedUtc);
            Assert.Null(final.TcpHost); Assert.Null(final.TcpPort);
            Assert.Null(final.SerialPortName); Assert.Null(final.BaudRate);
            Assert.True(final.AutoConnect); Assert.False(final.Reconnect);
        }
        await using (var reopened = await LocalStorage.OpenAsync(temporary, Token))
        {
            var manager = new ConnectionProfileManager(reopened.ConnectionProfiles, reopened.Settings, TimeProvider.System);
            Assert.Equal(final, await manager.GetSelectedProfileAsync(Token));
            Assert.Equal(other, await reopened.ConnectionProfiles.GetAsync(other.Id, Token));
            Assert.Equal(historical, await reopened.Sessions.GetAsync(historical.Id, Token));
        }

        // The store has no Delete API. Exercise schema deletion directly on this fixture:
        // history restricts deletion, an unused endpoint may disappear without changing selection.
        using (var writer = SqliteDatabase.CreateConnection(temporary.DatabasePath, SqliteOpenMode.ReadWrite))
        {
            writer.Open(); SqliteDatabase.ConfigureReader(writer);
            using var command = writer.CreateCommand();
            command.CommandText = "DELETE FROM ConnectionProfiles WHERE Id=$id;";
            command.Parameters.AddWithValue("$id", final.Id.ToString("D"));
            Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
            command.Parameters["$id"].Value = other.Id.ToString("D");
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        await using var afterDelete = await LocalStorage.OpenAsync(temporary, Token);
        Assert.Null(await afterDelete.ConnectionProfiles.GetAsync(other.Id, Token));
        Assert.Equal(final, Assert.Single(await afterDelete.ConnectionProfiles.GetAllAsync(Token)));
    }

    [Fact]
    public async Task InvalidBleSaveDoesNotReplacePersistedEndpointOrSelection()
    {
        using var temporary = new TemporaryStorage();
        await using var storage = await LocalStorage.OpenAsync(temporary, Token);
        var manager = new ConnectionProfileManager(storage.ConnectionProfiles, storage.Settings, TimeProvider.System);
        var draft = new ConnectionProfileDraft { Name = "Offline", Transport = ConnectionTransportKind.Ble, BleDeviceId = "offline" };
        var saved = await manager.SaveAndSelectAsync(draft, Token);
        await Assert.ThrowsAsync<ArgumentException>(() => manager.SaveAsync(draft with { Id = saved.Id, BleDeviceId = " " }, Token));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.ConnectionProfiles.SaveAsync(saved with { TcpPort = 5000 }, Token));
        Assert.Equal(saved, await manager.GetSelectedProfileAsync(Token));
        Assert.Equal(saved, await storage.ConnectionProfiles.GetAsync(saved.Id, Token));
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private sealed class TemporaryStorage : IAppPaths, IDisposable
    {
        public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "MeshCoreMessenger.BleStore.Tests", Guid.NewGuid().ToString("N"));
        public string DatabasePath => Path.Combine(DataDirectory, "messenger.db");
        public string BackupsDirectory => Path.Combine(DataDirectory, "backups");
        public void Dispose() => Directory.Delete(DataDirectory, recursive: true);
    }
}
