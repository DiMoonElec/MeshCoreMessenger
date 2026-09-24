namespace MeshCoreSharp.Models;

/// <summary>An incoming queue item. PathLength preserves the encoded firmware byte; 0xFF means direct routing.</summary>
public abstract record ReceivedMessage(byte PathLength, double? SnrDb);
