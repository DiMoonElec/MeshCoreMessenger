namespace MeshCoreSharp.Exceptions;

public sealed class MeshCoreTimeoutException : MeshCoreException
{
    public MeshCoreTimeoutException(string operation, TimeSpan timeout)
        : base($"MeshCore operation '{operation}' timed out after {timeout}.")
    {
        Operation = operation;
        Timeout = timeout;
    }

    public string Operation { get; }
    public TimeSpan Timeout { get; }
}
