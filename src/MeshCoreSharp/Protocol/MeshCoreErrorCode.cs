namespace MeshCoreSharp.Protocol;

public enum MeshCoreErrorCode : byte
{
    UnsupportedCommand = 1,
    NotFound = 2,
    TableFull = 3,
    BadState = 4,
    FileIoError = 5,
    IllegalArgument = 6,
}
