namespace MeshCoreSharp.Models;

public enum EffectiveFloodScope { Unknown, Scoped, Unscoped }
public enum TextMessageKind { Private, Channel }

/// <summary>An immutable outgoing observation, unrelated to incoming message or channel identity.</summary>
public sealed record SendScopeSnapshot(FloodScopeMode Selection, EffectiveFloodScope EffectiveScope,
    RegionKey? Key, string? Name, Guid SessionId, DateTimeOffset CapturedAt, string? UnknownReason)
{
    public string? KeyFingerprint => Key is null ? null :
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Key.ToArray()));
}

/// <summary>The current admission budget; recomputed before sending after waiting for gates.</summary>
public sealed record MessageTextBudget(int MaximumTextBytes, SendScopeSnapshot Scope);
