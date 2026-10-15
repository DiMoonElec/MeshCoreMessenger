namespace MeshCoreSharp.Exceptions;

public class MeshCoreTransportException : MeshCoreException
{
    public MeshCoreTransportException(string message) : base(message) { }
    public MeshCoreTransportException(string message, Exception? innerException) : base(message, innerException) { }
}
