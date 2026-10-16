using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed class BleProfileModelTests
{
    [Fact]
    public async Task NormalizesBleDraftAndPreservesEndpointAcrossUpdates()
    {
        var store = new MemoryProfiles();
        var manager = new ConnectionProfileManager(store, new MemorySettings(), TimeProvider.System);
        var draft = new ConnectionProfileDraft
        {
            Name = " BLE ", Transport = ConnectionTransportKind.Ble,
            BleDeviceId = " device-1 ", BleDeviceName = " Same name ",
            TcpHost = "stale", TcpPort = 5000, SerialPortName = "COM1", BaudRate = 115200,
            AutoConnect = true, Reconnect = false, CommandTimeoutMilliseconds = 1234,
            AcknowledgementTimeoutMilliseconds = 5678,
        };
        var saved = await manager.SaveAsync(draft, TestContext.Current.CancellationToken);
        Assert.Equal("device-1", saved.BleDeviceId);
        Assert.Equal("Same name", saved.BleDeviceName);
        Assert.Null(saved.TcpHost); Assert.Null(saved.TcpPort);
        Assert.Null(saved.SerialPortName); Assert.Null(saved.BaudRate);
        Assert.True(saved.AutoConnect); Assert.False(saved.Reconnect);
        Assert.Equal(1234, saved.CommandTimeoutMilliseconds);
        Assert.Equal(5678, saved.AcknowledgementTimeoutMilliseconds);
        Assert.Null(await manager.GetSelectedProfileAsync(TestContext.Current.CancellationToken));
        var updated = await manager.SaveAsync(draft with
        {
            Id = saved.Id, BleDeviceId = saved.BleDeviceId, BleDeviceName = saved.BleDeviceName,
        }, TestContext.Current.CancellationToken);
        Assert.Equal(saved.Id, updated.Id);
        Assert.Equal(saved.CreatedUtc, updated.CreatedUtc);
        Assert.Equal(saved.BleDeviceId, updated.BleDeviceId);
        var other = await manager.SaveAsync(draft with { BleDeviceId = "device-2" }, TestContext.Current.CancellationToken);
        Assert.NotEqual(saved.Id, other.Id);
        Assert.Equal(saved.BleDeviceName, other.BleDeviceName);
        Assert.NotEqual(saved.BleDeviceId, other.BleDeviceId);

        var tcp = await manager.SaveAsync(draft with
        {
            Id = saved.Id, Transport = ConnectionTransportKind.Tcp,
        }, TestContext.Current.CancellationToken);
        Assert.Null(tcp.BleDeviceId); Assert.Null(tcp.BleDeviceName);
        Assert.Equal("stale", tcp.TcpHost);
        var serial = await manager.SaveAsync(draft with
        {
            Id = saved.Id, Transport = ConnectionTransportKind.Serial,
        }, TestContext.Current.CancellationToken);
        Assert.Null(serial.BleDeviceId); Assert.Null(serial.BleDeviceName);
        Assert.Equal("COM1", serial.SerialPortName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RejectsMissingBleId(string? id) =>
        Assert.Throws<ArgumentException>(() => ConnectionProfileValidator.Validate(BleProfile() with { BleDeviceId = id }));

    [Fact]
    public void ValidatesTransportIsolationAndKeepsEnumValues()
    {
        Assert.Equal(0, (int)ConnectionTransportKind.Tcp);
        Assert.Equal(1, (int)ConnectionTransportKind.Serial);
        Assert.Equal(2, (int)ConnectionTransportKind.Ble);
        var ble = BleProfile();
        ConnectionProfileValidator.Validate(ble);
        ConnectionProfileValidator.Validate(ble with { BleDeviceName = null });
        foreach (var mixed in new[]
        {
            ble with { TcpHost = "host" }, ble with { TcpPort = 1 },
            ble with { SerialPortName = "COM1" }, ble with { BaudRate = 115200 },
            ble with { Transport = ConnectionTransportKind.Tcp, TcpHost = "host", TcpPort = 1 },
            ble with { Transport = ConnectionTransportKind.Serial, SerialPortName = "COM1", BaudRate = 115200 },
            ble with { Transport = ConnectionTransportKind.Tcp, BleDeviceId = null, TcpHost = "host", TcpPort = 1 },
            ble with { Transport = ConnectionTransportKind.Serial, BleDeviceId = null, SerialPortName = "COM1", BaudRate = 115200 },
        }) Assert.Throws<ArgumentException>(() => ConnectionProfileValidator.Validate(mixed));
        Assert.Throws<ArgumentOutOfRangeException>(() => ConnectionProfileValidator.Validate(ble with { CommandTimeoutMilliseconds = 0 }));
    }

    private static ConnectionProfile BleProfile() => new()
    {
        Id = Guid.NewGuid(), Name = "BLE", Transport = ConnectionTransportKind.Ble,
        BleDeviceId = "offline-device", BleDeviceName = "Hint",
        CreatedUtc = DateTimeOffset.UtcNow, UpdatedUtc = DateTimeOffset.UtcNow,
    };

    private sealed class MemoryProfiles : IConnectionProfileStore
    {
        private readonly Dictionary<Guid, ConnectionProfile> _profiles = [];
        public Task<IReadOnlyList<ConnectionProfile>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConnectionProfile>>(_profiles.Values.ToArray());
        public Task<ConnectionProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_profiles.GetValueOrDefault(id));
        public Task SaveAsync(ConnectionProfile profile, CancellationToken cancellationToken = default)
        {
            _profiles[profile.Id] = profile;
            return Task.CompletedTask;
        }
    }

    private sealed class MemorySettings : ISettingsStore
    {
        private readonly Dictionary<string, string> _values = [];
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(_values.GetValueOrDefault(key));
        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }
    }
}
