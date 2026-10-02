using Microsoft.Extensions.Logging.Abstractions;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Platform;
using MeshCoreMessenger.Desktop.ViewModels;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed class ConnectionSettingsTests
{
    [Fact]
    public async Task SelectionSaveCancelAndPendingSelectionNeverConnect()
    {
        await using var workspace = await Workspace.CreateAsync();
        var vm = workspace.ViewModel;
        var second = vm.AvailableProfiles.Single(item => item.Id != vm.SavedProfile!.Id);
        vm.ProfileName = "Edited";
        Assert.True(vm.SaveCommand.CanExecute(null));
        Assert.False(vm.Control.CanConnect);
        vm.SelectedProfile = second;
        Assert.True(vm.HasPendingSelection);
        Assert.NotEqual(second.Id, vm.SavedProfile!.Id);
        vm.StayCommand.Execute(null);
        vm.CancelCommand.Execute(null);
        Assert.False(vm.IsDirty);
        vm.SelectedProfile = second;
        Assert.Equal(second.Id, vm.SavedProfile!.Id);
        vm.TcpPort = "65535";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.False(vm.IsDirty);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.Equal(65535, (await workspace.Manager.GetProfilesAsync(TestContext.Current.CancellationToken)).Single(item => item.Id == second.Id).TcpPort);
        Assert.Equal(workspace.First.Id, (await workspace.Manager.GetSelectedProfileAsync(TestContext.Current.CancellationToken))!.Id);
        Assert.Equal(0, workspace.Supervisor.ConnectCalls);
        Assert.Equal(0, workspace.Supervisor.DisconnectCalls);
        Assert.Equal(0, workspace.Supervisor.SwitchCalls);
    }

    [Fact]
    public async Task SavedChangesRemainUnappliedUntilSingleFlightReconnectCompletes()
    {
        await using var workspace = await Workspace.CreateAsync();
        var vm = workspace.ViewModel;
        workspace.Supervisor.Publish(ConnectionSupervisorState.Online, workspace.First);
        vm.TcpPort = "6001";
        Assert.False(vm.Control.CanConnect);
        Assert.Equal(5000, workspace.Supervisor.Snapshot.UsedProfile!.TcpPort);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.Control.HasUnappliedChanges);
        Assert.Equal("Переподключить", vm.Control.ConnectLabel);
        Assert.Equal(5000, workspace.Supervisor.Snapshot.UsedProfile!.TcpPort);
        workspace.Supervisor.DisconnectGate = new();
        var first = vm.Control.ConnectCommand.ExecuteAsync(null);
        var repeated = vm.Control.ConnectCommand.ExecuteAsync(null);
        Assert.Equal("Переподключение…", vm.Control.Status);
        Assert.Equal(1, workspace.Supervisor.DisconnectCalls);
        Assert.Equal(0, workspace.Supervisor.ConnectCalls);
        workspace.Supervisor.DisconnectGate.SetResult();
        await Task.WhenAll(first, repeated);
        Assert.Equal(1, workspace.Supervisor.ConnectCalls);
        Assert.Equal(1, workspace.Supervisor.SwitchCalls);
        Assert.Equal(6001, workspace.Supervisor.Snapshot.UsedProfile!.TcpPort);
        Assert.False(vm.Control.HasUnappliedChanges);
        Assert.False(vm.Control.IsBusy);
        Assert.Equal(1, workspace.Supervisor.MaxOperations);
    }

    [Fact]
    public async Task ClosingDuringReconnectCancelsWorkflowBeforeNewConnection()
    {
        await using var workspace = await Workspace.CreateAsync();
        workspace.Supervisor.Publish(ConnectionSupervisorState.Online, workspace.First);
        workspace.ViewModel.TcpPort = "6001";
        await workspace.ViewModel.SaveCommand.ExecuteAsync(null);
        workspace.Supervisor.DisconnectGate = new();
        var request = workspace.ViewModel.Control.ConnectCommand.ExecuteAsync(null);
        await workspace.ViewModel.StopAsync();
        await request;
        await workspace.ViewModel.Control.ConnectCommand.ExecuteAsync(null);
        await workspace.ViewModel.Control.DisconnectCommand.ExecuteAsync(null);
        Assert.Equal(0, workspace.Supervisor.ConnectCalls);
        Assert.False(workspace.ViewModel.Control.IsBusy);
        Assert.False(workspace.ViewModel.Control.CanConnect);
    }

    [Fact]
    public async Task DisconnectAtRetryWaitingDoesNotConnectAndImmediateRetryUsesExistingLoop()
    {
        await using var workspace = await Workspace.CreateAsync();
        workspace.Supervisor.Publish(ConnectionSupervisorState.RetryWaiting, workspace.First);
        Assert.Equal("Подключить сейчас", workspace.ViewModel.Control.ConnectLabel);
        await workspace.ViewModel.Control.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(1, workspace.Supervisor.ConnectCalls);
        Assert.Equal(0, workspace.Supervisor.DisconnectCalls);
        workspace.Supervisor.Publish(ConnectionSupervisorState.RetryWaiting, workspace.First);
        await workspace.ViewModel.Control.DisconnectCommand.ExecuteAsync(null);
        Assert.Equal(1, workspace.Supervisor.DisconnectCalls);
        Assert.Equal(1, workspace.Supervisor.ConnectCalls);
        Assert.Equal(ConnectionSupervisorState.Offline, workspace.Supervisor.Snapshot.State);
    }

    [Fact]
    public async Task DisconnectTimeoutDoesNotStartNewConnectionOrLeaveWorkflowBusy()
    {
        var clock = new ManualClock();
        await using var workspace = await Workspace.CreateAsync(clock);
        workspace.Supervisor.Publish(ConnectionSupervisorState.Online, workspace.First);
        workspace.ViewModel.TcpPort = "6001";
        await workspace.ViewModel.SaveCommand.ExecuteAsync(null);
        workspace.Supervisor.DisconnectGate = new();
        var request = workspace.ViewModel.Control.ConnectCommand.ExecuteAsync(null);
        clock.Expire();
        await request;
        Assert.False(workspace.ViewModel.Control.IsBusy);
        Assert.Contains("Новое подключение не начато", workspace.ViewModel.Control.ErrorMessage);
        Assert.Equal(0, workspace.Supervisor.ConnectCalls);
    }

    [Theory]
    [InlineData("", "5000", false)]
    [InlineData(" ", "5000", false)]
    [InlineData("localhost", "0", false)]
    [InlineData("localhost", "65536", false)]
    [InlineData("localhost", "abc", false)]
    [InlineData("localhost", "-1", false)]
    [InlineData("localhost", "1", true)]
    [InlineData("localhost", "65535", true)]
    public void ValidationHasInlineErrorsAndCorrectBoundaries(string host, string port, bool valid)
    {
        var editor = new ConnectionProfileEditorViewModel { ProfileName = "TCP", SelectedTransport = ConnectionTransportKind.Tcp, TcpHost = host, TcpPort = port };
        Assert.Equal(valid, editor.IsValid);
        if (!valid) Assert.True(editor.TcpHostError is not null || editor.TcpPortError is not null);
        if (valid) Assert.Equal(int.Parse(port), editor.CreateDraft().TcpPort);
    }

    [Fact]
    public void HiddenTransportFieldsDoNotBlockValidationOrParseInvalidCachedValues()
    {
        var editor = new ConnectionProfileEditorViewModel { ProfileName = "TCP", SerialPortName = "/dev/test", OpenDelayMilliseconds = "bad", BaudRate = "bad" };
        Assert.False(editor.IsValid);
        editor.SelectedTransport = ConnectionTransportKind.Tcp;
        Assert.True(editor.IsValid);
        Assert.NotNull(editor.CreateDraft().TcpPort);
        editor.SelectedTransport = ConnectionTransportKind.Serial;
        Assert.NotNull(editor.OpenDelayError);
        Assert.NotNull(editor.BaudRateError);
    }

    [Fact]
    public async Task PortRefreshFailurePreservesListAndUnavailableSavedPort()
    {
        await using var workspace = await Workspace.CreateAsync();
        var vm = workspace.ViewModel;
        vm.SelectedTransport = ConnectionTransportKind.Serial;
        vm.SerialPortName = "/dev/cu.fixture-absent";
        await vm.SaveCommand.ExecuteAsync(null);
        await vm.RefreshPortsCommand.ExecuteAsync(null);
        Assert.Contains(vm.SerialPortName, vm.AvailableSerialPorts);
        Assert.NotNull(vm.SerialPortNotice);
        var before = vm.AvailableSerialPorts.ToArray();
        workspace.Ports.Fail = true;
        await vm.RefreshPortsCommand.ExecuteAsync(null);
        Assert.Equal(before, vm.AvailableSerialPorts);
        Assert.Contains("Предыдущий список сохранён", vm.ErrorMessage);
        Assert.False(vm.IsBusy);
        Assert.Equal("/dev/cu.fixture-absent", vm.SerialPortName);
    }

    [Fact]
    public async Task OldGenerationAndCallbacksAfterStopCannotChangeStatus()
    {
        await using var workspace = await Workspace.CreateAsync();
        workspace.Supervisor.Publish(ConnectionSupervisorState.Online, workspace.First, 2);
        workspace.Supervisor.Publish(ConnectionSupervisorState.NeedsAttention, workspace.First, 1);
        Assert.True(workspace.ViewModel.Control.IsOnline);
        await workspace.ViewModel.StopAsync();
        workspace.Supervisor.Publish(ConnectionSupervisorState.NeedsAttention, workspace.First, 3);
        Assert.True(workspace.ViewModel.Control.IsOnline);
    }

    private sealed class Workspace : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "MeshCoreMessenger.ConnectionSettings.Tests", Guid.NewGuid().ToString("N"));
        private LocalStorage _storage = null!;
        public ConnectionProfileManager Manager { get; private set; } = null!;
        public ControlledSupervisor Supervisor { get; private set; } = null!;
        public ConnectionProfilesViewModel ViewModel { get; private set; } = null!;
        public ConnectionProfile First { get; private set; } = null!;
        public PortCatalog Ports { get; } = new();
        public static async Task<Workspace> CreateAsync(TimeProvider? clock = null)
        {
            var result = new Workspace();
            result._storage = await LocalStorage.OpenAsync(DesktopAppPaths.CreateForDirectory(result._directory));
            result.Manager = new(result._storage.ConnectionProfiles, result._storage.Settings, TimeProvider.System);
            result.First = await result.Manager.SaveAndSelectAsync(new() { Name = "First", Transport = ConnectionTransportKind.Tcp, TcpHost = "localhost", TcpPort = 5000 });
            await result.Manager.SaveAsync(new() { Name = "Second", Transport = ConnectionTransportKind.Tcp, TcpHost = "localhost", TcpPort = 6000 });
            result.Supervisor = new(result.Manager);
            result.ViewModel = new(result.Manager, result.Supervisor, result.Ports, NullLogger<ConnectionProfilesViewModel>.Instance, time: clock);
            await result.ViewModel.LoadAsync();
            return result;
        }
        public async ValueTask DisposeAsync()
        {
            await ViewModel.StopAsync(); await _storage.DisposeAsync(); Directory.Delete(_directory, true);
        }
    }
    private sealed class PortCatalog : ISerialPortCatalog
    {
        public bool Fail { get; set; }
        public Task<IReadOnlyList<string>> GetPortNamesAsync(CancellationToken cancellationToken = default) =>
            Fail ? Task.FromException<IReadOnlyList<string>>(new IOException("catalog failed")) : Task.FromResult<IReadOnlyList<string>>(["/dev/cu.detected"]);
    }
    private sealed class ControlledSupervisor(IConnectionProfileManager manager) : IConnectionSupervisor
    {
        public ConnectionSupervisorSnapshot Snapshot { get; private set; } = new(ConnectionSupervisorState.Offline, 0, null, null, null, null, null);
        public event EventHandler<ConnectionSupervisorStateChangedEventArgs>? StateChanged;
        public TaskCompletionSource? DisconnectGate { get; set; }
        public int DisconnectCalls { get; private set; }
        public int ConnectCalls { get; private set; }
        public int SwitchCalls { get; private set; }
        public int MaxOperations { get; private set; }
        private int _operations;
        public void Publish(ConnectionSupervisorState state, ConnectionProfile profile, long generation = 1)
        {
            var previous = Snapshot;
            Snapshot = new(state, generation, profile.Id, null, null, null, state == ConnectionSupervisorState.RetryWaiting ? DateTimeOffset.UtcNow.AddSeconds(15) : null) { UsedProfile = profile };
            StateChanged?.Invoke(this, new(previous, Snapshot));
        }
        public async Task ConnectNowAsync(CancellationToken cancellationToken = default)
        {
            ConnectCalls++; MaxOperations = Math.Max(MaxOperations, ++_operations);
            try { var profile = await manager.GetSelectedProfileAsync(cancellationToken); Publish(ConnectionSupervisorState.Online, profile!, Snapshot.Generation + 1); }
            finally { _operations--; }
        }
        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            DisconnectCalls++; MaxOperations = Math.Max(MaxOperations, ++_operations);
            try
            {
                if (DisconnectGate is { } gate) await gate.Task.WaitAsync(cancellationToken);
                Publish(ConnectionSupervisorState.Offline, Snapshot.UsedProfile!);
            }
            finally { _operations--; }
        }
        public async Task SwitchProfileAsync(Guid id, CancellationToken cancellationToken = default)
        {
            SwitchCalls++;
            await manager.SelectAsync(id, cancellationToken);
            await ConnectNowAsync(cancellationToken);
        }
        public Task StartAutoConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class ManualClock : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { _callback = callback; _state = state; return new TimerHandle(); }
        public void Expire() => _callback!(_state);
        private sealed class TimerHandle : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
