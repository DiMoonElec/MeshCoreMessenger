using MeshCoreSharp.Protocol;

namespace MeshCoreSharp.Exceptions;

public sealed class MeshCoreCommandException : MeshCoreException
{
    public MeshCoreCommandException(CommandType command, MeshCoreErrorCode? errorCode)
        : base(errorCode is null
            ? $"MeshCore command {command} failed with PACKET_ERROR."
            : $"MeshCore command {command} failed with {errorCode} ({(byte)errorCode.Value}).")
    {
        Command = command;
        ErrorCode = errorCode;
    }

    public CommandType Command { get; }
    public MeshCoreErrorCode? ErrorCode { get; }
}
