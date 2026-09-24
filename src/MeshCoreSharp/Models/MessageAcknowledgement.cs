namespace MeshCoreSharp.Models;

public sealed record MessageAcknowledgement(uint Ack, uint RoundTripTimeMilliseconds);
