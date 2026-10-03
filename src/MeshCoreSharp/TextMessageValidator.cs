using System.Text;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;

namespace MeshCoreSharp;

/// <summary>Shared plain-text validation for Companion commands and application composers. Never modifies text.</summary>
public static class TextMessageValidator
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Checks text without trimming it. Pass a null limit when a channel's current sender name is unknown.</summary>
    public static TextMessageValidation Validate(string? text, int? maxUtf8Bytes = ProtocolLimits.MaxTextBytes)
    {
        int? bytes;
        try { bytes = text is null ? 0 : StrictUtf8.GetByteCount(text); }
        catch (EncoderFallbackException) { bytes = null; }
        // Preserve the encoder's rejection order: empty, NUL, malformed UTF-16, size.
        var error = string.IsNullOrEmpty(text) ? TextMessageValidationError.Empty
            : text.Contains('\0') ? TextMessageValidationError.ContainsNul
            : bytes is null ? TextMessageValidationError.InvalidUtf16
            : maxUtf8Bytes is null ? TextMessageValidationError.LimitUnknown
            : bytes > maxUtf8Bytes ? TextMessageValidationError.ExceedsLimit
            : TextMessageValidationError.None;
        return new TextMessageValidation(bytes, maxUtf8Bytes, error);
    }

    /// <summary>The firmware inserts the actual SelfInfo name followed by ': ' into the 160-byte budget.</summary>
    public static int GetChannelTextLimit(string senderName)
    {
        ArgumentNullException.ThrowIfNull(senderName);
        // Preserve the existing name encoding policy; strict validation applies to the message body.
        return ProtocolLimits.MaxTextBytes - Encoding.UTF8.GetByteCount(senderName) - 2;
    }

    internal static byte[] EncodeText(string text, int maxUtf8Bytes)
    {
        var result = Validate(text, maxUtf8Bytes);
        switch (result.Error)
        {
            case TextMessageValidationError.Empty:
                ArgumentException.ThrowIfNullOrEmpty(text);
                break;
            case TextMessageValidationError.ContainsNul:
                throw new ArgumentException("Text must not contain NUL.", nameof(text));
            case TextMessageValidationError.ExceedsLimit:
                throw new ArgumentOutOfRangeException(nameof(text), $"Text exceeds the {maxUtf8Bytes}-byte UTF-8 limit.");
        }
        // Keeps EncoderFallbackException for invalid UTF-16 and produces the unchanged wire bytes.
        return StrictUtf8.GetBytes(text);
    }
}
