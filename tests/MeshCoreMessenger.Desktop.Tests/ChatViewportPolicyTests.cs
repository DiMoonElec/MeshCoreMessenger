using MeshCoreMessenger.Desktop.Views.Chat;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed class ChatViewportPolicyTests
{
    [Fact]
    public void CacheAndInvalidContainersAreNotViewedButPartiallyVisibleMessagesAre()
    {
        RealizedMessageBounds[] containers = [new(-1, 0, 10), new(0, -100, 100), new(1, -5, 20),
            new(2, 15, 20), new(3, 35, 80), new(4, 100, 20), new(5, 0, 0), new(99, 0, 10)];
        Assert.Equal([1, 2, 3], ChatViewportPolicy.VisibleIndices(containers, 100, 6));
        Assert.Empty(ChatViewportPolicy.VisibleIndices(containers, 0, 6));
    }

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    public void HiddenDetachedOrInactiveChatCannotReportRead(bool attached, bool visible, bool active, bool expected) =>
        Assert.Equal(expected, ChatViewportPolicy.CanReportRead(attached, visible, active));

    [Fact]
    public void QueuedScrollIsRejectedAfterContextChangeHideOrDetach()
    {
        Assert.True(ChatViewportPolicy.CanApplyCallback(2, 2, true, true));
        Assert.False(ChatViewportPolicy.CanApplyCallback(2, 3, true, true));
        Assert.False(ChatViewportPolicy.CanApplyCallback(2, 2, true, false));
        Assert.False(ChatViewportPolicy.CanApplyCallback(2, 2, false, true));
    }
}
