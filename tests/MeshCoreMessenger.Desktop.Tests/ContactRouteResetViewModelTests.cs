using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Desktop.ViewModels;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed class ContactRouteResetViewModelTests
{
    [Fact]
    public async Task OfflineAndStaleAvailabilityCannotEnableRouteReset()
    {
        var model = new ContactRouteResetViewModel();
        var service = new Service();
        ContactRouteResetRequest? selected = null;
        model.Configure(() => selected, service, _ => Task.CompletedTask, task => task, TestContext.Current.CancellationToken);
        await model.RefreshAsync();
        Assert.False(model.Command.CanExecute(null)); Assert.Equal(0, service.Checks);
        selected = Target();
        var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Availability = () => gate.Task;
        var checking = model.RefreshAsync();
        selected = Target(); model.Invalidate();
        gate.SetResult(null); await checking;
        Assert.False(model.CanReset);
        service.Availability = () => Task.FromResult<string?>("ACK pending");
        await model.RefreshAsync();
        Assert.False(model.Command.CanExecute(null)); Assert.Equal("ACK pending", model.AvailabilityMessage);
    }

    [Fact]
    public async Task StableMenuCommandResetsOnlyCapturedContactAndNeverDuplicatesBusyOperation()
    {
        var model = new ContactRouteResetViewModel(); var selected = Target(); var original = selected;
        var gate = new TaskCompletionSource<ContactRouteResetResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new Service { Reset = _ => gate.Task };
        var commits = 0;
        model.Configure(() => selected, service, _ => { commits++; return Task.CompletedTask; }, task => task, TestContext.Current.CancellationToken);
        var menu = new ConversationMenuViewModel(MessengerNavigationTab.Personal,
            new(() => null, null, _ => Task.CompletedTask), model);
        var item = menu.Items.Single(i => i.Action == ConversationMenuAction.ResetRoute);
        await menu.RefreshAsync();
        Assert.Equal("Сбросить маршрут", item.Header); Assert.True(item.Command.CanExecute(null));
        var resetting = model.Command.ExecuteAsync(null);
        Assert.True(model.IsBusy); Assert.False(item.Command.CanExecute(null));
        await model.Command.ExecuteAsync(null);
        Assert.Single(service.Requests);
        selected = Target(); model.Invalidate();
        gate.SetResult(new(original.NodeId, original.PublicKey, true, null)); await resetting;
        var captured = Assert.Single(service.Requests);
        Assert.Equal(original.NodeId, captured.NodeId); Assert.Equal(original.SessionId, captured.SessionId);
        Assert.Equal(original.Generation, captured.Generation); Assert.Equal(original.PublicKey.ToArray(), captured.PublicKey.ToArray());
        Assert.Null(model.StatusMessage); Assert.Equal(1, commits);
        Assert.Same(model.Command, item.Command);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmedResetReportsLocalFailureWithoutRetry(bool failedUiRefresh)
    {
        var model = new ContactRouteResetViewModel(); var target = Target();
        var service = new Service { Reset = request => Task.FromResult(new ContactRouteResetResult(request.NodeId, request.PublicKey,
            failedUiRefresh, failedUiRefresh ? null : "Маршрут сброшен. Локальные данные недоступны.")) };
        model.Configure(() => target, service, _ => throw new IOException("UI refresh"), task => task, TestContext.Current.CancellationToken);
        await model.RefreshAsync(); await model.Command.ExecuteAsync(null);
        Assert.StartsWith("Маршрут сброшен.", model.StatusMessage);
        Assert.Single(service.Requests); Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task SelectionChangeBeforeClickDoesNotInvokePreparedRecipient()
    {
        var selected = Target(); var model = new ContactRouteResetViewModel(); var service = new Service();
        model.Configure(() => selected, service, _ => Task.CompletedTask, task => task, TestContext.Current.CancellationToken);
        await model.RefreshAsync(); selected = Target();
        await model.Command.ExecuteAsync(null);
        Assert.Empty(service.Requests);
    }

    [Theory]
    [InlineData(null, "данные не получены")]
    [InlineData((byte)255, "неизвестен · flood")]
    [InlineData((byte)0, "напрямую")]
    [InlineData((byte)0x42, "хопов: 2, 2-байтовые хеши · 11111111")]
    [InlineData((byte)0x82, "хопов: 2, 3-байтовые хеши · 111111111111")]
    [InlineData((byte)0xC1, "некорректные данные")]
    public void RouteFormattingUsesEncodedDescriptorRatherThanStalePathBytes(byte? descriptor, string expected) =>
        Assert.Equal(expected, ContactRouteFormatter.Format(descriptor, Enumerable.Repeat((byte)0x11, 64).ToArray()));

    private static ContactRouteResetRequest Target() => new(Guid.NewGuid(), Guid.NewGuid(), 1, Enumerable.Repeat((byte)9, 32).ToArray());
    private sealed class Service : IContactRouteService
    {
        public int Checks { get; private set; }
        public Func<Task<string?>> Availability { get; set; } = () => Task.FromResult<string?>(null);
        public Func<ContactRouteResetRequest, Task<ContactRouteResetResult>> Reset { get; init; } = request => Task.FromResult(new ContactRouteResetResult(request.NodeId, request.PublicKey, true, null));
        public List<ContactRouteResetRequest> Requests { get; } = [];
        public Task<string?> GetUnavailableReasonAsync(ContactRouteResetRequest request, CancellationToken cancellationToken = default) { Checks++; return Availability(); }
        public Task<ContactRouteResetResult> ResetAsync(ContactRouteResetRequest request, CancellationToken cancellationToken = default) { Requests.Add(request); return Reset(request); }
    }
}
