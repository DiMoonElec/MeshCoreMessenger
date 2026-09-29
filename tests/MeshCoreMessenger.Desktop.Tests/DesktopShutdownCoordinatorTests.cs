using Microsoft.Extensions.Logging.Abstractions;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.Platform;
using MeshCoreSharp.Models;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed class DesktopShutdownCoordinatorTests
{
    [Fact]
    public async Task SuccessfulShutdownUsesExactQuiesceThenFlushOrder()
    {
        var order = new List<string>();
        var ui = new FakeUi(order);
        var connections = new FakeConnections(order);
        var ingress = new FakeIngress(order);
        var coordinator = CreateCoordinator(ui, connections, ingress, new FakeSessionCompletions(order));

        await coordinator.ShutdownAsync(CancellationToken);
        await coordinator.ShutdownAsync(CancellationToken);

        Assert.True(coordinator.IsCompleted);
        Assert.Equal(["ui", "connections", "flush", "session-flush"], order);
        Assert.Equal(1, ui.StopCount);
        Assert.Equal(1, connections.ShutdownCount);
        Assert.Equal(1, ingress.FlushCount);
    }

    [Fact]
    public async Task ConcurrentShutdownRequestsShareOneAttempt()
    {
        var order = new List<string>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ingress = new FakeIngress(order) { FlushGate = gate };
        var coordinator = CreateCoordinator(
            new FakeUi(order),
            new FakeConnections(order),
            ingress,
            new FakeSessionCompletions(order));

        var first = coordinator.ShutdownAsync(CancellationToken);
        var second = coordinator.ShutdownAsync(CancellationToken);
        await ingress.FlushStarted.Task.WaitAsync(CancellationToken);

        Assert.Equal(1, ingress.FlushCount);
        gate.SetResult();
        await Task.WhenAll(first, second);
        Assert.True(coordinator.IsCompleted);
    }

    [Fact]
    public async Task PersistenceFailureCancelsExitAndExplicitRetryKeepsPendingWriter()
    {
        var order = new List<string>();
        var ui = new FakeUi(order);
        var connections = new FakeConnections(order);
        var ingress = new FakeIngress(order);
        ingress.Failures.Enqueue(new IOException("disk full"));
        var coordinator = CreateCoordinator(ui, connections, ingress, new FakeSessionCompletions(order));

        var failure = await Assert.ThrowsAsync<DesktopShutdownException>(
            () => coordinator.ShutdownAsync(CancellationToken));

        Assert.IsType<ReceiveIngestException>(failure.InnerException);
        Assert.False(coordinator.IsCompleted);
        Assert.True(ingress.IsPaused);
        Assert.Equal(1, ingress.PendingMessageCount);
        Assert.Equal(["ui", "connections", "flush", "report"], order);

        await coordinator.ShutdownAsync(CancellationToken);

        Assert.True(coordinator.IsCompleted);
        Assert.False(ingress.IsPaused);
        Assert.Equal(0, ingress.PendingMessageCount);
        Assert.Equal(
            ["ui", "connections", "flush", "report", "retry", "flush", "session-flush"],
            order);
        Assert.Equal(1, ui.StopCount);
        Assert.Equal(1, connections.ShutdownCount);
        Assert.Equal(2, ingress.FlushCount);
        Assert.Equal(1, ingress.RetryCount);
    }

    [Fact]
    public async Task FailedSessionEndIsRetriedWithoutRepeatingQuiesce()
    {
        var order = new List<string>();
        var ui = new FakeUi(order);
        var connections = new FakeConnections(order);
        var ingress = new FakeIngress(order);
        var sessions = new FakeSessionCompletions(order) { PendingCount = 1 };
        sessions.Failures.Enqueue(new IOException("session write failed"));
        var coordinator = CreateCoordinator(ui, connections, ingress, sessions);

        await Assert.ThrowsAsync<DesktopShutdownException>(
            () => coordinator.ShutdownAsync(CancellationToken));

        Assert.False(coordinator.IsCompleted);
        Assert.True(sessions.IsPaused);
        Assert.Equal(1, sessions.PendingCount);
        Assert.Equal(
            ["ui", "connections", "flush", "session-flush", "report"],
            order);

        await coordinator.ShutdownAsync(CancellationToken);

        Assert.True(coordinator.IsCompleted);
        Assert.Equal(1, ui.StopCount);
        Assert.Equal(1, connections.ShutdownCount);
        Assert.Equal(
            [
                "ui", "connections", "flush", "session-flush", "report",
                "session-retry", "flush", "session-flush",
            ],
            order);
    }

    [Fact]
    public async Task FailedShutdownKeepsStorageAndInstanceLockAliveUntilSuccessfulRetry()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new TestPaths(temporary.Path);
        var instanceLock = ApplicationInstanceLock.Acquire(paths);
        var storage = await LocalStorage.OpenAsync(paths, CancellationToken);
        var now = DateTimeOffset.UtcNow;
        var profile = new ConnectionProfile
        {
            Id = Guid.NewGuid(),
            Name = "Shutdown test",
            Transport = ConnectionTransportKind.Tcp,
            TcpHost = "127.0.0.1",
            TcpPort = 5000,
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        await storage.ConnectionProfiles.SaveAsync(profile, CancellationToken);
        var node = await storage.Nodes.FindOrCreateAsync(
            Enumerable.Range(1, 32).Select(value => (byte)value).ToArray(),
            "Node",
            now,
            CancellationToken);
        var session = new SessionRecord(Guid.NewGuid(), profile.Id, node.Id, now, null, null);
        await storage.Sessions.CreateAsync(session, CancellationToken);
        await storage.IncomingMessages.StoreAsync(
            new IncomingMessageEnvelope(
                Guid.NewGuid(),
                session.Id,
                node.Id,
                new ContactMessage(
                    new byte[] { 1, 2, 3, 4, 5, 6 },
                    1,
                    MessageTextType.Plain,
                    now,
                    "survives restart",
                    Array.Empty<byte>(),
                    null),
                now,
                null,
                null),
            CancellationToken);
        var order = new List<string>();
        var ingress = new FakeIngress(order);
        ingress.Failures.Enqueue(new IOException("disk full"));
        var coordinator = CreateCoordinator(
            new FakeUi(order),
            new FakeConnections(order),
            ingress,
            new FakeSessionCompletions(order));

        try
        {
            await Assert.ThrowsAsync<DesktopShutdownException>(
                () => coordinator.ShutdownAsync(CancellationToken));

            Assert.Throws<ApplicationInstanceAlreadyRunningException>(
                () => ApplicationInstanceLock.Acquire(paths));
            var liveSummary = Assert.Single(
                await storage.History.GetConversationsAsync(node.Id, 10, CancellationToken));
            Assert.Equal(
                "survives restart",
                Assert.Single(await storage.History.GetMessagesAsync(
                    node.Id,
                    liveSummary.Id,
                    null,
                    10,
                    CancellationToken)).Text);

            await coordinator.ShutdownAsync(CancellationToken);
        }
        finally
        {
            await storage.DisposeAsync();
            instanceLock.Dispose();
        }

        using var restartedLock = ApplicationInstanceLock.Acquire(paths);
        await using var reopened = await LocalStorage.OpenAsync(paths, CancellationToken);
        var reopenedSummary = Assert.Single(
            await reopened.History.GetConversationsAsync(node.Id, 10, CancellationToken));
        Assert.Equal(
            "survives restart",
            Assert.Single(await reopened.History.GetMessagesAsync(
                node.Id,
                reopenedSummary.Id,
                null,
                10,
                CancellationToken)).Text);
    }

    private static DesktopShutdownCoordinator CreateCoordinator(
        IDesktopUiLifetime ui,
        IDesktopConnectionLifecycle connections,
        IDurableMessageIngress ingress,
        IDurableSessionCompletion sessionCompletions) =>
        new(ui, connections, ingress, sessionCompletions, NullLogger<DesktopShutdownCoordinator>.Instance);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private sealed class FakeUi(List<string> order) : IDesktopUiLifetime
    {
        public int StopCount { get; private set; }

        public Task StopAsync()
        {
            StopCount++;
            order.Add("ui");
            return Task.CompletedTask;
        }

        public Task ReportShutdownFailureAsync(Exception exception)
        {
            order.Add("report");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeConnections(List<string> order) : IDesktopConnectionLifecycle
    {
        public int ShutdownCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ShutdownAsync(CancellationToken cancellationToken = default)
        {
            ShutdownCount++;
            order.Add("connections");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeIngress(List<string> order) : IDurableMessageIngress
    {
        public Queue<Exception> Failures { get; } = [];
        public TaskCompletionSource FlushStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? FlushGate { get; init; }
        public bool IsPaused { get; private set; }
        public int PendingMessageCount { get; private set; } = 1;
        public int FlushCount { get; private set; }
        public int RetryCount { get; private set; }

        public async Task FlushAsync(CancellationToken cancellationToken = default)
        {
            FlushCount++;
            order.Add("flush");
            FlushStarted.TrySetResult();
            if (FlushGate is not null)
            {
                await FlushGate.Task.WaitAsync(cancellationToken);
            }

            if (Failures.TryDequeue(out var exception))
            {
                IsPaused = true;
                throw new ReceiveIngestException(exception);
            }

            PendingMessageCount = 0;
        }

        public Task RetryAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RetryCount++;
            order.Add("retry");
            IsPaused = false;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSessionCompletions(List<string> order) : IDurableSessionCompletion
    {
        public Queue<Exception> Failures { get; } = [];
        public bool IsPaused { get; private set; }
        public int PendingCount { get; set; }

        public Task EndAsync(
            Guid sessionId,
            DateTimeOffset endedUtc,
            string reason,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task FlushAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            order.Add("session-flush");
            if (Failures.TryDequeue(out var exception))
            {
                IsPaused = true;
                throw new SessionCompletionPersistenceException(exception);
            }

            PendingCount = 0;
            return Task.CompletedTask;
        }

        public Task RetryAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            order.Add("session-retry");
            IsPaused = false;
            return Task.CompletedTask;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "MeshCoreMessenger.Shutdown.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

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

    private sealed record TestPaths(string DataDirectory) : IAppPaths
    {
        public string DatabasePath { get; } = System.IO.Path.Combine(DataDirectory, "messenger.db");
        public string BackupsDirectory { get; } = System.IO.Path.Combine(DataDirectory, "backups");
    }
}
