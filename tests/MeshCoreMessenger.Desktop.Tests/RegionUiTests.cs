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
                Assert.True(options!.Consent.AllowTemporaryZeroHop); Assert.False(options.Consent.AllowContactCreation);
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
            vm.AllowTemporaryZeroHop = true;
            var search = vm.SearchCommand.ExecuteAsync(null); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3), Token);
            if (switchNode) { vm.SetNode(null, null); release.TrySetResult(); }
            await search;
            Assert.Empty(await storage.Regions.GetRegionsAsync(node.Id, true, Token));
            if (switchNode) Assert.Empty(vm.FoundRegions);
            else
            {
                Assert.Equal(2, vm.FoundRegions.Count); Assert.False(vm.AllowTemporaryZeroHop);
                vm.SelectedFound = vm.FoundRegions[0]; await vm.AddFoundCommand.ExecuteAsync(null);
                Assert.Single(await storage.Regions.GetRegionsAsync(node.Id, true, Token));
                vm.SelectedFound = vm.FoundRegions[1]; await vm.AddFoundCommand.ExecuteAsync(null);
                Assert.Equal("$team", vm.ProtocolName); Assert.True(vm.HasError);
                Assert.Single(await storage.Regions.GetRegionsAsync(node.Id, true, Token));
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
    public async Task OfflineCatalogCrudPersistsAndInvalidPrivateKeyKeepsForm()
    {
        var path = Path.Combine(Path.GetTempPath(), "region-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var storage = await LocalStorage.OpenAsync(DesktopAppPaths.CreateForDirectory(path), Token);
            var node = await storage.Nodes.FindOrCreateAsync(new byte[32], "A", DateTimeOffset.UtcNow, Token);
            var pending = new ConcurrentBag<Task>();
            using var vm = new RegionSettingsViewModel(new RegionService(storage.Regions, TimeProvider.System), new ImmediateUiDispatcher(), task => { pending.Add(task); return task; });
            vm.SetNode(node.Id, "A"); await vm.InitializeAsync();
            vm.Label = "Ростов"; vm.ProtocolName = "ru-ros"; await vm.AddPublicCommand.ExecuteAsync(null);
            var added = Assert.Single(vm.Catalog); Assert.Equal("ru-ros", added.ProtocolName);
            vm.Selected = added; vm.Label = "Новое имя"; await vm.RenameCommand.ExecuteAsync(null);
            Assert.Equal("Новое имя", Assert.Single(vm.Catalog).Label);
            await vm.ArchiveCommand.ExecuteAsync(null); Assert.True(Assert.Single(vm.Catalog).Archived);
            vm.Label = "Команда"; vm.ProtocolName = "$team"; vm.KeyHex = "bad";
            await vm.ImportCommand.ExecuteAsync(null); Assert.True(vm.HasError); Assert.Equal("bad", vm.KeyHex);
            Assert.Single(await storage.Regions.GetRegionsAsync(node.Id, true, Token));
            await vm.DeleteCommand.ExecuteAsync(null); Assert.Empty(vm.Catalog);
            vm.Dispose(); await Task.WhenAll(pending);
        }
        finally { Directory.Delete(path, true); }
    }
    [Fact]
    public async Task SwitchingNodeClearsPrivateInputAndDoesNotMixCatalogs()
    {
        var path = Path.Combine(Path.GetTempPath(), "region-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var storage = await LocalStorage.OpenAsync(DesktopAppPaths.CreateForDirectory(path), Token);
            var a = await storage.Nodes.FindOrCreateAsync(new byte[32], "A", DateTimeOffset.UtcNow, Token);
            var key = new byte[32]; key[31] = 1;
            var b = await storage.Nodes.FindOrCreateAsync(key, "B", DateTimeOffset.UtcNow, Token);
            var pending = new ConcurrentBag<Task>();
            using var vm = new RegionSettingsViewModel(new RegionService(storage.Regions, TimeProvider.System), new ImmediateUiDispatcher(), task => { pending.Add(task); return task; });
            vm.SetNode(a.Id, "A"); await vm.InitializeAsync(); vm.Label = "A"; vm.ProtocolName = "Area";
            await vm.AddPublicCommand.ExecuteAsync(null); vm.KeyHex = "sensitive";
            vm.SetNode(b.Id, "B"); await vm.RefreshAsync(); Assert.Empty(vm.Catalog); Assert.Equal("", vm.KeyHex);
            vm.SetNode(a.Id, "A"); await vm.RefreshAsync(); Assert.Single(vm.Catalog);
            vm.Dispose(); await Task.WhenAll(pending);
        }
        finally { Directory.Delete(path, true); }
    }
}
