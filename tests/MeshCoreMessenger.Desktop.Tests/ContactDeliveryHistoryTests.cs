using Microsoft.Extensions.Logging.Abstractions;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.ViewModels;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed class ContactDeliveryHistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CapturedIdentityPaginationAndRefreshKeepDeliveriesSeparateFromEvidence()
    {
        var node = Guid.NewGuid(); var key = new byte[32]; key[0] = 7;
        var savedKey = key.ToArray();
        var first = Delivery(node, savedKey);
        first = first with { Evidence = [first.Evidence[0], first.Evidence[0] with { Id = Guid.NewGuid(), AckTag = 99 }] };
        var second = Delivery(node, savedKey);
        var cursor = new ContactDeliveryCursor(node, savedKey, Now, first.Id);
        var calls = 0;
        var reader = new Reader((n, k, limit, before, _) =>
        {
            Assert.Equal(node, n); Assert.Equal(savedKey, k.ToArray()); Assert.Equal(25, limit);
            calls++;
            if (calls == 2) { Assert.Equal(cursor, before); return Task.FromResult(new ContactDeliveryPage([second], null)); }
            Assert.Null(before); return Task.FromResult(new ContactDeliveryPage([first], cursor));
        });
        using var vm = Create(node, key, reader);
        key[0] = 88; // selection buffers cannot retarget an open card.
        await vm.RefreshAsync();
        Assert.Equal(2, vm.Rows.Count); Assert.Equal("Доставок: 1 · подтверждений: 2", vm.Summary);
        Assert.True(vm.HasMore);
        await vm.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.Rows.Count); Assert.Equal("Доставок: 2 · подтверждений: 3", vm.Summary);
        Assert.False(vm.HasMore);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Rows.Count); Assert.True(vm.HasMore);
    }

    [Fact]
    public void AmbiguousLateAckShowsEveryCandidateAndDistinctRouteProvenance()
    {
        var delivery = Delivery(Guid.NewGuid(), new byte[32]) with { WasLate = true };
        var evidence = delivery.Evidence[0] with
        {
            Attribution = DeliveryAttribution.MultipleCandidates,
            LearnedRoute = new PrivateRouteSnapshot(0x41, new byte[] { 0x09, 0x79 }, Now),
            Candidates = [Candidate(1, new PrivateRouteSnapshot(0, ReadOnlyMemory<byte>.Empty, Now)), Candidate(4, new PrivateRouteSnapshot(255, ReadOnlyMemory<byte>.Empty, Now))],
        };
        var row = new ContactDeliveryRowViewModel(delivery, evidence);
        Assert.Contains("Попытка 1\ndirect", row.ConfiguredRoutes);
        Assert.Contains("Попытка 4\nшироковещательный", row.ConfiguredRoutes);
        Assert.Equal("1 хоп\n0979", row.LearnedRoute);
        Assert.Contains("Несколько", row.Attribution); Assert.Contains("поздний ACK", row.Attribution);
        Assert.Contains("13:00:00.000 +03:00", row.AckTime);
        Assert.Equal("250 мс", row.RoundTrip);
        Assert.Contains("timestamp=123, attempt=0", row.Diagnostics);
    }

    [Fact]
    public void LegacyDeliveryDoesNotInventAttemptRouteOrTiming()
    {
        var delivery = Delivery(Guid.NewGuid(), new byte[32]);
        var row = new ContactDeliveryRowViewModel(delivery, delivery.Evidence[0] with
        { Attribution = DeliveryAttribution.Unknown, Candidates = [], LearnedRoute = null, RoundTripMilliseconds = null });
        Assert.Equal("Попытка неизвестна", row.Attribution);
        Assert.Equal("Нет снимка", row.ConfiguredRoutes); Assert.Equal("Нет снимка", row.LearnedRoute);
        Assert.Equal("—", row.RoundTrip);
    }

    [Fact]
    public async Task FailedPaginationPreservesRowsAndCursorForRetry()
    {
        var node = Guid.NewGuid(); var key = new byte[32]; var delivery = Delivery(node, key);
        var cursor = new ContactDeliveryCursor(node, key, Now, delivery.Id);
        var calls = 0;
        using var vm = Create(node, key, new Reader((_, _, _, before, _) => ++calls switch
        {
            1 => Task.FromResult(new ContactDeliveryPage([delivery], cursor)),
            2 => Task.FromException<ContactDeliveryPage>(new IOException("disk")),
            _ => ReadLast(before),
        }));
        Task<ContactDeliveryPage> ReadLast(ContactDeliveryCursor? before)
        { Assert.Equal(cursor, before); return Task.FromResult(new ContactDeliveryPage([], null)); }
        await vm.RefreshAsync(); await vm.LoadMoreCommand.ExecuteAsync(null);
        Assert.True(vm.HasError); Assert.Single(vm.Rows); Assert.True(vm.HasMore); Assert.False(vm.IsLoading);
        await vm.LoadMoreCommand.ExecuteAsync(null);
        Assert.False(vm.HasError); Assert.Single(vm.Rows); Assert.False(vm.HasMore);
    }

    [Fact]
    public async Task OlderReadAndClosedCardCannotPublishLateHistory()
    {
        var node = Guid.NewGuid(); var key = new byte[32];
        var old = new TaskCompletionSource<ContactDeliveryPage>(); var closing = new TaskCompletionSource<ContactDeliveryPage>();
        var calls = 0; CancellationToken captured = default;
        var vm = Create(node, key, new Reader((_, _, _, _, token) =>
        {
            captured = token;
            return ++calls switch { 1 => old.Task, 2 => Task.FromResult(new ContactDeliveryPage([], null)), _ => closing.Task };
        }));
        var load = vm.RefreshAsync(); await vm.RefreshAsync();
        old.SetResult(new ContactDeliveryPage([Delivery(node, key)], null)); await load;
        Assert.Empty(vm.Rows); Assert.True(vm.IsEmpty);
        load = vm.RefreshAsync(); vm.Dispose(); Assert.True(captured.IsCancellationRequested);
        closing.SetResult(new ContactDeliveryPage([Delivery(node, key)], null)); await load;
        Assert.Empty(vm.Rows); Assert.False(vm.RefreshCommand.CanExecute(null));
    }

    private static ContactDeliveryCandidate Candidate(int number, PrivateRouteSnapshot route) =>
        new(number, Guid.NewGuid(), 1, 123, 0, PrivateDeliveryPhase.KnownRoute, Now, 180, "Europe/Moscow", route, false);
    private static ContactDeliveryRecord Delivery(Guid node, byte[] key) =>
        new(Guid.NewGuid(), node, key, null, null, Now, 180, "Europe/Moscow", false, DeliveryAttribution.SingleAttempt,
            [new(Guid.NewGuid(), 42, Now, 250, DeliveryAttribution.SingleAttempt, null,
                [Candidate(1, new PrivateRouteSnapshot(255, ReadOnlyMemory<byte>.Empty, Now))])]);
    private static ContactDeliveryHistoryViewModel Create(Guid node, byte[] key, IContactDeliveryHistoryReader reader) =>
        new(node, key, reader, new InlineDispatcher(), NullLogger.Instance, task => task);
    private sealed class InlineDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); action(); return Task.CompletedTask; }
    }
    private sealed class Reader(Func<Guid, ReadOnlyMemory<byte>, int, ContactDeliveryCursor?, CancellationToken, Task<ContactDeliveryPage>> read) : IContactDeliveryHistoryReader
    {
        public Task<ContactDeliveryPage> GetPageAsync(Guid nodeId, ReadOnlyMemory<byte> contactPublicKey, int limit = 50,
            ContactDeliveryCursor? before = null, CancellationToken cancellationToken = default) => read(nodeId, contactPublicKey, limit, before, cancellationToken);
    }
}
