using Microsoft.Extensions.Logging.Abstractions;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Desktop.ViewModels;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed class ConnectionProfilesViewModelTests
{
    [Fact]
    public async Task LoadsSelectedProfileAndKeepsUnavailableSavedSerialPortVisible()
    {
        var saved = CreateSerialProfile("Saved", "/dev/cu.saved");
        var manager = new FakeProfileManager
        {
            Profiles = [saved],
            Selected = saved,
        };
        var viewModel = CreateViewModel(manager, ["/dev/cu.detected"]);

        await viewModel.LoadAsync(CancellationToken);

        Assert.Equal(saved.Id, viewModel.SelectedProfile?.Id);
        Assert.Equal("/dev/cu.saved", viewModel.SerialPortName);
        Assert.Equal(["/dev/cu.detected", "/dev/cu.saved"], viewModel.AvailableSerialPorts);
        Assert.Contains("Saved", viewModel.Status, StringComparison.Ordinal);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task MissingSelectionDoesNotChooseFirstAvailableProfile()
    {
        var manager = new FakeProfileManager
        {
            Profiles = [CreateSerialProfile("Available", "/dev/cu.available")],
        };
        var viewModel = CreateViewModel(manager, []);

        await viewModel.LoadAsync(CancellationToken);

        Assert.Single(viewModel.AvailableProfiles);
        Assert.Null(viewModel.SelectedProfile);
        Assert.Equal("Профиль подключения не выбран", viewModel.Status);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task SavesAndSelectsEverySerialSettingWithoutOpeningPort()
    {
        var manager = new FakeProfileManager();
        var catalog = new FakeSerialPortCatalog(["/dev/cu.usbserial-0001"]);
        var viewModel = CreateViewModel(manager, catalog);
        await viewModel.LoadAsync(CancellationToken);
        viewModel.ProfileName = "USB node";
        viewModel.SelectedTransport = ConnectionTransportKind.Serial;
        viewModel.SerialPortName = "/dev/cu.usbserial-0001";
        viewModel.BaudRate = "230400";
        viewModel.DtrEnable = false;
        viewModel.RtsEnable = true;
        viewModel.OpenDelayMilliseconds = "2750";
        viewModel.CommandTimeoutMilliseconds = "12000";
        viewModel.AcknowledgementTimeoutMilliseconds = "45000";
        viewModel.AutoConnect = true;
        viewModel.Reconnect = false;

        await viewModel.SaveAndSelectCommand.ExecuteAsync(null);

        var draft = Assert.IsType<ConnectionProfileDraft>(manager.SavedDraft);
        Assert.Equal("USB node", draft.Name);
        Assert.Equal(ConnectionTransportKind.Serial, draft.Transport);
        Assert.Equal("/dev/cu.usbserial-0001", draft.SerialPortName);
        Assert.Equal(230_400, draft.BaudRate);
        Assert.False(draft.DtrEnable);
        Assert.True(draft.RtsEnable);
        Assert.Equal(2_750, draft.OpenDelayMilliseconds);
        Assert.Equal(12_000, draft.CommandTimeoutMilliseconds);
        Assert.Equal(45_000, draft.AcknowledgementTimeoutMilliseconds);
        Assert.True(draft.AutoConnect);
        Assert.False(draft.Reconnect);
        Assert.Equal(2, catalog.Calls);
        Assert.False(viewModel.HasError);
        Assert.NotNull(viewModel.SelectedProfile);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task InvalidTcpPortIsShownWithoutCallingPersistence()
    {
        var manager = new FakeProfileManager();
        var viewModel = CreateViewModel(manager, []);
        await viewModel.LoadAsync(CancellationToken);
        viewModel.ProfileName = "Broken";
        viewModel.SelectedTransport = ConnectionTransportKind.Tcp;
        viewModel.TcpHost = "localhost";
        viewModel.TcpPort = "70000";

        await viewModel.SaveAndSelectCommand.ExecuteAsync(null);

        Assert.Null(manager.SavedDraft);
        Assert.True(viewModel.HasError);
        Assert.Contains("TCP port", viewModel.ErrorMessage, StringComparison.Ordinal);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task NewProfileDoesNotOverwriteCurrentlySelectedProfile()
    {
        var saved = CreateSerialProfile("Saved", "/dev/cu.saved");
        var manager = new FakeProfileManager
        {
            Profiles = [saved],
            Selected = saved,
        };
        var viewModel = CreateViewModel(manager, []);
        await viewModel.LoadAsync(CancellationToken);

        viewModel.NewProfileCommand.Execute(null);

        Assert.Null(viewModel.SelectedProfile);
        Assert.Equal(string.Empty, viewModel.ProfileName);
        Assert.Equal(ConnectionTransportKind.Serial, viewModel.SelectedTransport);
        Assert.Equal("115200", viewModel.BaudRate);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task NewProfileClearsEditorWhenNoProfileWasSelected()
    {
        var manager = new FakeProfileManager();
        var viewModel = CreateViewModel(manager, []);
        await viewModel.LoadAsync(CancellationToken);
        viewModel.ProfileName = "Unsaved";
        viewModel.BaudRate = "9600";

        viewModel.NewProfileCommand.Execute(null);

        Assert.Equal(string.Empty, viewModel.ProfileName);
        Assert.Equal("115200", viewModel.BaudRate);
        Assert.Equal("Новый профиль", viewModel.Status);
        await viewModel.StopAsync();
    }

    private static ConnectionProfilesViewModel CreateViewModel(
        FakeProfileManager manager,
        IReadOnlyList<string> ports) =>
        CreateViewModel(manager, new FakeSerialPortCatalog(ports));

    private static ConnectionProfilesViewModel CreateViewModel(
        FakeProfileManager manager,
        ISerialPortCatalog catalog) =>
        new(manager, catalog, NullLogger<ConnectionProfilesViewModel>.Instance);

    private static ConnectionProfile CreateSerialProfile(string name, string portName)
    {
        var now = DateTimeOffset.UtcNow;
        return new ConnectionProfile
        {
            Id = Guid.NewGuid(),
            Name = name,
            Transport = ConnectionTransportKind.Serial,
            SerialPortName = portName,
            BaudRate = 115_200,
            DtrEnable = true,
            RtsEnable = true,
            OpenDelayMilliseconds = 2_000,
            CommandTimeoutMilliseconds = 10_000,
            AcknowledgementTimeoutMilliseconds = 30_000,
            Reconnect = true,
            CreatedUtc = now,
            UpdatedUtc = now,
        };
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private sealed class FakeProfileManager : IConnectionProfileManager
    {
        public IReadOnlyList<ConnectionProfile> Profiles { get; set; } = [];
        public ConnectionProfile? Selected { get; set; }
        public ConnectionProfileDraft? SavedDraft { get; private set; }

        public Task<IReadOnlyList<ConnectionProfile>> GetProfilesAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Profiles);
        }

        public Task<ConnectionProfile?> GetSelectedProfileAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Selected);
        }

        public Task<ConnectionProfile> SelectAsync(
            Guid profileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Selected = Profiles.Single(item => item.Id == profileId);
            return Task.FromResult(Selected);
        }

        public Task<ConnectionProfile> SaveAndSelectAsync(
            ConnectionProfileDraft draft,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SavedDraft = draft;
            var now = DateTimeOffset.UtcNow;
            var saved = new ConnectionProfile
            {
                Id = draft.Id ?? Guid.NewGuid(),
                Name = draft.Name,
                Transport = draft.Transport,
                TcpHost = draft.TcpHost,
                TcpPort = draft.TcpPort,
                SerialPortName = draft.SerialPortName,
                BaudRate = draft.BaudRate,
                DtrEnable = draft.DtrEnable,
                RtsEnable = draft.RtsEnable,
                OpenDelayMilliseconds = draft.OpenDelayMilliseconds,
                CommandTimeoutMilliseconds = draft.CommandTimeoutMilliseconds,
                AcknowledgementTimeoutMilliseconds = draft.AcknowledgementTimeoutMilliseconds,
                AutoConnect = draft.AutoConnect,
                Reconnect = draft.Reconnect,
                CreatedUtc = now,
                UpdatedUtc = now,
            };
            Profiles = Profiles.Where(item => item.Id != saved.Id).Append(saved).ToArray();
            Selected = saved;
            return Task.FromResult(saved);
        }

        public Task<ConnectionProfile> UpdateExpectedNodePublicKeyAsync(
            Guid profileId,
            ReadOnlyMemory<byte> publicKey,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeSerialPortCatalog(IReadOnlyList<string> ports) : ISerialPortCatalog
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<string>> GetPortNamesAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(ports);
        }
    }
}
