using CommunityToolkit.Mvvm.Input;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Desktop.Presentation;
using MeshCoreMessenger.Desktop.ViewModels;
using Xunit;
using MeshCoreMessenger.Core.Application;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed class SendPresentationTests
{
    [Fact]
    public void ExpiringExplanationLeavesCounterAndMultipleReasonsUseBullets()
    {
        var composer = new ComposerViewModel
        {
            Text = new string('x', 42), Readiness = SendReadiness.Offline, PreviewExplanation = "Черновик не сохранён",
        };
        Assert.Equal("42 / 160 байт - offline • Черновик не сохранён", composer.StatusLine);
        composer.Readiness = SendReadiness.Ready;
        composer.PreviewExplanation = string.Empty;
        Assert.Equal("42 / 160 байт", composer.StatusLine);
        composer.Text = "👋";
        Assert.False(composer.CanSend);
    }

    [Fact]
    public void ExistingHistoryDoesNotAcquireInventedDeliveryMetadata()
    {
        foreach (var direction in Enum.GetValues<MessageDirection>())
        {
            var message = Item(direction);
            Assert.Equal(message.ReceivedTime, message.MetadataText);
            Assert.False(message.HasDetails);
            Assert.False(message.RetryVisible);
            Assert.False(message.CanRetry);
            Assert.False(message.IsSendError);
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void RetryFlagsIndependentlyPreventInvocation(bool visible, bool enabled)
    {
        var calls = 0;
        var message = Item(MessageDirection.Outgoing);
        message.Presentation = new(MessageSendDisplayState.Failed, RetryVisible: visible, RetryEnabled: enabled,
            RetryCommand: new RelayCommand(() => calls++));
        message.RequestRetry();
        Assert.Equal(0, calls);
        Assert.Equal(visible, message.RetryVisible);
        Assert.False(message.CanRetry);
    }

    [Fact]
    public void ConfirmationTargetCanLoseAdmissionAndRetryUpdatesSameMessageOnlyOnExplicitRequest()
    {
        var calls = 0;
        var message = Item(MessageDirection.Outgoing);
        var id = message.Id;
        var command = new RelayCommand(() =>
        {
            calls++;
            message.Presentation = message.Presentation with { State = MessageSendDisplayState.Sending, AttemptNumber = 2 };
        });
        message.Presentation = new(MessageSendDisplayState.Failed, RetryVisible: true, RetryEnabled: true, RetryCommand: command);
        Assert.Equal(0, calls); // displaying the error does not invoke anything
        message.Presentation = message.Presentation with { RetryEnabled = false };
        message.RequestRetry();
        Assert.Equal(0, calls); // stale confirmation must recheck business availability
        message.Presentation = message.Presentation with { RetryEnabled = true };
        message.RequestRetry();
        Assert.Equal(1, calls);
        Assert.Equal(id, message.Id);
        Assert.Contains("попытка 2", message.MetadataText);
        Assert.False(message.IsSendError);
        Assert.Equal("Исходный @[текст] 👋", message.CopyText);
    }

    [Fact]
    public void IncomingMetadataCannotExposeRetryOrTurnBubbleIntoSendError()
    {
        var calls = 0;
        var message = Item(MessageDirection.Incoming);
        message.Presentation = new(MessageSendDisplayState.Failed, Hops: 5,
            RetryVisible: true, RetryEnabled: true, RetryCommand: new RelayCommand(() => calls++));
        message.RequestRetry();
        Assert.Equal(0, calls);
        Assert.False(message.RetryVisible);
        Assert.False(message.IsSendError);
        Assert.Contains("Хопов: 5", message.MetadataText);
    }

    private static HistoryMessageListItem Item(MessageDirection direction) => new(new HistoryMessage(
        Guid.NewGuid(), 1, Guid.NewGuid(), direction, StoredMessageKind.Text,
        "Исходный @[текст] 👋", DateTimeOffset.UnixEpoch));
}
