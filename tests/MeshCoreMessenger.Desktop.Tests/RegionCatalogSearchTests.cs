using System.Collections.Concurrent;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Platform;
using MeshCoreMessenger.Desktop.ViewModels;
using MeshCoreSharp.Models;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed class RegionCatalogSearchTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private sealed class Discovery(Func<SessionCommandOwner, IProgress<RegionDiscoveryProgress>?, CancellationToken, Task<RegionDiscoveryRunResult>> run) : IRegionDiscoveryService
    {
        public Task<RegionDiscoveryRunResult> DiscoverAsync(SessionCommandOwner owner, RegionDiscoveryOptions? options = null,
            IProgress<RegionDiscoveryProgress>? progress = null, CancellationToken cancellationToken = default) => run(owner, progress, cancellationToken);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal required string Path;
        internal required LocalStorage Storage;
        internal required RegionService Service;
        internal required RegionCatalogSearchViewModel Vm;
        internal required SessionCommandOwner Owner;
        internal SessionCommandOwner? ActiveOwner;
        internal readonly ConcurrentBag<Task> Tasks = [];
        internal static async Task<Fixture> Create(Func<SessionCommandOwner, IProgress<RegionDiscoveryProgress>?, CancellationToken, Task<RegionDiscoveryRunResult>>? run = null)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "region-simple-ui-" + Guid.NewGuid().ToString("N"));
            var storage = await LocalStorage.OpenAsync(DesktopAppPaths.CreateForDirectory(path), Token);
            var node = await storage.Nodes.FindOrCreateAsync(new byte[32], "A", DateTimeOffset.UtcNow, Token);
            var service = new RegionService(storage.Regions, TimeProvider.System, discovery: new Discovery(run ?? ((o, _, _) => Task.FromResult(Result(o)))));
            var owner = new SessionCommandOwner(node.Id, Guid.NewGuid(), 1, "A", null, null);
            Fixture? f = null;
            var vm = new RegionCatalogSearchViewModel(service, new ImmediateUiDispatcher(), task => { f!.Tasks.Add(task); return task; }, () => f!.ActiveOwner);
            f = new() { Path = path, Storage = storage, Service = service, Vm = vm, Owner = owner, ActiveOwner = owner };
            vm.SetNode(node.Id); return f;
        }
        public async ValueTask DisposeAsync()
        {
            Vm.Dispose(); await Task.WhenAll(Tasks); await Storage.DisposeAsync(); Directory.Delete(Path, true);
        }
    }
    private static DiscoveredRegionName Public(string name) => new(name, DiscoveredRegionNameKind.Public, [new string('A', 64)]);
    private static RegionDiscoveryRunResult Result(SessionCommandOwner owner, params DiscoveredRegionName[] names) =>
        new(Guid.NewGuid(), new(owner.NodeId, owner.SessionId, owner.Generation), [], [], names, false, false, false, false, 0);

    [Fact]
    public async Task RepeatedSearchUnionsCatalogAndCandidatesAndDeletedRowsLastOnlyForOneVisit()
    {
        var calls = 0;
        await using var f = await Fixture.Create((owner, _, _) => Task.FromResult(Result(owner,
            Public("saved"), Public("ru-ros"), Public("#ru-ros"), Public(++calls == 1 ? "first" : "second"),
            new("*", DiscoveredRegionNameKind.Unscoped, []), new("$hidden", DiscoveredRegionNameKind.PrivateKeyRequired, []))));
        await f.Service.CreatePublicAsync(f.Owner.NodeId, "Saved", "saved", Token);
        f.Vm.SetActive(true); await f.Vm.RefreshAsync();
        Assert.Equal("−", Assert.Single(f.Vm.Rows).Symbol);
        await f.Vm.SearchCommand.ExecuteAsync(null); await f.Vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "first", "ru-ros", "saved", "second" }, f.Vm.Rows.Select(r => r.Name));
        Assert.Single(await f.Service.GetRegionsAsync(f.Owner.NodeId, true, Token));
        var found = f.Vm.Rows.Single(r => r.Name == "ru-ros"); await found.ToggleCommand.ExecuteAsync(null);
        Assert.Equal("−", found.Symbol);
        var saved = f.Vm.Rows.Single(r => r.Name == "saved"); await saved.ToggleCommand.ExecuteAsync(null);
        Assert.Equal("+", saved.Symbol); Assert.Equal(4, f.Vm.Rows.Count);
        f.Vm.SetActive(false); Assert.Empty(f.Vm.Rows);
        f.Vm.SetActive(true); await f.Vm.RefreshAsync();
        Assert.Equal("ru-ros", Assert.Single(f.Vm.Rows).Name); Assert.Equal("−", f.Vm.Rows[0].Symbol);
    }

    [Fact]
    public async Task ArchivedAndPrivateEntriesAreVisibleAndPrivateDeleteReaddPreservesExactKey()
    {
        await using var f = await Fixture.Create();
        var key = Enumerable.Repeat((byte)0x53, 16).ToArray();
        var entry = await f.Service.ImportCustomAsync(f.Owner.NodeId, "Команда", "$team", key, Token);
        await f.Service.SetArchivedAsync(f.Owner.NodeId, entry.Id, true, Token);
        f.Vm.SetActive(true); await f.Vm.RefreshAsync();
        var row = Assert.Single(f.Vm.Rows); Assert.Equal("$team", row.Name); Assert.Equal("−", row.Symbol);
        await row.ToggleCommand.ExecuteAsync(null); Assert.Equal("+", row.Symbol); Assert.Single(f.Vm.Rows);
        await row.ToggleCommand.ExecuteAsync(null); Assert.Equal("−", row.Symbol);
        var restored = Assert.Single(await f.Service.GetRegionsAsync(f.Owner.NodeId, true, Token));
        Assert.Equal(key, restored.Definition.Key.ToArray()); Assert.Equal(RegionKeyKind.Custom, restored.Definition.Kind);
        Assert.Equal("Команда", restored.Label); Assert.NotEqual(entry.Id, restored.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionChangeCancelsSearchAndPreservesPreviouslyReceivedRows(bool replacement)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var f = await Fixture.Create(async (owner, progress, token) =>
        {
            var partial = Result(owner, Public("ru-ros"));
            progress!.Report(new(partial.RunId, RegionDiscoveryPhase.Requesting, 1, 2, Names: partial.Names, Owner: partial.Owner));
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Result(owner, Public("late"));
        });
        f.Vm.SetActive(true); await f.Vm.RefreshAsync();
        var search = f.Vm.SearchCommand.ExecuteAsync(null); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3), Token);
        f.ActiveOwner = replacement ? new(f.Owner.NodeId, Guid.NewGuid(), 2, "A", null, null) : null;
        f.Vm.NotifyAvailability(); await search;
        Assert.False(f.Vm.IsSearching); Assert.Equal("ru-ros", Assert.Single(f.Vm.Rows).Name);
        Assert.Equal(replacement, f.Vm.SearchCommand.CanExecute(null));
    }

    [Fact]
    public async Task FailedWriteLeavesPlusAndReportsErrorWithoutLosingCandidate()
    {
        await using var f = await Fixture.Create((owner, _, _) => Task.FromResult(Result(owner, Public("ru-ros"))));
        f.Vm.SetActive(true); await f.Vm.RefreshAsync(); await f.Vm.SearchCommand.ExecuteAsync(null);
        var errors = new List<string>(); f.Vm.ErrorOccurred += errors.Add;
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = DesktopAppPaths.CreateForDirectory(f.Path).DatabasePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync(Token); using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER RefuseRegion BEFORE INSERT ON Regions BEGIN SELECT RAISE(ABORT,'injected region failure'); END;";
            await command.ExecuteNonQueryAsync(Token);
        }
        var row = Assert.Single(f.Vm.Rows); await row.ToggleCommand.ExecuteAsync(null);
        Assert.Equal("+", row.Symbol); Assert.Single(f.Vm.Rows); Assert.Single(errors);
        Assert.Empty(await f.Service.GetRegionsAsync(f.Owner.NodeId, true, Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelKeepsPartialResultsAndBlocksNewSearchUntilCleanupButLeavingDiscardsCandidates(bool leave)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var f = await Fixture.Create(async (owner, progress, token) =>
        {
            var result = Result(owner, Public("ru-ros"));
            progress!.Report(new(result.RunId, RegionDiscoveryPhase.Requesting, 1, 2, Names: result.Names, Owner: result.Owner));
            received.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            cleanup.TrySetResult(); await release.Task;
            return result with { Cancelled = true };
        });
        f.Vm.SetActive(true); await f.Vm.RefreshAsync();
        var search = f.Vm.SearchCommand.ExecuteAsync(null);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(3), Token);
        Assert.Equal("ru-ros", Assert.Single(f.Vm.Rows).Name);
        f.Vm.CancelCommand.Execute(null); await cleanup.Task.WaitAsync(TimeSpan.FromSeconds(3), Token);
        Assert.True(f.Vm.IsSearching); Assert.False(f.Vm.SearchCommand.CanExecute(null)); Assert.False(f.Vm.CancelCommand.CanExecute(null));
        if (leave)
        {
            f.Vm.SetActive(false); f.Vm.SetActive(true); await f.Vm.RefreshAsync(); Assert.Empty(f.Vm.Rows);
            Assert.False(f.Vm.SearchCommand.CanExecute(null));
        }
        release.TrySetResult(); await search;
        Assert.False(f.Vm.IsSearching); Assert.True(f.Vm.SearchCommand.CanExecute(null));
        if (leave) Assert.Empty(f.Vm.Rows); else Assert.Single(f.Vm.Rows);
        Assert.Empty(await f.Service.GetRegionsAsync(f.Owner.NodeId, true, Token));
    }
}
