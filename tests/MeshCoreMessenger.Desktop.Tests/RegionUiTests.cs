using System.Collections.Concurrent;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Core.Domain;
using MeshCoreSharp.Models;
using MeshCoreMessenger.Desktop.Platform;
using MeshCoreMessenger.Desktop.ViewModels;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed class RegionUiTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscoveryUsesConsentAndOnlyExplicitSaveCreatesCatalogEntryOrDiscardsOldNode(bool switchNode)
    {
        var path = Path.Combine(Path.GetTempPath(), "region-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var storage = await LocalStorage.OpenAsync(DesktopAppPaths.CreateForDirectory(path), Token);
            var node = await storage.Nodes.FindOrCreateAsync(new byte[32], "A", DateTimeOffset.UtcNow, Token);
            var supervisor = new FakeConnectionSupervisor(); var session = Guid.NewGuid();
            supervisor.Publish(new(ConnectionSupervisorState.Online, 1, null, session, node.Id, null, null));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var discovery = new DiscoveryStub(async (owner, options) =>
            {
                Assert.True(options!.Consent.AllowTemporaryZeroHop); Assert.True(options.Consent.AllowContactCreation);
                Assert.Equal(TimeSpan.FromSeconds(30), options.Neighbors.CollectionWindow);
                Assert.Equal(4, options.MaximumTargets); Assert.Equal(3, options.MaximumAttempts); Assert.Null(options.TotalTimeout);
                entered.TrySetResult(); if (switchNode) await release.Task;
                var sources = new[] { new string('A', 64) };
                return new(Guid.NewGuid(), new(owner.NodeId, owner.SessionId, owner.Generation), [], [],
                    [new("ru-ros", DiscoveredRegionNameKind.Public, sources), new("$team", DiscoveredRegionNameKind.PrivateKeyRequired, sources)],
                    false, false, false, false, 0);
            });
            var pending = new ConcurrentBag<Task>();
            using var vm = new RegionSettingsViewModel(new RegionService(storage.Regions, TimeProvider.System, discovery: discovery),
                new ImmediateUiDispatcher(), task => { pending.Add(task); return task; });
            vm.ConfigureConnection(supervisor, new RegionGateway(), storage.Regions); vm.SetNode(node.Id, "A"); await vm.InitializeAsync();
            vm.CatalogSearch.SetActive(true); await vm.CatalogSearch.RefreshAsync();
            var search = vm.CatalogSearch.SearchCommand.ExecuteAsync(null); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3), Token);
            if (switchNode) { vm.SetNode(null, null); release.TrySetResult(); }
            await search;
            Assert.Empty(await storage.Regions.GetRegionsAsync(node.Id, true, Token));
            if (switchNode) Assert.Empty(vm.CatalogSearch.Rows);
            else
            {
                var row = Assert.Single(vm.CatalogSearch.Rows); Assert.Equal("ru-ros", row.Name); Assert.Equal("+", row.Symbol);
                await row.ToggleCommand.ExecuteAsync(null);
                Assert.Single(await storage.Regions.GetRegionsAsync(node.Id, true, Token));
                Assert.Equal("−", row.Symbol);
                await row.ToggleCommand.ExecuteAsync(null); Assert.Equal("+", row.Symbol);
                Assert.Single(vm.CatalogSearch.Rows); Assert.Empty(await storage.Regions.GetRegionsAsync(node.Id, true, Token));
                vm.CatalogSearch.SetActive(false); vm.CatalogSearch.SetActive(true); await vm.CatalogSearch.RefreshAsync();
                Assert.Empty(vm.CatalogSearch.Rows);
            }
            vm.Dispose(); await Task.WhenAll(pending);
        }
        finally { Directory.Delete(path, true); }
    }
    private sealed class DiscoveryStub(Func<SessionCommandOwner, RegionDiscoveryOptions?, Task<RegionDiscoveryRunResult>> run) : IRegionDiscoveryService
    {
        public Task<RegionDiscoveryRunResult> DiscoverAsync(SessionCommandOwner owner, RegionDiscoveryOptions? options = null,
            IProgress<RegionDiscoveryProgress>? progress = null, CancellationToken cancellationToken = default) => run(owner, options);
    }
    [Fact]
    public async Task DefaultCardShowsUnknownAndUnsupportedWithoutIssuingCommands()
    {
        var path = Path.Combine(Path.GetTempPath(), "region-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var storage = await LocalStorage.OpenAsync(DesktopAppPaths.CreateForDirectory(path), Token);
            var node = await storage.Nodes.FindOrCreateAsync(new byte[32], "A", DateTimeOffset.UtcNow, Token);
            var supervisor = new FakeConnectionSupervisor(); var gateway = new RegionGateway();
            using var vm = new RegionSettingsViewModel(new RegionService(storage.Regions, TimeProvider.System), new ImmediateUiDispatcher(), task => task);
            vm.ConfigureConnection(supervisor, gateway, storage.Regions);
            vm.SetNode(node.Id, "A"); await vm.InitializeAsync();
            Assert.Contains("offline", vm.DefaultStatus); Assert.False(vm.ClearDefaultCommand.CanExecute(null));
            supervisor.Publish(new(ConnectionSupervisorState.Online, 1, null, Guid.NewGuid(), node.Id, null, null));
            await vm.RefreshAsync(); Assert.Contains("неизвестен", vm.DefaultStatus);
            gateway.Capabilities = RegionCapabilities.Unknown with { DefaultReadWrite = RegionSupport.Unsupported };
            gateway.Snapshot = new(node.Id, supervisor.Snapshot.SessionId, 1, DateTimeOffset.UtcNow, RegionSupport.Unsupported,
                NodeFloodScope.Unknown, RegionSnapshotFreshness.CurrentSession);
            await vm.RefreshAsync(); Assert.Contains("Не поддерживается", vm.DefaultStatus);
            Assert.False(vm.ClearDefaultCommand.CanExecute(null)); Assert.Equal(0, gateway.AcquireCalls);
            vm.Dispose();
        }
        finally { Directory.Delete(path, true); }
    }
    private sealed class RegionGateway : ISessionCommandGateway
    {
        public RegionCapabilities Capabilities { get; set; } = RegionCapabilities.Unknown;
        public NodeRegionSnapshot? Snapshot { get; set; }
        public int AcquireCalls { get; private set; }
        public NodeRegionSnapshot? GetDefaultRegion(Guid node) => Snapshot;
        public RegionCapabilities? GetRegionCapabilities(Guid node) => Capabilities;
        public SessionCommandLease Acquire(Guid node, SessionCommandTarget target, CancellationToken token = default)
        { AcquireCalls++; throw new InvalidOperationException("No command permitted in read-only UI test."); }
    }
    [Fact]
    public async Task OfflineCatalogRemainsEditableWithoutAnySearchOrNodeCommands()
    {
        var path = Path.Combine(Path.GetTempPath(), "region-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var storage = await LocalStorage.OpenAsync(DesktopAppPaths.CreateForDirectory(path), Token);
            var node = await storage.Nodes.FindOrCreateAsync(new byte[32], "A", DateTimeOffset.UtcNow, Token);
            var pending = new ConcurrentBag<Task>();
            var service = new RegionService(storage.Regions, TimeProvider.System);
            await service.CreatePublicAsync(node.Id, "Ростов", "ru-ros", Token);
            using var vm = new RegionSettingsViewModel(service, new ImmediateUiDispatcher(), task => { pending.Add(task); return task; });
            vm.SetNode(node.Id, "A"); await vm.InitializeAsync();
            vm.CatalogSearch.SetActive(true); await vm.CatalogSearch.RefreshAsync();
            Assert.False(vm.CatalogSearch.SearchCommand.CanExecute(null));
            var row = Assert.Single(vm.CatalogSearch.Rows); await row.ToggleCommand.ExecuteAsync(null);
            Assert.Equal("+", row.Symbol); Assert.Empty(await service.GetRegionsAsync(node.Id, true, Token));
            await row.ToggleCommand.ExecuteAsync(null); Assert.Equal("−", row.Symbol);
            vm.Dispose(); await Task.WhenAll(pending);
        }
        finally { Directory.Delete(path, true); }
    }
    [Fact]
    public async Task SwitchingNodeClearsCandidatesAndDoesNotMixCatalogs()
    {
        var path = Path.Combine(Path.GetTempPath(), "region-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var storage = await LocalStorage.OpenAsync(DesktopAppPaths.CreateForDirectory(path), Token);
            var a = await storage.Nodes.FindOrCreateAsync(new byte[32], "A", DateTimeOffset.UtcNow, Token);
            var key = new byte[32]; key[31] = 1;
            var b = await storage.Nodes.FindOrCreateAsync(key, "B", DateTimeOffset.UtcNow, Token);
            var pending = new ConcurrentBag<Task>();
            var service = new RegionService(storage.Regions, TimeProvider.System);
            await service.CreatePublicAsync(a.Id, "A", "Area", Token);
            using var vm = new RegionSettingsViewModel(service, new ImmediateUiDispatcher(), task => { pending.Add(task); return task; });
            vm.SetNode(a.Id, "A"); await vm.InitializeAsync(); vm.CatalogSearch.SetActive(true); await vm.CatalogSearch.RefreshAsync();
            var row = Assert.Single(vm.CatalogSearch.Rows); await row.ToggleCommand.ExecuteAsync(null); Assert.Equal("+", row.Symbol);
            vm.SetNode(b.Id, "B"); await vm.CatalogSearch.RefreshAsync(); Assert.Empty(vm.CatalogSearch.Rows);
            vm.SetNode(a.Id, "A"); await vm.CatalogSearch.RefreshAsync(); Assert.Empty(vm.CatalogSearch.Rows);
            vm.Dispose(); await Task.WhenAll(pending);
        }
        finally { Directory.Delete(path, true); }
    }
}
