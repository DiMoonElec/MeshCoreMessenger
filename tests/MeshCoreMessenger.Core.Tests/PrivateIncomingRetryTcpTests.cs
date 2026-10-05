using System.Buffers.Binary;
using System.Text;
using MeshCoreMessenger.Core.Domain;
using MeshCoreSharp.Protocol;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed partial class SessionCommandGatewayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrivateIncomingRetriesThroughCompanionDrainKeepOneBubblePerTimestamp(bool v3)
    {
        await using var f = await TcpPrivateFixture.CreateAsync(autoAck: false);
        await f.Ingress.FlushAsync(CancellationToken);
        var key = Enumerable.Repeat((byte)0xA1, 32).ToArray();
        var draft = await f.Storage.Drafts.SaveAsync(new(f.Node, null, ConversationKind.Contact, key), "draft", DateTimeOffset.UtcNow, CancellationToken);
        var conversation = draft!.ConversationId;
        var before = await f.Storage.ReadStates.GetAsync(f.Node, conversation, CancellationToken);
        const string text = "P7 TCP повтор 👋";
        byte[] Frame(uint timestamp, byte path, byte snr)
        {
            var time = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(time, timestamp);
            return [(byte)(v3 ? PacketType.ContactMessageReceivedV3 : PacketType.ContactMessageReceived),
                .. (v3 ? new byte[] { snr, 0, 0 } : Array.Empty<byte>()), .. key[..6], path, 0, .. time, .. Encoding.UTF8.GetBytes(text)];
        }
        await f.Server.QueueIncomingMessageAsync(Frame(1_700_000_500, 0, 4));
        await f.Server.QueueIncomingMessageAsync(Frame(1_700_000_500, 3, 12));
        await f.Server.QueueIncomingMessageAsync(Frame(1_700_000_501, 1, 20));
        await Until(() =>
        {
            using var connection = MeshCoreMessenger.Core.Persistence.Sqlite.SqliteDatabase.CreateConnection(f.Paths.DatabasePath, Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly);
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM IncomingMessageEvents e JOIN Messages m ON m.Id=e.MessageId WHERE m.Text=$text;";
            command.Parameters.AddWithValue("$text", text);
            return Task.FromResult((long)command.ExecuteScalar()! == 3);
        });
        await f.Ingress.FlushAsync(CancellationToken);
        var rows = await f.Storage.History.GetMessagesAsync(f.Node, conversation, null, 20, CancellationToken);
        Assert.Equal(2, rows.Count(m => m.Text == text));
        Assert.Equal(before.UnreadCount + 2, (await f.Storage.ReadStates.GetAsync(f.Node, conversation, CancellationToken)).UnreadCount);
        Assert.Empty(f.Server.PrivateTransmissions);
    }
}
