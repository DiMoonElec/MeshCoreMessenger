using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreSharp;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed class ConnectionProfileTests
{
    [Fact]
    public async Task SaveOnlyNormalizesWithoutChangingStartupSelection()
    {
        using var temporary = new TemporaryDirectory();
        await using var storage = await LocalStorage.OpenAsync(temporary.Paths, CancellationToken);
        var manager = new ConnectionProfileManager(storage.ConnectionProfiles, storage.Settings, TimeProvider.System);
        var first = await manager.SaveAndSelectAsync(new() { Name = "First", Transport = ConnectionTransportKind.Tcp, TcpHost = "localhost", TcpPort = 5000 }, CancellationToken);
        var second = await manager.SaveAsync(new() { Name = "  Second  ", Transport = ConnectionTransportKind.Tcp, TcpHost = " localhost ", TcpPort = 6000 }, CancellationToken);
        Assert.Equal("Second", second.Name);
        Assert.Equal("localhost", second.TcpHost);
        Assert.Equal(first, await manager.GetSelectedProfileAsync(CancellationToken));
        Assert.Equal(second, await storage.ConnectionProfiles.GetAsync(second.Id, CancellationToken));
        var updated = await manager.SaveAsync(new() { Id = second.Id, Name = "Updated", Transport = ConnectionTransportKind.Tcp, TcpHost = "localhost", TcpPort = 6001 }, CancellationToken);
        Assert.Equal(second.CreatedUtc, updated.CreatedUtc);
        Assert.Equal(first, await manager.GetSelectedProfileAsync(CancellationToken));
    }

    [Fact]
    public async Task SaveOnlyDoesNotSelectFirstProfileAndRejectsInvalidDraft()
    {
        using var temporary = new TemporaryDirectory();
        await using var storage = await LocalStorage.OpenAsync(temporary.Paths, CancellationToken);
        var manager = new ConnectionProfileManager(storage.ConnectionProfiles, storage.Settings, TimeProvider.System);
        await manager.SaveAsync(new() { Name = "Only", Transport = ConnectionTransportKind.Tcp, TcpHost = "localhost", TcpPort = 5000 }, CancellationToken);
        Assert.Null(await manager.GetSelectedProfileAsync(CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.SaveAsync(new() { Name = "Bad", Transport = ConnectionTransportKind.Tcp, TcpHost = "", TcpPort = 0 }, CancellationToken));
        Assert.Single(await manager.GetProfilesAsync(CancellationToken));
    }

    [Fact]
    public async Task ListsProfilesByNameThenId()
    {
        using var temporary = new TemporaryDirectory();
        await using var storage = await LocalStorage.OpenAsync(temporary.Paths, CancellationToken);
        var now = DateTimeOffset.UtcNow;
        var second = CreateTcpProfile(Guid.Parse("00000000-0000-0000-0000-000000000002"), "zulu", now);
        var first = CreateTcpProfile(Guid.Parse("00000000-0000-0000-0000-000000000001"), "Alpha", now);
        await storage.ConnectionProfiles.SaveAsync(second, CancellationToken);
        await storage.ConnectionProfiles.SaveAsync(first, CancellationToken);

        var profiles = await storage.ConnectionProfiles.GetAllAsync(CancellationToken);

        Assert.Equal([first.Id, second.Id], profiles.Select(item => item.Id));
    }

    [Fact]
    public async Task SavesNormalizesAndRestoresSelectedProfile()
    {
        using var temporary = new TemporaryDirectory();
        await using var storage = await LocalStorage.OpenAsync(temporary.Paths, CancellationToken);
        var now = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        var manager = new ConnectionProfileManager(
            storage.ConnectionProfiles,
            storage.Settings,
            new FixedTimeProvider(now));
        var draft = new ConnectionProfileDraft
        {
            Name = "  Local TCP  ",
            Transport = ConnectionTransportKind.Tcp,
            TcpHost = " 192.0.2.15 ",
            TcpPort = 5000,
            CommandTimeoutMilliseconds = 12_000,
            AcknowledgementTimeoutMilliseconds = 45_000,
            AutoConnect = true,
            Reconnect = true,
        };

        var saved = await manager.SaveAndSelectAsync(draft, CancellationToken);
        var selected = await manager.GetSelectedProfileAsync(CancellationToken);

        Assert.Equal("Local TCP", saved.Name);
        Assert.Equal("192.0.2.15", saved.TcpHost);
        Assert.Equal(now, saved.CreatedUtc);
        Assert.Equal(now, saved.UpdatedUtc);
        Assert.Equal(saved, selected);
    }

    [Fact]
    public async Task UpdatingProfilePreservesCreationTime()
    {
        using var temporary = new TemporaryDirectory();
        await using var storage = await LocalStorage.OpenAsync(temporary.Paths, CancellationToken);
        var created = new DateTimeOffset(2026, 9, 24, 8, 0, 0, TimeSpan.Zero);
        var updated = created.AddDays(1);
        var existing = CreateTcpProfile(Guid.NewGuid(), "Original", created);
        await storage.ConnectionProfiles.SaveAsync(existing, CancellationToken);
        var manager = new ConnectionProfileManager(
            storage.ConnectionProfiles,
            storage.Settings,
            new FixedTimeProvider(updated));

        var saved = await manager.SaveAndSelectAsync(new ConnectionProfileDraft
        {
            Id = existing.Id,
            Name = "Updated",
            Transport = ConnectionTransportKind.Serial,
            SerialPortName = "/dev/cu.test",
            BaudRate = 230_400,
            DtrEnable = false,
            RtsEnable = true,
            OpenDelayMilliseconds = 750,
            CommandTimeoutMilliseconds = 9_000,
            AcknowledgementTimeoutMilliseconds = 20_000,
        }, CancellationToken);

        Assert.Equal(existing.Id, saved.Id);
        Assert.Equal(created, saved.CreatedUtc);
        Assert.Equal(updated, saved.UpdatedUtc);
        Assert.Null(saved.TcpHost);
        Assert.Null(saved.TcpPort);
        Assert.Equal("/dev/cu.test", saved.SerialPortName);
    }

    [Fact]
    public async Task MissingOrMalformedSelectedProfileDoesNotChooseFirstProfile()
    {
        using var temporary = new TemporaryDirectory();
        await using var storage = await LocalStorage.OpenAsync(temporary.Paths, CancellationToken);
        var profile = CreateTcpProfile(Guid.NewGuid(), "Available", DateTimeOffset.UtcNow);
        await storage.ConnectionProfiles.SaveAsync(profile, CancellationToken);
        var manager = new ConnectionProfileManager(
            storage.ConnectionProfiles,
            storage.Settings,
            TimeProvider.System);

        Assert.Null(await manager.GetSelectedProfileAsync(CancellationToken));
        await storage.Settings.SetAsync(
            ConnectionProfileManager.SelectedProfileSettingKey,
            Guid.NewGuid().ToString("D"),
            CancellationToken);
        Assert.Null(await manager.GetSelectedProfileAsync(CancellationToken));
        await storage.Settings.SetAsync(
            ConnectionProfileManager.SelectedProfileSettingKey,
            "not-a-guid",
            CancellationToken);
        Assert.Null(await manager.GetSelectedProfileAsync(CancellationToken));
    }

    [Fact]
    public async Task SelectsExistingProfileWithoutRewritingIt()
    {
        using var temporary = new TemporaryDirectory();
        await using var storage = await LocalStorage.OpenAsync(temporary.Paths, CancellationToken);
        var first = CreateTcpProfile(Guid.NewGuid(), "First", DateTimeOffset.UtcNow.AddDays(-1));
        var second = CreateTcpProfile(Guid.NewGuid(), "Second", DateTimeOffset.UtcNow);
        await storage.ConnectionProfiles.SaveAsync(first, CancellationToken);
        await storage.ConnectionProfiles.SaveAsync(second, CancellationToken);
        var manager = new ConnectionProfileManager(
            storage.ConnectionProfiles,
            storage.Settings,
            TimeProvider.System);

        var selected = await manager.SelectAsync(second.Id, CancellationToken);

        Assert.Equal(second, selected);
        Assert.Equal(second, await manager.GetSelectedProfileAsync(CancellationToken));
        Assert.Equal(second, await storage.ConnectionProfiles.GetAsync(second.Id, CancellationToken));
    }

    [Fact]
    public async Task MissingProfileSelectionDoesNotReplaceCurrentSelection()
    {
        using var temporary = new TemporaryDirectory();
        await using var storage = await LocalStorage.OpenAsync(temporary.Paths, CancellationToken);
        var profile = CreateTcpProfile(Guid.NewGuid(), "Current", DateTimeOffset.UtcNow);
        await storage.ConnectionProfiles.SaveAsync(profile, CancellationToken);
        var manager = new ConnectionProfileManager(
            storage.ConnectionProfiles,
            storage.Settings,
            TimeProvider.System);
        await manager.SelectAsync(profile.Id, CancellationToken);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            manager.SelectAsync(Guid.NewGuid(), CancellationToken));

        Assert.Equal(profile, await manager.GetSelectedProfileAsync(CancellationToken));
    }

    [Fact]
    public void MapsTcpProfileWithoutEnablingAutomaticMessageDrain()
    {
        var profile = CreateTcpProfile(Guid.NewGuid(), "TCP", DateTimeOffset.UtcNow) with
        {
            TcpHost = "mesh.example",
            TcpPort = 4321,
            CommandTimeoutMilliseconds = 8_500,
            AcknowledgementTimeoutMilliseconds = 27_000,
        };

        var mapping = ConnectionProfileMapper.Map(profile);

        Assert.Equal(ConnectionTransportKind.Tcp, mapping.Transport);
        Assert.Equal("mesh.example", mapping.TcpOptions?.Host);
        Assert.Equal(4321, mapping.TcpOptions?.Port);
        Assert.Null(mapping.SerialOptions);
        Assert.Equal("MeshCoreMessenger", mapping.ClientOptions.ApplicationName);
        Assert.Equal(TimeSpan.FromMilliseconds(8_500), mapping.ClientOptions.CommandTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(27_000), mapping.ClientOptions.MaximumAckTimeout);
        Assert.False(mapping.ClientOptions.AutoReceiveMessages);
    }

    [Fact]
    public void MapsEverySerialLineAndTimingOption()
    {
        var now = DateTimeOffset.UtcNow;
        var profile = new ConnectionProfile
        {
            Id = Guid.NewGuid(),
            Name = "Serial",
            Transport = ConnectionTransportKind.Serial,
            SerialPortName = "/dev/cu.usbserial-test",
            BaudRate = 230_400,
            DtrEnable = false,
            RtsEnable = true,
            OpenDelayMilliseconds = 2_750,
            CommandTimeoutMilliseconds = 11_000,
            AcknowledgementTimeoutMilliseconds = 900,
            CreatedUtc = now,
            UpdatedUtc = now,
        };

        var mapping = ConnectionProfileMapper.Map(profile);

        Assert.Equal(ConnectionTransportKind.Serial, mapping.Transport);
        Assert.Null(mapping.TcpOptions);
        Assert.Equal(profile.SerialPortName, mapping.SerialOptions?.PortName);
        Assert.Equal(profile.BaudRate, mapping.SerialOptions?.BaudRate);
        Assert.False(mapping.SerialOptions?.DtrEnable);
        Assert.True(mapping.SerialOptions?.RtsEnable);
        Assert.Equal(TimeSpan.FromMilliseconds(2_750), mapping.SerialOptions?.OpenDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(900), mapping.ClientOptions.MinimumAckTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(900), mapping.ClientOptions.MaximumAckTimeout);
        Assert.False(mapping.ClientOptions.AutoReceiveMessages);
    }

    [Fact]
    public async Task FactoryConstructsDisconnectedClientWithoutOpeningTransport()
    {
        var profile = CreateTcpProfile(Guid.NewGuid(), "No connect", DateTimeOffset.UtcNow);
        var factory = new MeshCoreClientFactory();

        await using var client = factory.Create(profile);

        Assert.Equal(MeshCoreConnectionState.Disconnected, client.State);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task FactoryDoesNotOpenNonexistentSerialPort()
    {
        var now = DateTimeOffset.UtcNow;
        var profile = new ConnectionProfile
        {
            Id = Guid.NewGuid(),
            Name = "Offline serial",
            Transport = ConnectionTransportKind.Serial,
            SerialPortName = "/definitely/not/a/serial-port",
            BaudRate = 115_200,
            DtrEnable = true,
            RtsEnable = true,
            OpenDelayMilliseconds = 2_000,
            CommandTimeoutMilliseconds = 10_000,
            AcknowledgementTimeoutMilliseconds = 30_000,
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        var factory = new MeshCoreClientFactory();

        await using var client = factory.Create(profile);

        Assert.Equal(MeshCoreConnectionState.Disconnected, client.State);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task RejectsMixedOrInvalidTransportFieldsBeforeSaving()
    {
        using var temporary = new TemporaryDirectory();
        await using var storage = await LocalStorage.OpenAsync(temporary.Paths, CancellationToken);
        var manager = new ConnectionProfileManager(
            storage.ConnectionProfiles,
            storage.Settings,
            TimeProvider.System);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => manager.SaveAndSelectAsync(
            new ConnectionProfileDraft
            {
                Name = "Invalid TCP",
                Transport = ConnectionTransportKind.Tcp,
                TcpHost = "localhost",
                TcpPort = 70_000,
            },
            CancellationToken));

        Assert.Contains("invalid or mixed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await storage.ConnectionProfiles.GetAllAsync(CancellationToken));
        Assert.Null(await manager.GetSelectedProfileAsync(CancellationToken));
    }

    private static ConnectionProfile CreateTcpProfile(Guid id, string name, DateTimeOffset now) => new()
    {
        Id = id,
        Name = name,
        Transport = ConnectionTransportKind.Tcp,
        TcpHost = "127.0.0.1",
        TcpPort = 5000,
        OpenDelayMilliseconds = 0,
        CommandTimeoutMilliseconds = 10_000,
        AcknowledgementTimeoutMilliseconds = 30_000,
        CreatedUtc = now,
        UpdatedUtc = now,
    };

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "MeshCoreMessenger.ConnectionProfile.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Paths = new TestAppPaths(Path);
        }

        public string Path { get; }
        public TestAppPaths Paths { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    private sealed record TestAppPaths(string DataDirectory) : IAppPaths
    {
        public string DatabasePath { get; } = System.IO.Path.Combine(DataDirectory, "messenger.db");
        public string BackupsDirectory { get; } = System.IO.Path.Combine(DataDirectory, "backups");
    }
}
