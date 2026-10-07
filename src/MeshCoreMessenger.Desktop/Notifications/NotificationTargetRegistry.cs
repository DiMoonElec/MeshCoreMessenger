using System.Text.Json;

namespace MeshCoreMessenger.Desktop.Notifications;

internal sealed record NotificationActivationEntry(int Version, string DataDirectory, DateTimeOffset ExpiresUtc,
    Guid? NodeId, Guid? ConversationId, Guid? MessageId)
{
    public NotificationTarget Target => NodeId is { } node && ConversationId is { } conversation && MessageId is { } message
        ? new MessageNotificationTarget(node, conversation, message) : new OpenApplicationTarget();
}

/// <summary>Bounded opaque cold-start targets. Never stores notification text or secret keys.</summary>
internal sealed class NotificationTargetRegistry(string directory, TimeProvider time)
{
    internal const int Capacity = 128;
    public string Add(string dataDirectory, NotificationTarget? target)
    {
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var old in Directory.EnumerateFiles(directory, "*.json").OrderByDescending(File.GetLastWriteTimeUtc).Skip(Capacity - 1))
            TryDelete(old);
        var token = Guid.NewGuid().ToString("N");
        var message = target as MessageNotificationTarget;
        var entry = new NotificationActivationEntry(1, Path.GetFullPath(dataDirectory), time.GetUtcNow().AddDays(2),
            message?.NodeId, message?.ConversationId, message?.MessageId);
        var path = Path.Combine(directory, token + ".json");
        var temporary = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temporary, options))
            JsonSerializer.Serialize(stream, entry);
        File.Move(temporary, path);
        return token;
    }
    public NotificationActivationEntry? Get(string? token)
    {
        if (!Guid.TryParseExact(token, "N", out _)) return null;
        try
        {
            var path = Path.Combine(directory, token + ".json");
            if (new FileInfo(path).Length > 8192) return null;
            var entry = JsonSerializer.Deserialize<NotificationActivationEntry>(File.ReadAllText(path));
            if (entry is null || entry.Version != 1 || entry.ExpiresUtc <= time.GetUtcNow() ||
                !Path.IsPathFullyQualified(entry.DataDirectory) || (entry.NodeId is null) != (entry.ConversationId is null) ||
                (entry.NodeId is null) != (entry.MessageId is null) || entry.NodeId == Guid.Empty ||
                entry.ConversationId == Guid.Empty || entry.MessageId == Guid.Empty) return null;
            return entry;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }
    public void Remove(string token) { if (Guid.TryParseExact(token, "N", out _)) TryDelete(Path.Combine(directory, token + ".json")); }
    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
