namespace MeshCoreSharp.Models;

/// <summary>Channel text, preserving the sender-name prefix and whitespace supplied by firmware.</summary>
public sealed record ChannelMessage(
    byte ChannelIndex,
    byte PathLength,
    MessageTextType TextType,
    DateTimeOffset Timestamp,
    string Text,
    double? SnrDb) : ReceivedMessage(PathLength, SnrDb);
