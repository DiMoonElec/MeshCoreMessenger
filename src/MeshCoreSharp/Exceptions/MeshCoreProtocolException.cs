namespace MeshCoreSharp.Exceptions;

public sealed class MeshCoreProtocolException : MeshCoreException
{
    public MeshCoreProtocolException(string message) : base(message) { }
    public MeshCoreProtocolException(string message, Exception innerException) : base(message, innerException) { }
}
