using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreSharp;
using MeshCoreSharp.Transport.Ble;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed partial class SessionCommandGatewayTests
{
    [Fact]
    public async Task SessionBudgetIsLiveNodeScopedAndClearedWhenAdmissionCloses()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Clients.Configure = client => client.TransportTextLimit = 144;
        await fixture.Supervisor.ConnectNowAsync(CancellationToken);
        await fixture.Online();
        var nodeId = fixture.NodeId;
        Assert.Null(fixture.Gateway.GetTextByteLimit(Guid.NewGuid(), false));
        await using (var lease = fixture.Acquire())
        {
            Assert.Equal(144, lease.MaximumTextBytes);
            Assert.Equal(144, fixture.Gateway.GetTextByteLimit(fixture.NodeId, false));
            fixture.Clients.Current!.TransportTextLimit = 7;
            Assert.Equal(7, lease.MaximumTextBytes);
            Assert.Equal(7, fixture.Gateway.GetTextByteLimit(fixture.NodeId, true));
            Assert.Equal(0, fixture.Clients.Current.Tx);
        }
        await fixture.Supervisor.DisconnectAsync(CancellationToken);
        Assert.Null(fixture.Gateway.GetTextByteLimit(nodeId, false));
    }

    [Theory]
    [InlineData(23, false, 7)]
    [InlineData(160, false, 144)]
    [InlineData(176, false, 160)]
    [InlineData(176, true, 148)]
    public void ProcessorAppliesSourceDerivedBleBudgetWithoutChangingProtocolLimit(int mtu, bool channel, int expected)
    {
        const string name = "0123456789";
        var budget = BleTextBudget.Calculate((ushort)mtu, channel ? BleTextMessageKind.Channel : BleTextMessageKind.Private, name);
        var processor = new PassthroughOutgoingTextProcessor();
        var context = new OutgoingTextContext(channel, name, budget.MaximumTextBytes);
        var exact = new string('x', expected);
        Assert.True(processor.Process(exact, context, new()).Validation.IsValid);
        var longer = processor.Process(exact + "x", context, new());
        Assert.False(longer.Validation.IsValid);
        Assert.Equal(exact + "x", longer.TransmissionText);
        var original = processor.Process(exact, new(channel, name), new());
        Assert.Equal(channel ? TextMessageValidator.GetChannelTextLimit(name) : 160, original.Validation.MaxUtf8Bytes);
    }
}
