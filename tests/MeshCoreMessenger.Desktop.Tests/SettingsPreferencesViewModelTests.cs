using System.Collections.Concurrent;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.Preferences;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed partial class MainWindowViewModelTests
{
    [Fact]
    public async Task ExplicitPreferencesSaveBeforeExitWithoutConnectionCommands()
    {
        var settings = new PreferencesTestStore();
        var supervisor = new FakeConnectionSupervisor();
        var vm = CreateViewModel(new FakeHistoryReader(), settings: settings, supervisor: supervisor);
        await vm.LoadAsync(CancellationToken);
        vm.SelectedTheme = vm.ThemeOptions.Single(option => option.Value == DesktopThemePreference.Dark);
        vm.SelectedCloseBehavior = vm.CloseBehaviorOptions.Single(option => option.Value == DesktopCloseBehavior.ExitApplication);
        vm.NotifyPrivateMessages = false;
        vm.NotifyChannelMessages = false;
        await WaitUntilAsync(() => settings.Values.GetValueOrDefault(DesktopPreferences.NotifyChannelsSettingKey) == "False");
        var restored = new DesktopPreferences(settings);
        await restored.LoadAsync(CancellationToken);
        Assert.Equal(DesktopThemePreference.Dark, restored.Snapshot.Theme);
        Assert.Equal(DesktopCloseBehavior.ExitApplication, restored.Snapshot.CloseBehavior);
        Assert.False(restored.Snapshot.NotifyPrivateMessages);
        Assert.False(restored.Snapshot.NotifyChannelMessages);
        Assert.False(vm.HasPreferencesSaveError);
        Assert.Equal(0, supervisor.ConnectCalls);
        Assert.Equal(0, supervisor.DisconnectCalls);
        await vm.StopAsync();
    }

    [Fact]
    public async Task PreferencesFailureAndExplicitRetryDoNotEraseAnUnrelatedError()
    {
        var settings = new PreferencesTestStore();
        var vm = CreateViewModel(new FakeHistoryReader(), settings: settings);
        await vm.LoadAsync(CancellationToken);
        await ((IDesktopUiLifetime)vm).ReportShutdownFailureAsync(new IOException("unrelated failure"));
        var originalError = vm.ErrorMessage;
        settings.BeforeWrite = (_, _) => Task.FromException(new IOException("settings failure"));
        vm.NotifyPrivateMessages = false;
        await WaitUntilAsync(() => vm.HasPreferencesSaveError);
        Assert.Equal(originalError, vm.ErrorMessage);
        Assert.False(vm.NotifyPrivateMessages);
        Assert.True(vm.RetryPreferencesSaveCommand.CanExecute(null));
        settings.BeforeWrite = null;
        await vm.RetryPreferencesSaveCommand.ExecuteAsync(null);
        Assert.False(vm.HasPreferencesSaveError);
        Assert.Equal(originalError, vm.ErrorMessage);
        Assert.Equal("False", settings.Values[DesktopPreferences.NotifyPrivateSettingKey]);
        await vm.StopAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OlderUiCompletionReflectsCurrentFailureOrRecovery(bool firstFails)
    {
        var settings = new PreferencesTestStore();
        var dispatcher = new ReorderingPreferencesDispatcher();
        var vm = CreateViewModel(new FakeHistoryReader(), settings: settings, dispatcher: dispatcher);
        await vm.LoadAsync(CancellationToken);
        dispatcher.QueueCallbacks = true;
        var fail = firstFails;
        settings.BeforeWrite = (_, _) => fail ? Task.FromException(new IOException("settings failure")) : Task.CompletedTask;
        vm.SelectedTheme = vm.ThemeOptions.Single(option => option.Value == DesktopThemePreference.Dark);
        await WaitUntilAsync(() => dispatcher.PendingCount == 1);
        fail = !firstFails;
        vm.NotifyPrivateMessages = false;
        await WaitUntilAsync(() => dispatcher.PendingCount == 2);
        dispatcher.RunAt(1);
        dispatcher.RunAt(0);
        Assert.Equal(!firstFails, vm.HasPreferencesSaveError);
        dispatcher.QueueCallbacks = false;
        await vm.StopAsync();
    }

    [Fact]
    public async Task StopCancelsBackgroundWriteButFinalPreferencesBarrierRetainsChanges()
    {
        var settings = new PreferencesTestStore();
        var preferences = new DesktopPreferences(settings);
        var vm = CreateViewModel(new FakeHistoryReader(), settings: settings, preferences: preferences);
        await vm.LoadAsync(CancellationToken);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        settings.BeforeWrite = async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        vm.NotifyChannelMessages = false;
        await started.Task.WaitAsync(CancellationToken);
        await vm.StopAsync().WaitAsync(TimeSpan.FromSeconds(3), CancellationToken);
        vm.NotifyChannelMessages = true;
        Assert.False(vm.NotifyChannelMessages); // Quiesced UI cannot change the accepted choice.
        settings.BeforeWrite = null;
        await preferences.FlushAsync(CancellationToken);
        Assert.Equal("False", settings.Values[DesktopPreferences.NotifyChannelsSettingKey]);
    }

    private sealed class PreferencesTestStore : ISettingsStore
    {
        public ConcurrentDictionary<string, string> Values { get; } = new();
        public Func<string, CancellationToken, Task>? BeforeWrite { get; set; }
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(Values.GetValueOrDefault(key));
        public async Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BeforeWrite is { } before) await before(key, cancellationToken);
            Values[key] = value;
        }
    }

    private sealed class ReorderingPreferencesDispatcher : IUiDispatcher
    {
        private readonly List<(Action Action, TaskCompletionSource Done, CancellationToken Token)> _queue = [];
        public bool QueueCallbacks { get; set; }
        public int PendingCount { get { lock (_queue) return _queue.Count; } }
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            if (!QueueCallbacks) { cancellationToken.ThrowIfCancellationRequested(); action(); return Task.CompletedTask; }
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_queue) _queue.Add((action, done, cancellationToken));
            return done.Task;
        }
        public void RunAt(int index)
        {
            (Action Action, TaskCompletionSource Done, CancellationToken Token) item;
            lock (_queue) { item = _queue[index]; _queue.RemoveAt(index); }
            if (item.Token.IsCancellationRequested) { item.Done.SetCanceled(item.Token); return; }
            item.Action();
            item.Done.SetResult();
        }
    }
}
