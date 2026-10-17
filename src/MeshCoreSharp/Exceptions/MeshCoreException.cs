namespace MeshCoreSharp.Exceptions;

public class MeshCoreException : Exception
{
    /// <summary>False proves that a text SEND was not invoked (for example temporary scope SET failed).
    /// Null means this exception carries no such evidence.</summary>
    public bool? TextSendWasInvoked { get; internal set; }
    public MeshCoreException(string message) : base(message) { }
    public MeshCoreException(string message, Exception? innerException) : base(message, innerException) { }
}
