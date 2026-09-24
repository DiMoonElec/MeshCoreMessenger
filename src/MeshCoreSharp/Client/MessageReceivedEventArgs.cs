using MeshCoreSharp.Models;

namespace MeshCoreSharp;

public sealed class MessageReceivedEventArgs(ReceivedMessage message) : EventArgs
{
    public ReceivedMessage Message { get; } = message;
}
