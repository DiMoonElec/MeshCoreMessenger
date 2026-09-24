namespace MeshCoreSharp;

public sealed class MeshCoreClientErrorEventArgs : EventArgs
{
    public MeshCoreClientErrorEventArgs(Exception exception) => Exception = exception;
    public Exception Exception { get; }
}
