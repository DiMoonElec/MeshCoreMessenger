namespace MeshCoreSharp.Exceptions;

public sealed class MeshCoreTransportException : MeshCoreException
{
    public MeshCoreTransportException(string message) : base(message) { }
    public MeshCoreTransportException(string message, Exception innerException) : base(message, innerException) { }
}
