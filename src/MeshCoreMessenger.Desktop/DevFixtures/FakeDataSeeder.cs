using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.ViewModels;
using MeshCoreSharp.Models;

namespace MeshCoreMessenger.Desktop.DevFixtures;

/// <summary>Offline visual fixtures. All fixture writes go through production Core stores.</summary>
internal sealed class FakeDataSeeder
{
    public const string ProfileName = "[ТЕСТ] Fixture — не подключать";
    public const string SessionEndReason = "FixtureCompleted";

    // Read-only preflight before OpenAsync: a refusal must not migrate an existing database.
    public static async Task EnsureEmptyDatabaseAsync(IAppPaths paths, CancellationToken cancellationToken = default)
    {
        if (new FileInfo(paths.DatabasePath).LinkTarget is not null)
            throw new FakeDataSeedException("Fake-data seed refuses a symbolic-link database file; use an isolated test folder.");
        if (!File.Exists(paths.DatabasePath))
            return;
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT EXISTS(SELECT 1 FROM Nodes) OR EXISTS(SELECT 1 FROM Messages)
                    OR EXISTS(SELECT 1 FROM Contacts) OR EXISTS(SELECT 1 FROM ConnectionProfiles);
                """;
            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0)
                throw NotEmpty();
        }
        catch (SqliteException exception)
        {
            throw new FakeDataSeedException($"Fake-data seed cannot verify that the existing database is empty: {exception.Message}. Database left unchanged; use a new test folder.");
        }
    }

    public static void ValidateDirectoryTarget(string directory, string defaultDirectory)
    {
        try
        {
            var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.Equals(ResolveDirectoryLinks(directory), ResolveDirectoryLinks(defaultDirectory), comparison))
                throw new FakeDataSeedException("--seed-fake-data cannot use the standard data directory, including via a symbolic link.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new FakeDataSeedException($"Fake-data seed cannot verify directory isolation: {exception.Message}");
        }
    }

    private static string ResolveDirectoryLinks(string directory)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var parent = Path.GetDirectoryName(full);
        if (parent is null)
            return full;
        var resolved = Path.Combine(ResolveDirectoryLinks(parent), Path.GetFileName(full));
        var info = new DirectoryInfo(resolved);
        return info.LinkTarget is null ? resolved :
            ResolveDirectoryLinks(info.ResolveLinkTarget(returnFinalTarget: true)!.FullName);
    }

    public async Task<FakeDataSeedResult> SeedAsync(
        LocalStorage storage, IAppPaths paths, bool large, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        // Foreign keys guarantee contacts/messages cannot exist without a node. Profile-only
        // databases are checked separately. The caller owns the per-directory instance lock.
        if ((await storage.Nodes.GetAllAsync(1, cancellationToken).ConfigureAwait(false)).Count != 0 ||
            (await storage.ConnectionProfiles.GetAllAsync(cancellationToken).ConfigureAwait(false)).Count != 0)
            throw NotEmpty();

        now = now.ToUniversalTime();
        var profile = new ConnectionProfile
        {
            Id = Guid.NewGuid(), Name = ProfileName, Transport = ConnectionTransportKind.Serial,
            SerialPortName = Path.Combine(paths.DataDirectory, $".fixture-no-device-{Guid.NewGuid():N}", "not-a-serial-port"),
            BaudRate = 115200, AutoConnect = false, Reconnect = false,
            DtrEnable = false, RtsEnable = false, OpenDelayMilliseconds = 0,
            CreatedUtc = now, UpdatedUtc = now,
        };
        await storage.ConnectionProfiles.SaveAsync(profile, cancellationToken).ConfigureAwait(false);
        var node = await storage.Nodes.FindOrCreateAsync(Key("owner"), "[ТЕСТ] Визуальная нода 🐈", now, cancellationToken).ConfigureAwait(false);
        var sessionId = Guid.NewGuid();
        // CreateAsync rejects pre-ended records. Close immediately, before any fixture data or UI.
        await storage.Sessions.CreateAsync(new SessionRecord(sessionId, profile.Id, node.Id, now.AddMonths(-3), null, null), cancellationToken).ConfigureAwait(false);
        await storage.Sessions.EndAsync(sessionId, now, SessionEndReason, cancellationToken).ConfigureAwait(false);

        var contacts = CreateContacts(now);
        var channelNames = new List<string>
        {
            "#fixture-empty", "#fixture-twenty", "#fixture-5000", "#fixture-unread",
            "#fixture-длинное-название-" + string.Concat(Enumerable.Repeat("Очень длинное название канала 🌍 — ", 8)),
        };
        if (large)
            channelNames.Add("#fixture-100000");
        var channels = channelNames.Select((name, slot) => new DirectoryChannelSnapshot(
            (byte)slot, name, Key($"channel-{slot}"), ChannelAccessKind.PublicOrHashtag)).ToArray();
        var snapshot = await storage.Directories.ApplySnapshotAsync(node.Id, sessionId, contacts, channels, now, cancellationToken).ConfigureAwait(false);
        var counts = new[] { 0, 20, 5000, 30, 3, 100000 };
        var messageCount = 0;
        foreach (var binding in snapshot.ActiveBindings)
        {
            StoredIncomingMessage? lastRead = null;
            var count = counts[binding.Slot];
            for (var index = 0; index < count; index++)
            {
                var received = MessageTime(now, index, count);
                var text = binding.Slot == 1 ? ExampleText(index) : $"Участник {index % 4 + 1}: Сообщение {index + 1:D6} — {channelNames[binding.Slot]}";
                var stored = await storage.IncomingMessages.StoreAsync(new IncomingMessageEnvelope(
                    Guid.NewGuid(), sessionId, node.Id,
                    new ChannelMessage(binding.Slot, 2, MessageTextType.Plain, received, text, -5),
                    received, binding, null), cancellationToken).ConfigureAwait(false);
                messageCount++;
                if (binding.Slot != 3 || index < 20)
                    lastRead = stored;
            }
            if (lastRead is not null)
                await MarkReadAsync(storage, lastRead, cancellationToken).ConfigureAwait(false);
        }

        foreach (var contact in contacts.Where(contact => contact.ContactType == 1))
        {
            StoredIncomingMessage? last = null;
            for (var index = 0; index < 12; index++)
            {
                var received = MessageTime(now, index, 12);
                last = await storage.IncomingMessages.StoreAsync(new IncomingMessageEnvelope(
                    Guid.NewGuid(), sessionId, node.Id,
                    new ContactMessage(contact.PublicKey[..6], 1, MessageTextType.Plain, received, ExampleText(index), ReadOnlyMemory<byte>.Empty, -4),
                    received, null, null), cancellationToken).ConfigureAwait(false);
                messageCount++;
            }
            await MarkReadAsync(storage, last!, cancellationToken).ConfigureAwait(false);
        }

        // Omission in the next real snapshot is the production way to retain absent devices.
        await storage.Directories.ApplySnapshotAsync(node.Id, sessionId,
            contacts.Where((_, index) => index is not (6 or 8 or 10)).ToArray(), channels, now.AddSeconds(1), cancellationToken).ConfigureAwait(false);
        await storage.Settings.SetAsync(MainWindowViewModel.ViewedNodeSettingKey, node.Id.ToString("D"), cancellationToken).ConfigureAwait(false);
        await storage.Settings.SetAsync(MainWindowViewModel.LastConnectedNodeSettingKey, node.Id.ToString("D"), cancellationToken).ConfigureAwait(false);
        await storage.Settings.SetAsync(ConversationNavigationViewModel.TabSettingKey(node.Id), MessengerNavigationTab.Channels.ToString(), cancellationToken).ConfigureAwait(false);
        return new FakeDataSeedResult(node.Id, sessionId, profile.Id, channels.Length, contacts.Length, messageCount);
    }

    private static Task<ConversationReadState> MarkReadAsync(LocalStorage storage, StoredIncomingMessage message, CancellationToken cancellationToken) =>
        storage.ReadStates.AdvanceAsync(new HistoryMessagePosition(message.NodeId, message.ConversationId, message.MessageId, message.LocalSequence), cancellationToken);

    private static DirectoryContactSnapshot[] CreateContacts(DateTimeOffset now)
    {
        string[] names = ["Alice", "Bob", "Александра — " + new string('я', 120), "Мария Иванова", "Кот 🐈‍⬛ / 🛰️",
            "Ретранслятор на крыше", "Ретранслятор offline", "Комната сообщества 🏠", "Архивная комната", "Датчик 🌡️", "Датчик offline"];
        return names.Select((name, index) => new DirectoryContactSnapshot(
            Key($"contact-{index}"), name, index < 5 ? 1 : 2 + (index - 5) / 2, 0, new byte[64],
            now.AddHours(-index * 13), index is 5 or 8 or 9 ? 55.75 + index * 0.001 : 0,
            index is 5 or 8 or 9 ? 37.61 + index * 0.001 : 0)).ToArray();
    }

    private static DateTimeOffset MessageTime(DateTimeOffset now, int index, int count)
    {
        var group = index * 4L / count;
        var day = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        return group switch
        {
            0 => day.AddMonths(-2).AddSeconds(index),
            1 => day.AddMonths(-1).AddSeconds(index),
            2 => day.AddDays(-1).AddSeconds(index % 86400),
            _ => day.AddTicks(now.TimeOfDay.Ticks * (index + 1) / count),
        };
    }

    private static string ExampleText(int index) => (index % 7) switch
    {
        0 => "Привет! Это тестовое сообщение 👋",
        1 => "Несколько строк:\nПервая строка\nВторая строка\n\nПоследний абзац.",
        2 => string.Concat(Enumerable.Repeat("Очень длинное сообщение для проверки переноса и высоты строки. ", 160)),
        3 => new string('W', 2048),
        4 => "URL: https://example.org/meshcore?account=fixture&lang=ru#chat",
        5 => "Кириллица, emoji: 🐈 🌍 🛰️ 👍🏽 — проверка Unicode",
        _ => "Короткая строка",
    };

    private static byte[] Key(string label) => SHA256.HashData(Encoding.UTF8.GetBytes($"MeshCoreMessenger visual fixture: {label}"));
    private static FakeDataSeedException NotEmpty() => new("Fake-data seed refused: the database is not empty (nodes, messages, contacts or profiles). Nothing was seeded; use a new test folder.");
}

internal sealed record FakeDataSeedResult(Guid NodeId, Guid SessionId, Guid ProfileId, int ChannelCount, int ContactCount, int MessageCount);
