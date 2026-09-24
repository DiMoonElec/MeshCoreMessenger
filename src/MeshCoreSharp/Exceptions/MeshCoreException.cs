namespace MeshCoreSharp.Exceptions;

public class MeshCoreException : Exception
{
    public MeshCoreException(string message) : base(message) { }
    public MeshCoreException(string message, Exception innerException) : base(message, innerException) { }
}
