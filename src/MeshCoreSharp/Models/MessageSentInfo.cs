namespace MeshCoreSharp.Models;

/// <summary>Immediate acceptance by the Companion, not confirmation of remote delivery.</summary>
public sealed record MessageSentInfo(bool IsFlood, uint ExpectedAck, uint SuggestedTimeoutMilliseconds);
