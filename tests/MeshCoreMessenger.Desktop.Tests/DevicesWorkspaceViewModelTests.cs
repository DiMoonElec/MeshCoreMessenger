using Microsoft.Extensions.Logging.Abstractions;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.ViewModels;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed class DevicesWorkspaceViewModelTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private static byte[] Key(int value) => Enumerable.Repeat((byte)value, 32).ToArray();
    private static ContactDetailsProjection Detail(Guid node, int key, string name = "Устройство 🐈", int type = 2) =>
        new(node, Key(key), name, type, 0x81, new byte[64], null, true, null, 0, 0, DateTimeOffset.UnixEpoch);
    private static ConversationDirectoryEntry Row(Guid node, int key, string name = "Устройство 🐈", int type = 2) =>
        new(node, $"contact:{Convert.ToHexString(Key(key))}", ConversationDirectorySection.ServiceContacts,
            ConversationKind.Contact, Key(key), null, name, type, true, null, [], false,
            DateTimeOffset.UnixEpoch, 0, DateTimeOffset.UnixEpoch, null, null, null, null, null, null);
    private static DevicesWorkspaceViewModel Create(Reader reader, ISearchDelay? delay = null) =>
        new(reader, new InlineDispatcher(), delay ?? new ImmediateDelay(), NullLogger.Instance);

    [Theory]
    [InlineData(0, "Без типа")]
    [InlineData(2, "Ретранслятор")]
    [InlineData(3, "Комната")]
    [InlineData(4, "Датчик")]
    [InlineData(99, "Устройство (тип 99)")]
    public void FormatsTypesAndSavedFactsWithoutGuessing(int type, string label)
    {
        var vm = new DeviceDetailsViewModel(Detail(A, 1, "Кириллица 🌡️" + new string('я', 300), type) with { PresentOnNode = false });
        Assert.Equal(label, vm.Type);
        Assert.StartsWith("Кириллица", vm.Name);
        Assert.Equal(64, vm.PublicKey.Length);
        Assert.Contains("Сохранено локально", vm.Presence);
        Assert.Equal("Нет сохранённых данных", vm.LastAdvert);
        Assert.Equal("Нет сохранённых данных", vm.RawAdvert);
        Assert.Equal(128, vm.RawRoute.Length); // Full wire field, never a guessed hop count.
        Assert.Contains("0,0", vm.CoordinatesNote);
        Assert.Equal("0x81 (129)", vm.Flags);
    }

    [Fact]
    public void MissingFieldsAndRealCoordinatesAreDistinct()
    {
        var missing = new DeviceDetailsViewModel(Detail(A, 1, "") with { Latitude = null, Longitude = null, OutPath = null });
        Assert.Equal("Устройство без имени", missing.Name);
        Assert.Equal("Нет сохранённых данных", missing.Coordinates);
        Assert.Equal("Нет сохранённых данных", missing.RawRoute);
        var known = new DeviceDetailsViewModel(Detail(A, 1) with { Latitude = 55.75, Longitude = 37.61 });
        Assert.Equal("55.75000, 37.61000", known.Coordinates);
        Assert.Empty(known.CoordinatesNote);
    }

    [Fact]
    public async Task PaginationSearchAndRefreshKeepFullKeySelectionWithoutDuplicates()
    {
        var reader = new Reader();
        reader.Rows[A] = Enumerable.Range(1, 110).Select(key => Row(A, key, "Одинаковое имя")).ToArray();
        var vm = Create(reader); vm.SetNode(A, "A");
        await vm.RefreshAsync(TestContext.Current.CancellationToken); Assert.Equal(50, vm.Items.Count);
        await vm.LoadMoreCommand.ExecuteAsync(null); Assert.Equal(100, vm.Items.Count);
        await vm.LoadMoreCommand.ExecuteAsync(null); Assert.Equal(110, vm.Items.Count);
        Assert.False(vm.CanLoadMore);
        reader.Details = (node, key, _) => Task.FromResult<ContactDetailsProjection?>(Detail(node, key.Span[0]));
        await vm.SelectAsync(vm.Items[90]); var selectedKey = vm.Selected!.Key;
        reader.Rows[A] = reader.Rows[A].Reverse().ToArray();
        await vm.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(selectedKey, vm.Selected!.Key);
        Assert.Equal(110, vm.Items.Select(item => item.Key).Distinct().Count());
        vm.SearchText = "Нет такого";
        await vm.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.True(vm.IsEmpty); Assert.Null(vm.Details); Assert.Null(vm.Selected);
        Assert.Equal("Устройства не найдены", vm.EmptyText);
        Assert.All(reader.Limits, limit => Assert.Equal(50, limit));
        await vm.StopAsync();
    }

    [Fact]
    public async Task SelectedRowIsPinnedWhenActivityMovesItBeyondLoadedPage()
    {
        var reader = new Reader(); reader.Rows[A] = Enumerable.Range(1, 60).Select(key => Row(A, key)).ToArray();
        reader.Details = (node, key, _) => Task.FromResult<ContactDetailsProjection?>(Detail(node, key.Span[0]));
        var vm = Create(reader); vm.SetNode(A, "A"); await vm.RefreshAsync(TestContext.Current.CancellationToken);
        await vm.SelectAsync(vm.Items[0]); var key = vm.Selected!.Key;
        reader.Rows[A] = reader.Rows[A].Skip(1).Concat(reader.Rows[A].Take(1)).ToArray();
        await vm.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Contains(vm.Items, item => item.Key == key);
        await vm.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(60, vm.Items.Count); Assert.Equal(60, vm.Items.Select(item => item.Key).Distinct().Count());
        await vm.StopAsync();
    }

    [Fact]
    public async Task SameNamesAndSameKeyPrefixRemainDifferentDevices()
    {
        var first = Key(7); var second = Key(7); first[^1] = 1; second[^1] = 2;
        var reader = new Reader();
        reader.Rows[A] = [Row(A, 7, "Same") with { Identity = first }, Row(A, 7, "Same") with { Identity = second }];
        reader.Details = (node, key, _) => Task.FromResult<ContactDetailsProjection?>(Detail(node, 0) with { PublicKey = key.ToArray() });
        var vm = Create(reader); vm.SetNode(A, "A"); await vm.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, vm.Items.Count); Assert.NotEqual(vm.Items[0].Key, vm.Items[1].Key);
        await vm.SelectAsync(vm.Items[0]); Assert.Equal(Convert.ToHexString(first).ToLowerInvariant(), vm.Details!.PublicKey);
        await vm.SelectAsync(vm.Items[1]); Assert.Equal(Convert.ToHexString(second).ToLowerInvariant(), vm.Details!.PublicKey);
        await vm.StopAsync();
    }

    [Fact]
    public async Task LateSelectionAndPreviousNodeResultsCannotReplaceCurrentCard()
    {
        var reader = new Reader(); reader.Rows[A] = [Row(A, 1), Row(A, 2)]; reader.Rows[B] = [Row(B, 1)];
        var old = new TaskCompletionSource<ContactDetailsProjection?>(TaskCreationOptions.RunContinuationsAsynchronously);
        reader.Details = (node, key, _) => node == A && key.Span[0] == 1 ? old.Task : Task.FromResult<ContactDetailsProjection?>(Detail(node, key.Span[0], node == B ? "B" : "A2"));
        var vm = Create(reader); vm.SetNode(A, "A"); await vm.RefreshAsync(TestContext.Current.CancellationToken);
        var late = vm.SelectAsync(vm.Items[0]);
        await vm.SelectAsync(vm.Items[1]); Assert.Equal("A2", vm.Details!.Name);
        vm.SetNode(B, "B"); Assert.Null(vm.Details); Assert.Empty(vm.Items);
        await vm.RefreshAsync(TestContext.Current.CancellationToken); await vm.SelectAsync(vm.Items[0]);
        old.SetResult(Detail(A, 1, "Late A")); await late;
        Assert.Equal("B", vm.Details!.Name); Assert.Equal(B, vm.Selected!.NodeId);
        await vm.StopAsync();
    }

    [Fact]
    public async Task LatePageAndDebouncedSearchCannotRestorePreviousNode()
    {
        var reader = new Reader(); reader.Rows[B] = [Row(B, 2)];
        var page = new TaskCompletionSource<ConversationDirectoryPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        reader.Page = (node, _) => node == A ? page.Task : Task.FromResult(new ConversationDirectoryPage(reader.Rows[B], null));
        var vm = Create(reader); vm.SetNode(A, "A"); var request = vm.RefreshAsync(TestContext.Current.CancellationToken);
        vm.SetNode(B, "B"); await vm.RefreshAsync(TestContext.Current.CancellationToken);
        page.SetResult(new([Row(A, 1)], null)); await request;
        Assert.Equal(B, Assert.Single(vm.Items).NodeId);
        Assert.False(vm.IsLoading); await vm.StopAsync();
    }

    [Fact]
    public async Task ErrorsAreInlineAndRetryableAndWrongOwnerIsNeverShown()
    {
        var reader = new Reader(); reader.Rows[A] = [Row(A, 1)];
        reader.Page = (_, _) => throw new IOException("read failure");
        var vm = Create(reader); vm.SetNode(A, "A"); await vm.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.True(vm.HasListError); Assert.False(vm.IsLoading);
        reader.Page = null; await vm.RefreshCommand.ExecuteAsync(null); Assert.False(vm.HasListError);
        reader.Details = (_, _, _) => Task.FromResult<ContactDetailsProjection?>(Detail(B, 1));
        await vm.SelectAsync(vm.Items[0]); Assert.True(vm.HasDetailError); Assert.Null(vm.Details);
        reader.Details = (_, _, _) => Task.FromResult<ContactDetailsProjection?>(Detail(A, 1));
        await vm.RetryDetailsCommand.ExecuteAsync(null); Assert.True(vm.HasDetails); Assert.False(vm.HasDetailError);
        await vm.StopAsync();
    }

    [Fact]
    public async Task NewSearchSupersedesLateResultsOfPreviousQuery()
    {
        var reader = new Reader();
        var old = new TaskCompletionSource<ConversationDirectoryPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        reader.Page = (_, _) => ++calls == 1 ? old.Task : Task.FromResult(new ConversationDirectoryPage([Row(A, 2, "new")], null));
        var vm = Create(reader); vm.SetNode(A, "A");
        vm.SearchText = "old"; Assert.True(vm.IsLoading);
        vm.SearchText = "new";
        Assert.Equal("new", Assert.Single(vm.Items).Name);
        old.SetResult(new([Row(A, 1, "old")], null));
        await vm.StopAsync();
        Assert.Equal("new", Assert.Single(vm.Items).Name);
    }

    [Fact]
    public async Task ShutdownDuringPageReadAndMissingCardHaveExplicitStates()
    {
        var reader = new Reader(); reader.Rows[A] = [Row(A, 1)];
        var vm = Create(reader);
        Assert.False(vm.CanRefresh); Assert.True(vm.IsEmpty);
        vm.SetNode(A, "A"); await vm.RefreshAsync(TestContext.Current.CancellationToken);
        await vm.SelectAsync(vm.Items[0]); Assert.True(vm.ShowPlaceholder); Assert.Null(vm.Details);
        reader.Page = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new([], null); };
        var pending = vm.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.True(vm.IsLoading);
        await vm.StopAsync(); await pending;
        Assert.False(vm.IsLoading); Assert.False(vm.CanRefresh);
    }

    [Fact]
    public async Task NarrowBackRetainsSelectionAndRepeatedClickDoesNotReloadCard()
    {
        var reader = new Reader(); reader.Rows[A] = [Row(A, 1)];
        reader.Details = (_, _, _) => Task.FromResult<ContactDetailsProjection?>(Detail(A, 1));
        var vm = Create(reader); vm.SetNode(A, "A"); vm.SetNarrowLayout(true); await vm.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.True(vm.IsListVisible); Assert.False(vm.IsDetailVisible);
        await vm.SelectAsync(vm.Items[0]); Assert.True(vm.CanNavigateBack); Assert.False(vm.IsListVisible);
        vm.BackCommand.Execute(null); Assert.True(vm.IsListVisible); Assert.NotNull(vm.Selected);
        await vm.SelectAsync(vm.Items[0]); Assert.Equal(1, reader.DetailCalls);
        vm.SetNarrowLayout(false); Assert.True(vm.IsListVisible); Assert.True(vm.IsDetailVisible);
        await vm.StopAsync();
    }

    [Fact]
    public async Task ShutdownCancelsReadsAndLateCommandsDoNothing()
    {
        var reader = new Reader(); reader.Rows[A] = [Row(A, 1)];
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        reader.Details = async (_, _, token) =>
        { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return null; };
        var vm = Create(reader); vm.SetNode(A, "A"); await vm.RefreshAsync(TestContext.Current.CancellationToken); var item = vm.Items[0];
        var loading = vm.SelectAsync(item); await entered.Task;
        await vm.StopAsync(); await loading;
        Assert.False(vm.IsDetailLoading); Assert.Null(vm.Details);
        await vm.RefreshAsync(TestContext.Current.CancellationToken); await vm.SelectAsync(item); await vm.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(1, reader.DetailCalls);
    }

    private sealed class InlineDispatcher : IUiDispatcher
    { public Task InvokeAsync(Action action, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); action(); return Task.CompletedTask; } }
    private sealed class ImmediateDelay : ISearchDelay
    { public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; } }
    private sealed class Reader : IConversationDirectoryReader
    {
        public Dictionary<Guid, ConversationDirectoryEntry[]> Rows { get; } = [];
        public List<int> Limits { get; } = [];
        public Func<Guid, CancellationToken, Task<ConversationDirectoryPage>>? Page { get; set; }
        public Func<Guid, ReadOnlyMemory<byte>, CancellationToken, Task<ContactDetailsProjection?>>? Details { get; set; }
        public int DetailCalls { get; private set; }
        public Task<ConversationDirectoryPage> GetPageAsync(Guid node, ConversationDirectorySection section,
            ConversationDirectoryCursor? after, int limit, CancellationToken cancellationToken = default) => Read(node, section, after, limit, null, cancellationToken);
        public Task<ConversationDirectoryPage> SearchPageAsync(Guid node, ConversationDirectorySection section,
            string query, ConversationDirectoryCursor? after, int limit, CancellationToken cancellationToken = default) => Read(node, section, after, limit, query, cancellationToken);
        private Task<ConversationDirectoryPage> Read(Guid node, ConversationDirectorySection section,
            ConversationDirectoryCursor? after, int limit, string? query, CancellationToken token)
        {
            Assert.Equal(ConversationDirectorySection.ServiceContacts, section); Limits.Add(limit);
            if (Page is not null) return Page(node, token);
            var rows = (Rows.GetValueOrDefault(node) ?? []).Where(row => query is null || row.DisplayName!.Contains(query)).ToArray();
            var offset = (int)(after?.ActivitySequence ?? 0); var entries = rows.Skip(offset).Take(limit).ToArray();
            ConversationDirectoryCursor? next = offset + entries.Length < rows.Length ? new(node, section, offset + entries.Length, DateTimeOffset.UnixEpoch, "page") : null;
            return Task.FromResult(new ConversationDirectoryPage(entries, next));
        }
        public Task<ContactDetailsProjection?> GetContactDetailsAsync(Guid node, ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
        { DetailCalls++; return Details?.Invoke(node, key, cancellationToken) ?? Task.FromResult<ContactDetailsProjection?>(null); }
        public Task<ChannelDetailsProjection?> GetChannelDetailsAsync(Guid node, ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
