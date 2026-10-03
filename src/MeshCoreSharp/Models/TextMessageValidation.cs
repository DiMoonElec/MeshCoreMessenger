namespace MeshCoreSharp.Models;

public enum TextMessageValidationError
{
    None,
    Empty,
    ContainsNul,
    InvalidUtf16,
    ExceedsLimit,
    LimitUnknown,
}

/// <summary>Byte count is unavailable for malformed UTF-16; a null limit means the budget is unknown.</summary>
public sealed record TextMessageValidation(int? Utf8ByteCount, int? MaxUtf8Bytes, TextMessageValidationError Error)
{
    public bool IsValid => Error == TextMessageValidationError.None;
}
