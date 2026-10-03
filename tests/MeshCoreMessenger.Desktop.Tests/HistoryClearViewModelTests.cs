using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Desktop.ViewModels;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed class HistoryClearViewModelTests
{
    private static HistoryClearTarget Target() => new(Guid.NewGuid(), Guid.NewGuid(), "Node", "Chat");

    [Fact]
    public async Task TargetIsCapturedBeforeConfirmationAndIsNeverRetargetedAfterSelectionChange()
    {
        var selected = Target(); var original = selected;
        var service = new Service();
        var model = new HistoryClearViewModel(() => selected, service, _ => Task.CompletedTask);
        await model.RefreshAsync();
        var capture = model.Capture()!;
        selected = Target();
        Assert.Null(model.Capture());
        await model.ClearAsync(capture);
        Assert.Equal((original.NodeId, original.ConversationId), Assert.Single(service.Cleared));
    }

    [Fact]
    public async Task OldAvailabilityResultCannotEnableClearForAnotherSelection()
    {
        var selected = Target(); var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new Service { Availability = () => gate.Task };
        var model = new HistoryClearViewModel(() => selected, service, _ => Task.CompletedTask);
        var refreshing = model.RefreshAsync(); selected = Target();
        gate.SetResult(null); await refreshing;
        Assert.False(model.CanClear); Assert.Null(model.Capture());
        service.Availability = () => Task.FromResult<string?>("ACK pending");
        await model.RefreshAsync();
        Assert.False(model.CanClear); Assert.Equal("ACK pending", model.AvailabilityMessage);
    }

    [Fact]
    public async Task BusyClearCannotRunTwiceAndCommitRefreshFailureReportsThatDeletionSucceeded()
    {
        var target = Target(); var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new Service { ClearGate = gate };
        var model = new HistoryClearViewModel(() => target, service, _ => throw new IOException("refresh"));
        await model.RefreshAsync(); var deleting = model.ClearAsync(model.Capture()!);
        Assert.True(model.IsBusy); Assert.False(model.CanClear);
        await model.ClearAsync(target); Assert.Single(service.Cleared);
        gate.SetResult(); await deleting;
        Assert.False(model.IsBusy); Assert.StartsWith("История удалена.", model.ErrorMessage);
    }

    private sealed class Service : IHistoryClearService
    {
        public Func<Task<string?>> Availability { get; set; } = () => Task.FromResult<string?>(null);
        public TaskCompletionSource? ClearGate { get; init; }
        public List<(Guid, Guid)> Cleared { get; } = [];
        public Task<string?> GetUnavailableReasonAsync(Guid nodeId, Guid conversationId, CancellationToken cancellationToken = default) => Availability();
        public async Task<HistoryClearResult> ClearAsync(Guid nodeId, Guid conversationId, CancellationToken cancellationToken = default)
        {
            Cleared.Add((nodeId, conversationId));
            if (ClearGate is not null) await ClearGate.Task;
            return new(nodeId, conversationId, 42, 2);
        }
    }
}
