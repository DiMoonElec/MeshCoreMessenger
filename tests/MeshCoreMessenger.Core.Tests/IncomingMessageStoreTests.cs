using Microsoft.Data.Sqlite;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreSharp.Models;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed partial class IncomingMessageStoreTests
{
    [Fact]
    public async Task StoresAllIncomingModelsWithProtocolMetadataAndStableChannelBinding()
    {
        await using var context = await MessageStorageContext.CreateAsync();
        var contactKey = Key(10);
        var channel = await context.Storage.Directories.ApplySnapshotAsync(
            context.Node.Id, context.Session.Id,
            [new DirectoryContactSnapshot(contactKey, "Alice", 1, 0, new byte[64], context.Now, 0, 0)],
            [new DirectoryChannelSnapshot(7, "#test", Fingerprint(20), ChannelAccessKind.PublicOrHashtag)],
            context.Now, CancellationToken);
        var binding = Assert.Single(channel.ActiveBindings);

        await context.Storage.IncomingMessages.StoreAsync(context.Envelope(new ContactMessage(
            contactKey[..6], 0xFF, MessageTextType.SignedPlain,
            DateTimeOffset.FromUnixTimeSeconds(1_700_000_001), "private", new byte[] { 1, 2, 3, 4 }, -7.5)), CancellationToken);
        await context.Storage.IncomingMessages.StoreAsync(context.Envelope(new ChannelMessage(
            7, 3, MessageTextType.CliData, DateTimeOffset.FromUnixTimeSeconds(1_700_000_002), "channel", -5), binding: binding), CancellationToken);
        await context.Storage.IncomingMessages.StoreAsync(context.Envelope(new ChannelDataMessage(
            7, 4, 0xBEEF, new byte[] { 9, 8, 7 }, -3), binding: binding), CancellationToken);

        await using var connection = OpenReadOnly(context.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MessageKind, TextType, PathLength, BinaryDataType, Text, Payload,
                   WireTimestamp, OriginalPublicKeyPrefix, OriginalSenderPrefix,
                   OriginalChannelSlot, ChannelBindingId, ResolutionState
            FROM Messages ORDER BY LocalSequence;
            """;
        using var rows = command.ExecuteReader();
        Assert.True(rows.Read());
        Assert.Equal((int)StoredMessageKind.Text, rows.GetInt32(0));
        Assert.Equal((int)MessageTextType.SignedPlain, rows.GetInt32(1));
        Assert.Equal(255, rows.GetInt32(2));
        Assert.True(rows.IsDBNull(3));
        Assert.Equal("private", rows.GetString(4));
        Assert.Equal(1_700_000_001L, rows.GetInt64(6));
        Assert.Equal(contactKey[..6], (byte[])rows.GetValue(7));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, (byte[])rows.GetValue(8));
        Assert.Equal((int)MessageResolutionState.Resolved, rows.GetInt32(11));

        Assert.True(rows.Read());
        Assert.Equal((int)MessageTextType.CliData, rows.GetInt32(1));
        Assert.Equal(3, rows.GetInt32(2));
        Assert.Equal("channel", rows.GetString(4));
        Assert.Equal(7, rows.GetInt32(9));
        Assert.Equal(binding.Id.ToString("D"), rows.GetString(10));

        Assert.True(rows.Read());
        Assert.Equal((int)StoredMessageKind.Binary, rows.GetInt32(0));
        Assert.True(rows.IsDBNull(1));
        Assert.Equal(4, rows.GetInt32(2));
        Assert.Equal(0xBEEF, rows.GetInt32(3));
        Assert.Equal(new byte[] { 9, 8, 7 }, (byte[])rows.GetValue(5));
        Assert.False(rows.Read());
    }

    [Fact]
    public async Task ResolvesOnlyUniqueCurrentContactAndKeepsUnknownChannelSessionScoped()
    {
        await using var context = await MessageStorageContext.CreateAsync();
        var prefix = new byte[] { 1, 2, 3, 4, 5, 6 };
        var first = prefix.Concat(Enumerable.Repeat((byte)7, 26)).ToArray();
        var second = prefix.Concat(Enumerable.Repeat((byte)8, 26)).ToArray();
        await context.Storage.Directories.ApplySnapshotAsync(
            context.Node.Id, context.Session.Id,
            [new DirectoryContactSnapshot(first, "First", 1, 0, new byte[64], context.Now, 0, 0)], [], context.Now, CancellationToken);

        var eventId = Guid.NewGuid();
        var resolved = context.Envelope(Contact(prefix, "same"), eventId);
        var firstInsert = await context.Storage.IncomingMessages.StoreAsync(resolved, CancellationToken);
        var duplicate = await context.Storage.IncomingMessages.StoreAsync(resolved, CancellationToken);
        var separateButIdentical = await context.Storage.IncomingMessages.StoreAsync(context.Envelope(Contact(prefix, "same")), CancellationToken);
        Assert.True(firstInsert.Inserted);
        Assert.False(duplicate.Inserted);
        Assert.Equal(context.Node.Id, firstInsert.NodeId);
        Assert.Equal(context.Node.Id, duplicate.NodeId);
        Assert.Equal(firstInsert.MessageId, duplicate.MessageId);
        Assert.Equal(firstInsert.MessageId, separateButIdentical.MessageId);
        Assert.False(separateButIdentical.Inserted);

        await context.Storage.Directories.ApplySnapshotAsync(
            context.Node.Id, context.Session.Id,
            [
                new DirectoryContactSnapshot(first, "First", 1, 0, new byte[64], context.Now, 0, 0),
                new DirectoryContactSnapshot(second, "Second", 1, 0, new byte[64], context.Now, 0, 0),
            ], [], context.Now.AddMinutes(1), CancellationToken);
        await context.Storage.IncomingMessages.StoreAsync(context.Envelope(Contact(prefix, "ambiguous")), CancellationToken);
        await context.Storage.Directories.ApplySnapshotAsync(
            context.Node.Id, context.Session.Id, [], [], context.Now.AddMinutes(2), CancellationToken);
        await context.Storage.IncomingMessages.StoreAsync(context.Envelope(Contact(prefix, "unresolved")), CancellationToken);
        await context.Storage.Directories.ApplySnapshotAsync(
            context.Node.Id, context.Session.Id, [],
            [new DirectoryChannelSnapshot(3, "Before", Fingerprint(30), ChannelAccessKind.Unknown)],
            context.Now.AddMinutes(3), CancellationToken);
        var changed = await context.Storage.Directories.ApplySnapshotAsync(
            context.Node.Id, context.Session.Id, [],
            [new DirectoryChannelSnapshot(3, "After", Fingerprint(31), ChannelAccessKind.Unknown)],
            context.Now.AddMinutes(4), CancellationToken);
        await context.Storage.Directories.CommitPendingChannelTransitionsAsync(
            changed.PendingChannelTransitions, context.Now.AddMinutes(5), CancellationToken);
        var unknownChannel = await context.Storage.IncomingMessages.StoreAsync(
            context.Envelope(new ChannelMessage(3, 2, MessageTextType.Plain, context.Now, "unknown", null)), CancellationToken);

        await using var connection = OpenReadOnly(context.DatabasePath);
        Assert.Equal(1, ScalarInt(connection, "SELECT COUNT(*) FROM Messages WHERE ResolutionState = 1;"));
        Assert.Equal(1, ScalarInt(connection, "SELECT COUNT(*) FROM Messages WHERE ResolutionState = 2;"));
        Assert.Equal(2, ScalarInt(connection, "SELECT COUNT(*) FROM Messages WHERE ResolutionState = 0;"));
        using var unknown = connection.CreateCommand();
        unknown.CommandText = "SELECT Kind, UnknownIdentity FROM Conversations WHERE Id = $id;";
        unknown.Parameters.AddWithValue("$id", unknownChannel.ConversationId.ToString("D"));
        using var row = unknown.ExecuteReader();
        Assert.True(row.Read());
        Assert.Equal((int)ConversationKind.UnknownChannel, row.GetInt32(0));
        Assert.Equal(context.Session.Id.ToByteArray().Append((byte)3).ToArray(), (byte[])row.GetValue(1));
    }

    private static ContactMessage Contact(byte[] prefix, string text) => new(
        prefix, 1, MessageTextType.Plain, DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), text, Array.Empty<byte>(), -1);
    private static byte[] Key(byte seed) => Enumerable.Range(0, 32).Select(i => (byte)(seed + i)).ToArray();
    private static byte[] Fingerprint(byte seed) => System.Security.Cryptography.SHA256.HashData(Enumerable.Repeat(seed, 16).ToArray());
    private static int ScalarInt(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar());
    }
    private static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private sealed class MessageStorageContext : IAsyncDisposable
    {
        private MessageStorageContext(string root, LocalStorage storage, NodeRecord node, SessionRecord session)
        {
            Root = root; Storage = storage; Node = node; Session = session;
        }
        public string Root { get; }
        public string DatabasePath => Path.Combine(Root, "messenger.db");
        public LocalStorage Storage { get; private set; }
        public NodeRecord Node { get; }
        public SessionRecord Session { get; }
        public DateTimeOffset Now { get; } = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

        public static async Task<MessageStorageContext> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "MeshCoreMessenger.Incoming.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var storage = await LocalStorage.OpenAsync(new TestPaths(root), CancellationToken);
            var now = DateTimeOffset.UtcNow;
            var profile = new ConnectionProfile
            {
                Id = Guid.NewGuid(), Name = "Test", Transport = ConnectionTransportKind.Serial,
                SerialPortName = "/dev/cu.fake", BaudRate = 115_200, OpenDelayMilliseconds = 0,
                CommandTimeoutMilliseconds = 1_000, AcknowledgementTimeoutMilliseconds = 2_000,
                CreatedUtc = now, UpdatedUtc = now,
            };
            await storage.ConnectionProfiles.SaveAsync(profile, CancellationToken);
            var node = await storage.Nodes.FindOrCreateAsync(Key(100), "Node", now, CancellationToken);
            var session = new SessionRecord(Guid.NewGuid(), profile.Id, null, now, null, null);
            await storage.Sessions.CreateAsync(session, CancellationToken);
            await storage.Sessions.BindNodeAsync(session.Id, node.Id, CancellationToken);
            return new MessageStorageContext(root, storage, node, session with { NodeId = node.Id });
        }
        public IncomingMessageEnvelope Envelope(ReceivedMessage message, Guid? eventId = null, ChannelBindingRecord? binding = null) =>
            new(eventId ?? Guid.NewGuid(), Session.Id, Node.Id, message, Now, binding, null);
        public async Task ReopenAsync()
        {
            await Storage.DisposeAsync();
            Storage = await LocalStorage.OpenAsync(new TestPaths(Root), CancellationToken);
        }
        public async ValueTask DisposeAsync()
        {
            await Storage.DisposeAsync();
            try { Directory.Delete(Root, recursive: true); } catch (DirectoryNotFoundException) { }
        }
        private sealed record TestPaths(string DataDirectory) : IAppPaths
        {
            public string DatabasePath { get; } = Path.Combine(DataDirectory, "messenger.db");
            public string BackupsDirectory { get; } = Path.Combine(DataDirectory, "backups");
        }
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;
}
