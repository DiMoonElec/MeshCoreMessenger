using MeshCoreMessenger.Desktop.Notifications;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.Platform;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed class NativeNotificationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public void RegistryRoundTripsOpaqueScopedTargetAndRejectsExpiredOrInvalidToken()
    {
        using var folder = new Folder();
        var time = new Clock(); var registry = new NotificationTargetRegistry(folder.Registry, time);
        var target = new MessageNotificationTarget(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var token = registry.Add(folder.Paths.DataDirectory, target);
        Assert.True(Guid.TryParseExact(token, "N", out _));
        Assert.Equal(target, registry.Get(token)!.Target);
        Assert.Equal(folder.Paths.DataDirectory, registry.Get(token)!.DataDirectory);
        Assert.Null(registry.Get("../../other"));
        Assert.Null(registry.Get(Guid.NewGuid().ToString("N")));
        time.Now = time.Now.AddDays(3); Assert.Null(registry.Get(token));
        registry.Remove(token); Assert.Empty(Directory.GetFiles(folder.Registry));
    }
    [Fact]
    public void RegistryIsBoundedAndRejectsPartialIdentityAndCorruptedRecords()
    {
        using var folder = new Folder(); var registry = new NotificationTargetRegistry(folder.Registry, new Clock());
        for (var i = 0; i < 140; i++) registry.Add(folder.Paths.DataDirectory, new OpenApplicationTarget());
        Assert.Equal(NotificationTargetRegistry.Capacity, Directory.GetFiles(folder.Registry).Length);
        var token = registry.Add(folder.Paths.DataDirectory, new MessageNotificationTarget(Guid.Empty, Guid.NewGuid(), Guid.NewGuid()));
        Assert.Null(registry.Get(token));
        File.WriteAllText(Path.Combine(folder.Registry, token + ".json"), "{broken"); Assert.Null(registry.Get(token));
    }
    [Fact]
    public async Task AdapterReplacesSameGroupAndCleansTargetsEvenIfNativeRemovalFails()
    {
        using var folder = new Folder(); var registry = new NotificationTargetRegistry(folder.Registry, new Clock());
        var platform = new Platform(); await using var adapter = new NativeNotificationAdapter(platform, registry, folder.Paths);
        var request = new NotificationRequest("Title", "Private preview", new OpenApplicationTarget(), "group");
        await adapter.ShowAsync(request, Token); var first = platform.Shown.Single().CoalescingKey!;
        Assert.DoesNotContain("Private preview", File.ReadAllText(Path.Combine(folder.Registry, first + ".json")));
        platform.ThrowRemove = true;
        await adapter.ShowAsync(request, Token);
        Assert.Null(registry.Get(first)); Assert.Equal(2, platform.Shown.Count);
        await adapter.StopAsync(); Assert.Empty(Directory.GetFiles(folder.Registry)); Assert.True(platform.Disposed);
    }
    [Fact]
    public async Task UnavailableOrCancelledDeliveryNeverLeavesActivationRecord()
    {
        using var folder = new Folder(); var registry = new NotificationTargetRegistry(folder.Registry, new Clock());
        var platform = new Platform { Status = NotificationPlatformStatus.Denied };
        await using var adapter = new NativeNotificationAdapter(platform, registry, folder.Paths);
        var request = new NotificationRequest("Title", "Body", new OpenApplicationTarget());
        await adapter.ShowAsync(request, Token); Assert.Empty(platform.Shown); Assert.False(Directory.Exists(folder.Registry));
        platform.Status = NotificationPlatformStatus.Enabled; platform.ThrowShow = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ShowAsync(request, Token));
        Assert.Empty(Directory.GetFiles(folder.Registry));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.ShowAsync(request, cancelled.Token));
        Assert.Empty(Directory.GetFiles(folder.Registry));
    }
    [Fact]
    public async Task NotificationActivationWaitsForUiAndNavigationAndPreservesTypedScope()
    {
        using var activation = new DesktopActivationCoordinator(new ImmediateUiDispatcher());
        var target = new MessageNotificationTarget(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var request = activation.RequestNotificationAsync(target, Token); Assert.False(request.IsCompleted);
        activation.Attach(() => true); Assert.False(request.IsCompleted);
        NotificationTarget? received = null;
        activation.AttachNavigation((value, _) => { received = value; return Task.CompletedTask; });
        Assert.True(await request); Assert.Equal(target, received);
        activation.Dispose(); Assert.False(await activation.RequestNotificationAsync(target, Token));
    }
    [Fact]
    public async Task MessageTargetTraversesSingleInstancePipeWithoutOpeningDatabase()
    {
        using var folder = new Folder();
        using var primary = new DesktopActivationCoordinator(new ImmediateUiDispatcher());
        using var secondary = new DesktopActivationCoordinator(new ImmediateUiDispatcher());
        var target = new MessageNotificationTarget(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        NotificationTarget? received = null; primary.Attach(() => true);
        primary.AttachNavigation((value, _) => { received = value; return Task.CompletedTask; });
        using var owner = await ApplicationInstanceCoordinator.AcquireOrActivateAsync(folder.Paths, primary, cancellationToken: Token);
        Assert.Null(await ApplicationInstanceCoordinator.AcquireOrActivateAsync(folder.Paths, secondary, cancellationToken: Token, notificationTarget: target));
        Assert.Equal(target, received); Assert.False(File.Exists(folder.Paths.DatabasePath));
    }
    [Fact]
    public async Task ClickForOtherDataDirectoryActivatesItsOwnerWithoutCreatingDatabase()
    {
        using var current = new Folder(); using var other = new Folder();
        using var primary = new DesktopActivationCoordinator(new ImmediateUiDispatcher());
        using var local = new DesktopActivationCoordinator(new ImmediateUiDispatcher());
        primary.Attach(() => true); local.Attach(() => throw new InvalidOperationException("Wrong window activated."));
        var received = new TaskCompletionSource<NotificationTarget>(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.AttachNavigation((value, _) => { received.TrySetResult(value); return Task.CompletedTask; });
        using var owner = await ApplicationInstanceCoordinator.AcquireOrActivateAsync(other.Paths, primary, cancellationToken: Token);
        var registry = new NotificationTargetRegistry(current.Registry, new Clock()); var platform = new Platform();
        var target = new MessageNotificationTarget(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var token = registry.Add(other.Paths.DataDirectory, target);
        await using var clicks = new NotificationClickController(platform, registry, current.Paths, local, new ImmediateUiDispatcher(), NullLogger<NotificationClickController>.Instance);
        clicks.Start(); await Task.Run(() => platform.Click(token), Token);
        Assert.Equal(target, await received.Task.WaitAsync(TimeSpan.FromSeconds(5), Token));
        await clicks.StopAsync(); Assert.False(File.Exists(other.Paths.DatabasePath)); Assert.False(File.Exists(current.Paths.DatabasePath));
    }
    [Fact]
    public async Task ClickStopCancelsNavigationWaitingForFirstWindow()
    {
        using var folder = new Folder(); using var activation = new DesktopActivationCoordinator(new ImmediateUiDispatcher());
        var platform = new Platform(); var registry = new NotificationTargetRegistry(folder.Registry, new Clock());
        var token = registry.Add(folder.Paths.DataDirectory, new MessageNotificationTarget(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        await using var clicks = new NotificationClickController(platform, registry, folder.Paths, activation, new ImmediateUiDispatcher(), NullLogger<NotificationClickController>.Instance);
        clicks.Start(); platform.Click(token);
        await clicks.StopAsync().WaitAsync(TimeSpan.FromSeconds(2), Token);
        platform.Click(token); // Late native events have no subscriber after stop.
    }
    [Fact]
    public async Task PermissionFailureIsLocalAndDeniedStateNeverRepeatsPrompt()
    {
        var platform = new Platform { Status = NotificationPlatformStatus.PermissionRequired, ThrowPermission = true };
        var settings = new NotificationSettingsViewModel(platform, new ImmediateUiDispatcher());
        Assert.True(settings.CanRequestPermission);
        await settings.RequestPermissionCommand.ExecuteAsync(null);
        Assert.Contains("недоступны", settings.StatusText);
        platform.Status = NotificationPlatformStatus.Denied; platform.ChangeStatus();
        Assert.False(settings.CanRequestPermission); Assert.Contains("запрещены", settings.StatusText);
        settings.Stop(); Assert.False(settings.CanRequestPermission);
    }
    [Fact]
    public void PlatformInitializationFailureExposesSafeErrorCodeWithoutExceptionText()
    {
        var platform = new Platform { Status = NotificationPlatformStatus.Unavailable,
            Failure = new("Register", "COMException", unchecked((int)0x8007007E)) };
        var settings = new NotificationSettingsViewModel(platform, new ImmediateUiDispatcher());
        Assert.Contains("0x8007007E", settings.StatusText);
        Assert.Contains("Register", settings.StatusText);
        Assert.Contains("COMException", settings.StatusText);
        settings.Stop();
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Folder : IDisposable
    {
        public DesktopAppPaths Paths { get; } = DesktopAppPaths.CreateForDirectory(Path.Combine(Path.GetTempPath(), "mcm-notification-" + Guid.NewGuid().ToString("N")));
        public string Registry => Path.Combine(Paths.DataDirectory, "targets");
        public void Dispose() { if (Directory.Exists(Paths.DataDirectory)) Directory.Delete(Paths.DataDirectory, true); }
    }
    private sealed class Platform : IDesktopNotificationPlatform
    {
        public NotificationPlatformStatus Status { get; set; } = NotificationPlatformStatus.Enabled;
        public event EventHandler? StatusChanged;
        public event EventHandler<string>? Activated;
        public void Click(string token) => Activated?.Invoke(this, token);
        public void ChangeStatus() => StatusChanged?.Invoke(this, EventArgs.Empty);
        public NotificationPlatformFailure? Failure { get; set; }
        public List<NotificationRequest> Shown { get; } = [];
        public bool ThrowRemove, ThrowShow, ThrowPermission, Disposed;
        public Task InitializeAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task RequestPermissionAsync(CancellationToken token) { if (ThrowPermission) throw new InvalidOperationException(); return Task.CompletedTask; }
        public Task<string?> GetStartupTokenAsync(string[] args, CancellationToken token = default) => Task.FromResult<string?>(null);
        public Task ShowAsync(NotificationRequest request, CancellationToken token)
        { if (ThrowShow) throw new InvalidOperationException(); Shown.Add(request); return Task.CompletedTask; }
        public Task RemoveAsync(string identifier) { if (ThrowRemove) throw new IOException(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
