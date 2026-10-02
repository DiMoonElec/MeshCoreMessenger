using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Bootstrap;
using MeshCoreMessenger.Desktop.DevFixtures;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.Platform;
using MeshCoreMessenger.Desktop.ViewModels;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed class FakeDataSeederTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 30, 0, TimeSpan.Zero);
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EmptyDatabaseGetsCompleteVisualDatasetThroughProductionStores()
    {
        using var temporary = new TemporaryDirectory();
        using var instanceLock = ApplicationInstanceLock.Acquire(temporary.Paths);
        await FakeDataSeeder.EnsureEmptyDatabaseAsync(temporary.Paths, CancellationToken);
        await using var storage = await LocalStorage.OpenAsync(temporary.Paths, CancellationToken);
        var result = await SeedAsync(storage, temporary.Paths);
        Assert.Equal(5113, result.MessageCount);
        Assert.Equal(5, result.ChannelCount);
        Assert.Equal(11, result.ContactCount);
        var node = Assert.Single(await storage.Nodes.GetAllAsync(10, CancellationToken));
        Assert.Equal(result.NodeId, node.Id);
        Assert.Equal(node.Id.ToString("D"), await storage.Settings.GetAsync(MainWindowViewModel.ViewedNodeSettingKey, CancellationToken));
        var profile = Assert.Single(await storage.ConnectionProfiles.GetAllAsync(CancellationToken));
        Assert.Equal(FakeDataSeeder.ProfileName, profile.Name);
        Assert.False(profile.AutoConnect);
        Assert.False(profile.Reconnect);
        Assert.Equal(ConnectionTransportKind.Serial, profile.Transport);
        Assert.False(File.Exists(profile.SerialPortName));
        Assert.False(Directory.Exists(Path.GetDirectoryName(profile.SerialPortName)));
        var session = await storage.Sessions.GetAsync(result.SessionId, CancellationToken);
        Assert.NotNull(session);
        Assert.Equal(Now, session.EndedUtc);
        Assert.Equal(FakeDataSeeder.SessionEndReason, session.EndReason);
        Assert.Equal(profile.Id, session.ConnectionProfileId);
        Assert.Equal(node.Id, session.NodeId);

        var channels = (await storage.ConversationDirectory.GetPageAsync(node.Id, ConversationDirectorySection.Channels, null, 100, CancellationToken)).Items;
        Assert.Equal(5, channels.Count);
        Assert.Null(channels.Single(channel => channel.DisplayName == "#fixture-empty").ConversationId);
        Assert.All(channels, channel => Assert.Equal(ChannelAccessKind.PublicOrHashtag, channel.ChannelAccessKind));
        Assert.Contains(channels, channel => channel.DisplayName!.Length > 200);
        var twenty = channels.Single(channel => channel.DisplayName == "#fixture-twenty");
        var messages = await storage.History.GetMessagesAsync(node.Id, twenty.ConversationId!.Value, null, 100, CancellationToken);
        Assert.Equal(20, messages.Count);
        Assert.All(messages, message => Assert.Equal(MessageDirection.Incoming, message.Direction));
        Assert.Contains(messages, message => message.Text!.Contains('\n'));
        Assert.Contains(messages, message => message.Text!.Length > 8000);
        Assert.Contains(messages, message => message.Text!.Length >= 2048 && !message.Text.Contains(' '));
        Assert.Contains(messages, message => message.Text!.Contains("https://"));
        Assert.Contains(messages, message => message.ReceivedUtc.Date == Now.Date);
        Assert.Contains(messages, message => message.ReceivedUtc.Date == Now.AddDays(-1).Date);
        Assert.Contains(messages, message => message.ReceivedUtc.Month == Now.AddMonths(-1).Month);
        Assert.Contains(messages, message => message.ReceivedUtc.Month == Now.AddMonths(-2).Month);
        Assert.Equal(5000, await CountMessagesAsync(storage, node.Id, channels.Single(channel => channel.DisplayName == "#fixture-5000").ConversationId!.Value));
        var unread = channels.Single(channel => channel.DisplayName == "#fixture-unread");
        Assert.Equal(10, unread.UnreadCount);
        Assert.Equal(30, await CountMessagesAsync(storage, node.Id, unread.ConversationId!.Value));
        Assert.All(channels.Where(channel => channel != unread), channel => Assert.Equal(0, channel.UnreadCount));

        var people = (await storage.ConversationDirectory.GetPageAsync(node.Id, ConversationDirectorySection.ChatContacts, null, 100, CancellationToken)).Items;
        Assert.Equal(5, people.Count);
        Assert.Contains(people, person => person.DisplayName!.Length > 100);
        Assert.Contains(people, person => person.DisplayName!.Contains("🐈"));
        foreach (var person in people)
            Assert.Equal(12, await CountMessagesAsync(storage, node.Id, person.ConversationId!.Value));

        var devices = (await storage.ConversationDirectory.GetPageAsync(node.Id, ConversationDirectorySection.ServiceContacts, null, 100, CancellationToken)).Items;
        Assert.Equal(6, devices.Count);
        foreach (var type in new[] { 2, 3, 4 })
        {
            var pair = devices.Where(device => device.ContactType == type).ToArray();
            Assert.Equal(2, pair.Length);
            Assert.Contains(pair, device => device.PresentOnNode == true);
            Assert.Contains(pair, device => device.PresentOnNode == false);
        }
        var details = new List<ContactDetailsProjection>();
        foreach (var device in devices)
            details.Add((await storage.ConversationDirectory.GetContactDetailsAsync(node.Id, device.Identity, CancellationToken))!);
        Assert.Contains(details, device => device.Latitude == 0 && device.Longitude == 0);
        Assert.Contains(details, device => device.Latitude > 0 && device.Longitude > 0);
        Assert.Equal(6, details.Select(device => device.LastAdvertUtc).Distinct().Count());
    }

    [Fact]
    public async Task RepeatedSeedRefusesWithoutChangingDatabaseOrOtherWorkspace()
    {
        using var temporary = new TemporaryDirectory();
        using var other = new TemporaryDirectory();
        await using (var storage = await LocalStorage.OpenAsync(temporary.Paths, CancellationToken))
        {
            await SeedAsync(storage, temporary.Paths);
            await Assert.ThrowsAsync<FakeDataSeedException>(() => SeedAsync(storage, temporary.Paths));
        }
        await using (var storage = await LocalStorage.OpenAsync(other.Paths, CancellationToken))
            await storage.Nodes.FindOrCreateAsync(new byte[32], "Real data — preserved", Now, CancellationToken);
        var original = SHA256.HashData(File.ReadAllBytes(temporary.Paths.DatabasePath));
        var otherOriginal = SHA256.HashData(File.ReadAllBytes(other.Paths.DatabasePath));
        await Assert.ThrowsAsync<FakeDataSeedException>(() => FakeDataSeeder.EnsureEmptyDatabaseAsync(temporary.Paths, CancellationToken));
#if DEBUG
        Assert.Equal(5, MeshCoreMessenger.Desktop.Program.Main(["--seed-fake-data", "--data-dir", temporary.Paths.DataDirectory]));
#endif
        Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(temporary.Paths.DatabasePath)));
        Assert.Equal(otherOriginal, SHA256.HashData(File.ReadAllBytes(other.Paths.DatabasePath)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NodeOnlyOrProfileOnlyDatabaseRefusesBeforeFirstFixtureWrite(bool profileOnly)
    {
        using var temporary = new TemporaryDirectory();
        await using var storage = await LocalStorage.OpenAsync(temporary.Paths, CancellationToken);
        if (profileOnly)
            await storage.ConnectionProfiles.SaveAsync(new ConnectionProfile
            {
                Id = Guid.NewGuid(), Name = "Existing profile", Transport = ConnectionTransportKind.Tcp,
                TcpHost = "127.0.0.1", TcpPort = 5000, CreatedUtc = Now, UpdatedUtc = Now,
            }, CancellationToken);
        else
            await storage.Nodes.FindOrCreateAsync(new byte[32], "Existing node", Now, CancellationToken);
        await Assert.ThrowsAsync<FakeDataSeedException>(() => FakeDataSeeder.EnsureEmptyDatabaseAsync(temporary.Paths, CancellationToken));
        await Assert.ThrowsAsync<FakeDataSeedException>(() => SeedAsync(storage, temporary.Paths));
        Assert.Equal(profileOnly ? 0 : 1, (await storage.Nodes.GetAllAsync(10, CancellationToken)).Count);
        Assert.Equal(profileOnly ? 1 : 0, (await storage.ConnectionProfiles.GetAllAsync(CancellationToken)).Count);
    }

    [Fact]
    public async Task EmptyInitializedDatabasePassesReadOnlyPreflight()
    {
        using var temporary = new TemporaryDirectory();
        await using (var storage = await LocalStorage.OpenAsync(temporary.Paths, CancellationToken)) { }
        var original = SHA256.HashData(File.ReadAllBytes(temporary.Paths.DatabasePath));
        await FakeDataSeeder.EnsureEmptyDatabaseAsync(temporary.Paths, CancellationToken);
        Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(temporary.Paths.DatabasePath)));
    }

    [Fact]
    public async Task ClosedFixtureSessionSurvivesReopenAndDesktopStartupWithoutAttempts()
    {
        using var temporary = new TemporaryDirectory();
        FakeDataSeedResult seeded;
        await using (var storage = await LocalStorage.OpenAsync(temporary.Paths, CancellationToken))
            seeded = await SeedAsync(storage, temporary.Paths);
        await using var reopened = await LocalStorage.OpenAsync(temporary.Paths, CancellationToken);
        var before = await reopened.Sessions.GetAsync(seeded.SessionId, CancellationToken);

        // Actual Desktop DI/startup path; no window or physical transport is needed.
        await using (var services = AppBootstrap.CreateServiceProvider(temporary.Paths, reopened))
        {
            var viewModel = services.GetRequiredService<MainWindowViewModel>();
            await viewModel.LoadAsync(CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken);
            Assert.Null(viewModel.ErrorMessage);
            Assert.Equal(seeded.NodeId, viewModel.ViewedNode!.Id);
            Assert.NotEmpty(viewModel.Navigation.Conversations);
            Assert.Equal(6, viewModel.Devices.Items.Count);
            Assert.Equal(new[] { "Датчик", "Комната", "Ретранслятор" },
                viewModel.Devices.Items.Select(item => item.Type).Distinct().Order().ToArray());
            var chatSelection = viewModel.Navigation.SelectedConversation;
            var draftOwner = viewModel.Navigation.Draft;
            viewModel.Shell.SelectSection(ShellSection.Devices);
            Assert.Same(chatSelection, viewModel.Navigation.SelectedConversation);
            Assert.Same(draftOwner, viewModel.Navigation.Draft);
            var supervisor = services.GetRequiredService<IConnectionSupervisor>();
            await services.GetRequiredService<DesktopConnectionLifecycle>().StartAsync(CancellationToken);
            Assert.Equal(ConnectionSupervisorState.Offline, supervisor.Snapshot.State);
            Assert.Equal(0, supervisor.Snapshot.Generation);
            Assert.Equal(0, services.GetRequiredService<SessionCompletionTracker>().PendingCount);
            Assert.Equal("Не подключено", viewModel.ConnectionStatus);
            await viewModel.StopAsync();
            await services.GetRequiredService<DesktopConnectionLifecycle>().ShutdownAsync(CancellationToken);
        }
        Assert.Equal(before, await reopened.Sessions.GetAsync(seeded.SessionId, CancellationToken));
        Assert.Equal(FakeDataSeeder.SessionEndReason, before!.EndReason);

        var manager = new ConnectionProfileManager(reopened.ConnectionProfiles, reopened.Settings, TimeProvider.System);
        var attempts = new CountingAttemptFactory();
        await using var countedSupervisor = new ConnectionSupervisor(manager, attempts, new ConnectionFailureClassifier(),
            new SystemReconnectDelay(), new ZeroJitter(), TimeProvider.System);
        var profilesViewModel = new ConnectionProfilesViewModel(manager, countedSupervisor, new EmptyPortCatalog(),
            NullLogger<ConnectionProfilesViewModel>.Instance);
        await profilesViewModel.LoadAsync(CancellationToken);
        Assert.Single(profilesViewModel.AvailableProfiles);
        await using var lifecycle = new DesktopConnectionLifecycle(countedSupervisor);
        await lifecycle.StartAsync(CancellationToken);
        Assert.Equal(0, attempts.Count);
        // Even if explicitly selected (or selected by a future single-profile default), no auto-connect.
        await manager.SelectAsync(seeded.ProfileId, CancellationToken);
        await countedSupervisor.StartAutoConnectAsync(CancellationToken);
        Assert.Equal(0, attempts.Count);
        Assert.Equal(ConnectionSupervisorState.Offline, countedSupervisor.Snapshot.State);
        await profilesViewModel.StopAsync();
    }

    [Fact]
    public async Task UnverifiableDatabaseIsRejectedWithoutChanges()
    {
        using var temporary = new TemporaryDirectory();
        Directory.CreateDirectory(temporary.Paths.DataDirectory);
        File.WriteAllText(temporary.Paths.DatabasePath, "not a SQLite database — preserve me");
        var original = File.ReadAllBytes(temporary.Paths.DatabasePath);
        await Assert.ThrowsAsync<FakeDataSeedException>(() => FakeDataSeeder.EnsureEmptyDatabaseAsync(temporary.Paths, CancellationToken));
        Assert.Equal(original, File.ReadAllBytes(temporary.Paths.DatabasePath));
    }

    [Fact]
    public async Task DatabaseSymlinkCannotRedirectSeedIntoAnotherWorkspace()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var temporary = new TemporaryDirectory();
        using var other = new TemporaryDirectory();
        await using (var storage = await LocalStorage.OpenAsync(other.Paths, CancellationToken)) { }
        var original = SHA256.HashData(File.ReadAllBytes(other.Paths.DatabasePath));
        Directory.CreateDirectory(temporary.Paths.DataDirectory);
        File.CreateSymbolicLink(temporary.Paths.DatabasePath, other.Paths.DatabasePath);
        await Assert.ThrowsAsync<FakeDataSeedException>(() => FakeDataSeeder.EnsureEmptyDatabaseAsync(temporary.Paths, CancellationToken));
        Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(other.Paths.DatabasePath)));
    }

    [Fact]
    public async Task LargeModeCreatesAdditionalHundredThousandMessageChannel()
    {
        using var temporary = new TemporaryDirectory();
        await using var storage = await LocalStorage.OpenAsync(temporary.Paths, CancellationToken);
        var result = await new FakeDataSeeder().SeedAsync(storage, temporary.Paths, true, Now, CancellationToken);
        Assert.Equal(105113, result.MessageCount);
        var channels = await storage.ConversationDirectory.GetPageAsync(result.NodeId, ConversationDirectorySection.Channels, null, 100, CancellationToken);
        Assert.Equal(6, channels.Items.Count);
        var large = channels.Items.Single(channel => channel.DisplayName == "#fixture-100000");
        Assert.Equal(100000, await CountMessagesAsync(storage, result.NodeId, large.ConversationId!.Value));
    }

    private static Task<FakeDataSeedResult> SeedAsync(LocalStorage storage, DesktopAppPaths paths) =>
        new FakeDataSeeder().SeedAsync(storage, paths, false, Now, CancellationToken);

    private static async Task<int> CountMessagesAsync(LocalStorage storage, Guid nodeId, Guid conversationId)
    {
        var count = 0;
        HistoryMessagePosition? before = null;
        while (true)
        {
            var page = await storage.History.GetMessagesBeforeAsync(nodeId, conversationId, before, 100, CancellationToken);
            count += page.Items.Count;
            if (!page.HasEarlier)
                return count;
            before = page.FirstPosition;
        }
    }

    private sealed class CountingAttemptFactory : IConnectionAttemptFactory
    {
        public int Count { get; private set; }
        public Task<IConnectionAttempt> CreateAsync(ConnectionProfile profile, long generation, CancellationToken cancellationToken = default)
        {
            Count++;
            throw new InvalidOperationException("A fixture must never create a transport attempt.");
        }
    }

    private sealed class ZeroJitter : IReconnectJitter
    {
        public double GetJitterFraction(int retryNumber) => 0;
    }

    private sealed class EmptyPortCatalog : ISerialPortCatalog
    {
        public Task<IReadOnlyList<string>> GetPortNamesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public DesktopAppPaths Paths { get; } = DesktopAppPaths.CreateForDirectory(Path.Combine(
            Path.GetTempPath(), "MeshCoreMessenger.Fixture.Tests", Guid.NewGuid().ToString("N")));
        public void Dispose()
        {
            if (Directory.Exists(Paths.DataDirectory))
                Directory.Delete(Paths.DataDirectory, recursive: true);
        }
    }
}
