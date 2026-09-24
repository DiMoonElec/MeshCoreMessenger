namespace MeshCoreSharp;

public sealed class MeshCoreConnectionStateChangedEventArgs : EventArgs
{
    public MeshCoreConnectionStateChangedEventArgs(
        MeshCoreConnectionState previousState,
        MeshCoreConnectionState currentState)
    {
        PreviousState = previousState;
        CurrentState = currentState;
    }

    public MeshCoreConnectionState PreviousState { get; }
    public MeshCoreConnectionState CurrentState { get; }
}
