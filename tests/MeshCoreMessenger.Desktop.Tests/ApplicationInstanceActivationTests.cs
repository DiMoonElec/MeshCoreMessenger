using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.Platform;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed class ApplicationInstanceActivationTests
{
    [Fact]
    public async Task OwnerPublishesEndpointBeforeUiAndReleasesLockAfterEndpointCleanup()
    {
        using var folder = new TemporaryDirectory();
        using var activation = new DesktopActivationCoordinator(new ImmediateUiDispatcher());
        using (var owner = await ApplicationInstanceCoordinator.AcquireOrActivateAsync(folder.Paths, activation, cancellationToken: Token))
        {
            Assert.NotNull(owner);
            Assert.True(File.Exists(folder.Descriptor));
            Assert.False(File.Exists(folder.Paths.DatabasePath));
            Assert.Throws<ApplicationInstanceAlreadyRunningException>(() => ApplicationInstanceLock.Acquire(folder.Paths));
        }
        Assert.False(File.Exists(folder.Descriptor));
        using var next = ApplicationInstanceLock.Acquire(folder.Paths);
    }

    [Fact]
    public async Task RealSecondProcessExitsSuccessfullyWithoutOpeningSqliteOrUi()
    {
        using var folder = new TemporaryDirectory();
        using var activation = new DesktopActivationCoordinator(new ImmediateUiDispatcher());
        var calls = 0;
        activation.Attach(() => { Interlocked.Increment(ref calls); return true; });
        using var owner = await ApplicationInstanceCoordinator.AcquireOrActivateAsync(folder.Paths, activation, cancellationToken: Token);
        using var child = StartSecondProcess(folder.Paths.DataDirectory);
        await child.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(15), Token);
        Assert.Equal(0, child.ExitCode);
        Assert.Equal(1, calls);
        Assert.False(File.Exists(folder.Paths.DatabasePath));
        Assert.Throws<ApplicationInstanceAlreadyRunningException>(() => ApplicationInstanceLock.Acquire(folder.Paths));
    }

    [Fact]
    public async Task RapidSecondProcessesAllReachTheExistingOwner()
    {
        using var folder = new TemporaryDirectory();
        using var activation = new DesktopActivationCoordinator(new ImmediateUiDispatcher());
        var calls = 0;
        activation.Attach(() => { Interlocked.Increment(ref calls); return true; });
        using var owner = await ApplicationInstanceCoordinator.AcquireOrActivateAsync(folder.Paths, activation, cancellationToken: Token);
        var children = Enumerable.Range(0, 4).Select(_ => StartSecondProcess(folder.Paths.DataDirectory)).ToArray();
        try
        {
            await Task.WhenAll(children.Select(child => child.WaitForExitAsync(Token))).WaitAsync(TimeSpan.FromSeconds(15), Token);
            Assert.All(children, child => Assert.Equal(0, child.ExitCode));
            Assert.InRange(calls, 1, 4); // Concurrent UI requests may be coalesced.
            Assert.False(File.Exists(folder.Paths.DatabasePath));
        }
        finally
        {
            foreach (var child in children)
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                child.Dispose();
            }
        }
    }

    [Fact]
    public async Task UiFailureDoesNotDisableLaterActivation()
    {
        using var folder = new TemporaryDirectory();
        using var primary = new DesktopActivationCoordinator(new ImmediateUiDispatcher());
        using var secondary = new DesktopActivationCoordinator(new ImmediateUiDispatcher());
        var calls = 0;
        primary.Attach(() =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("Synthetic UI activation failure");
            return true;
        });
        using var owner = await ApplicationInstanceCoordinator.AcquireOrActivateAsync(folder.Paths, primary, cancellationToken: Token);
        Assert.Null(await ApplicationInstanceCoordinator.AcquireOrActivateAsync(folder.Paths, secondary, cancellationToken: Token));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task OversizedDescriptorAndCallerCancellationLeaveTheLockIntact()
    {
        using var folder = new TemporaryDirectory();
        using var held = ApplicationInstanceLock.Acquire(folder.Paths);
        await File.WriteAllTextAsync(folder.Descriptor, new string('x', 2048), Token);
        using var activation = new DesktopActivationCoordinator(new ImmediateUiDispatcher());
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var request = ApplicationInstanceCoordinator.AcquireOrActivateAsync(folder.Paths, activation, cancellationToken: cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Throws<ApplicationInstanceAlreadyRunningException>(() => ApplicationInstanceLock.Acquire(folder.Paths));
    }

    [Fact]
    public async Task EarlyRequestsCoalesceAndOneCanceledCallerDoesNotCancelOthers()
    {
        var dispatcher = new QueuedUiDispatcher();
        using var activation = new DesktopActivationCoordinator(dispatcher);
        using var canceled = new CancellationTokenSource();
        var first = activation.RequestAsync(canceled.Token);
        var second = activation.RequestAsync(Token);
        Assert.False(second.IsCompleted);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var calls = 0;
        activation.Attach(() => { calls++; return true; });
        await WaitUntilAsync(() => dispatcher.PendingCount == 1);
        Assert.False(second.IsCompleted);
        dispatcher.RunNext();
        Assert.True(await second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task EarlyIpcWaitsForUiRatherThanAcknowledgingStartup()
    {
        using var folder = new TemporaryDirectory();
        using var primary = new DesktopActivationCoordinator(new ImmediateUiDispatcher());
        using var secondary = new DesktopActivationCoordinator(new ImmediateUiDispatcher());
        using var owner = await ApplicationInstanceCoordinator.AcquireOrActivateAsync(folder.Paths, primary, cancellationToken: Token);
        var request = ApplicationInstanceCoordinator.AcquireOrActivateAsync(folder.Paths, secondary, cancellationToken: Token);
        // Exercise a wait longer than the protocol handshake budget.
        await Task.Delay(1100, Token);
        Assert.False(request.IsCompleted);
        primary.Attach(() => true);
        Assert.Null(await request.WaitAsync(TimeSpan.FromSeconds(3), Token));
    }

    [Fact]
    public async Task DisposeCancelsRequestsBeforeWindowReady()
    {
        using var activation = new DesktopActivationCoordinator(new ImmediateUiDispatcher());
        var request = activation.RequestAsync(Token);
        activation.Dispose();
        Assert.False(await request.WaitAsync(TimeSpan.FromSeconds(1), Token));
        Assert.False(await activation.RequestAsync(Token));
    }

    [Fact]
    public async Task QueuedCallbackDoesNotShowWindowAfterProcessExit()
    {
        var dispatcher = new QueuedUiDispatcher();
        using var activation = new DesktopActivationCoordinator(dispatcher);
        var calls = 0;
        activation.Attach(() => { calls++; return true; });
        var request = activation.RequestAsync(Token);
        await WaitUntilAsync(() => dispatcher.PendingCount == 1);
        activation.Dispose();
        dispatcher.RunNext();
        Assert.False(await request);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ClosingWindowRejectsActivationButFailedShutdownCanAcceptNextRequest()
    {
        using var folder = new TemporaryDirectory();
        using var primary = new DesktopActivationCoordinator(new ImmediateUiDispatcher());
        using var secondary = new DesktopActivationCoordinator(new ImmediateUiDispatcher());
        var closing = true;
        primary.Attach(() => !closing);
        using var owner = await ApplicationInstanceCoordinator.AcquireOrActivateAsync(folder.Paths, primary, cancellationToken: Token);
        await Assert.ThrowsAsync<ApplicationActivationException>(() =>
            ApplicationInstanceCoordinator.AcquireOrActivateAsync(folder.Paths, secondary, cancellationToken: Token));
        closing = false;
        Assert.Null(await ApplicationInstanceCoordinator.AcquireOrActivateAsync(folder.Paths, secondary, cancellationToken: Token));
    }

    [Fact]
    public async Task StaleOrInvalidDescriptorNeverOverridesHeldLock()
    {
        using var folder = new TemporaryDirectory();
        using var held = ApplicationInstanceLock.Acquire(folder.Paths);
        await File.WriteAllTextAsync(folder.Descriptor, "{invalid", Token);
        using var activation = new DesktopActivationCoordinator(new ImmediateUiDispatcher());
        await Assert.ThrowsAsync<ApplicationActivationException>(() =>
            ApplicationInstanceCoordinator.AcquireOrActivateAsync(folder.Paths, activation, TimeSpan.FromMilliseconds(150), cancellationToken: Token));
        Assert.False(File.Exists(folder.Paths.DatabasePath));
        Assert.Throws<ApplicationInstanceAlreadyRunningException>(() => ApplicationInstanceLock.Acquire(folder.Paths));
    }

    [Fact]
    public async Task StaleDescriptorIsReplacedWhenLockIsAvailable()
    {
        using var folder = new TemporaryDirectory();
        await File.WriteAllTextAsync(folder.Descriptor, "stale crash descriptor", Token);
        using var activation = new DesktopActivationCoordinator(new ImmediateUiDispatcher());
        using var owner = await ApplicationInstanceCoordinator.AcquireOrActivateAsync(folder.Paths, activation, cancellationToken: Token);
        Assert.NotNull(owner);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(folder.Descriptor, Token));
        Assert.Equal(Environment.ProcessId, json.RootElement.GetProperty("ProcessId").GetInt32());
    }

    [Fact]
    public async Task LockReleasedDuringRetryAllowsNewOwner()
    {
        using var folder = new TemporaryDirectory();
        var held = ApplicationInstanceLock.Acquire(folder.Paths);
        using var activation = new DesktopActivationCoordinator(new ImmediateUiDispatcher());
        var request = ApplicationInstanceCoordinator.AcquireOrActivateAsync(folder.Paths, activation, cancellationToken: Token);
        Assert.False(request.IsCompleted);
        held.Dispose();
        using var owner = await request.WaitAsync(TimeSpan.FromSeconds(3), Token);
        Assert.NotNull(owner);
    }

    [Fact]
    public async Task DifferentDataDirectoriesHaveIndependentEndpoints()
    {
        using var first = new TemporaryDirectory();
        using var second = new TemporaryDirectory();
        using var activationA = new DesktopActivationCoordinator(new ImmediateUiDispatcher());
        using var activationB = new DesktopActivationCoordinator(new ImmediateUiDispatcher());
        using var client = new DesktopActivationCoordinator(new ImmediateUiDispatcher());
        var callsA = 0;
        var callsB = 0;
        activationA.Attach(() => { callsA++; return true; });
        activationB.Attach(() => { callsB++; return true; });
        using var ownerA = await ApplicationInstanceCoordinator.AcquireOrActivateAsync(first.Paths, activationA, cancellationToken: Token);
        using var ownerB = await ApplicationInstanceCoordinator.AcquireOrActivateAsync(second.Paths, activationB, cancellationToken: Token);
        Assert.Null(await ApplicationInstanceCoordinator.AcquireOrActivateAsync(second.Paths, client, cancellationToken: Token));
        Assert.Equal(0, callsA);
        Assert.Equal(1, callsB);
    }

    [Fact]
    public async Task PathAliasesActivateTheSameOwner()
    {
        using var folder = new TemporaryDirectory();
        using var primary = new DesktopActivationCoordinator(new ImmediateUiDispatcher());
        using var client = new DesktopActivationCoordinator(new ImmediateUiDispatcher());
        primary.Attach(() => true);
        using var owner = await ApplicationInstanceCoordinator.AcquireOrActivateAsync(folder.Paths, primary, cancellationToken: Token);
        var normalized = DesktopAppPaths.CreateForDirectory(Path.Combine(folder.Paths.DataDirectory, "unused", ".."));
        Assert.Null(await ApplicationInstanceCoordinator.AcquireOrActivateAsync(normalized, client, cancellationToken: Token));
        if (!OperatingSystem.IsWindows())
        {
            var alias = folder.Paths.DataDirectory + "-alias";
            Directory.CreateSymbolicLink(alias, folder.Paths.DataDirectory);
            try
            {
                Assert.Null(await ApplicationInstanceCoordinator.AcquireOrActivateAsync(DesktopAppPaths.CreateForDirectory(alias), client, cancellationToken: Token));
            }
            finally { Directory.Delete(alias); }
        }
        var caseAlias = folder.Paths.DataDirectory.ToUpperInvariant();
        if (Directory.Exists(caseAlias))
            Assert.Null(await ApplicationInstanceCoordinator.AcquireOrActivateAsync(DesktopAppPaths.CreateForDirectory(caseAlias), client, cancellationToken: Token));
    }

    [Fact]
    public async Task MalformedAndDisconnectedClientsDoNotStopTheListener()
    {
        using var folder = new TemporaryDirectory();
        using var primary = new DesktopActivationCoordinator(new ImmediateUiDispatcher());
        using var secondary = new DesktopActivationCoordinator(new ImmediateUiDispatcher());
        primary.Attach(() => true);
        using var owner = await ApplicationInstanceCoordinator.AcquireOrActivateAsync(folder.Paths, primary, cancellationToken: Token);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(folder.Descriptor, Token));
        var pipeName = json.RootElement.GetProperty("PipeName").GetString()!;
        using (var invalid = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await invalid.ConnectAsync(1000, Token);
            await invalid.ReadExactlyAsync(new byte[21], Token);
            await invalid.WriteAsync(new byte[] { 99 }, Token);
        }
        Assert.Null(await ApplicationInstanceCoordinator.AcquireOrActivateAsync(folder.Paths, secondary, cancellationToken: Token));
    }

    [Fact]
    public async Task SimultaneousStartsElectOneOwnerAndActivateIt()
    {
        using var folder = new TemporaryDirectory();
        using var a = new DesktopActivationCoordinator(new ImmediateUiDispatcher());
        using var b = new DesktopActivationCoordinator(new ImmediateUiDispatcher());
        a.Attach(() => true);
        b.Attach(() => true);
        var results = await Task.WhenAll(
            Task.Run(() => ApplicationInstanceCoordinator.AcquireOrActivateAsync(folder.Paths, a, cancellationToken: Token)),
            Task.Run(() => ApplicationInstanceCoordinator.AcquireOrActivateAsync(folder.Paths, b, cancellationToken: Token)));
        try
        {
            Assert.Single(results, result => result is not null);
            Assert.Single(results, result => result is null);
        }
        finally { foreach (var owner in results) owner?.Dispose(); }
    }

    internal static Process StartSecondProcess(string directory)
    {
        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(typeof(App).Assembly.Location);
        info.ArgumentList.Add("--data-dir");
        info.ArgumentList.Add(directory);
        return Process.Start(info)!;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Paths = DesktopAppPaths.CreateForDirectory(Path.Combine(Path.GetTempPath(), "mcm-activation-tests", Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(Paths.DataDirectory);
        }
        public DesktopAppPaths Paths { get; }
        public string Descriptor => Path.Combine(Paths.DataDirectory, ApplicationInstanceCoordinator.DescriptorFileName);
        public void Dispose() => Directory.Delete(Paths.DataDirectory, recursive: true);
    }
}
