using MeshCoreSharp;
using MeshCoreSharp.Models;

namespace MeshCoreMessenger.Core.Application;

/// <summary>Reserved levels for a future visually equivalent Cyrillic-to-Latin substitution policy.</summary>
public enum TextOptimizationLevel { Disabled, Conservative, Moderate, Aggressive }

public sealed record OutgoingTextOptions(TextOptimizationLevel OptimizationLevel = TextOptimizationLevel.Disabled);

/// <summary>A null sender name leaves a channel's budget unknown. Names are never optimized.</summary>
public sealed record OutgoingTextContext(bool IsChannel, string? SenderName = null);

/// <summary>Capture TransmissionText with the message when sending; never rewrite the user's draft.</summary>
public sealed record ProcessedOutgoingText(string OriginalText, string TransmissionText, TextMessageValidation Validation);

public interface IOutgoingTextProcessor
{
    ProcessedOutgoingText Process(string text, OutgoingTextContext context, OutgoingTextOptions options);
}

/// <summary>D2 placeholder: every optimization level returns exactly the original text.</summary>
public sealed class PassthroughOutgoingTextProcessor : IOutgoingTextProcessor
{
    public ProcessedOutgoingText Process(string text, OutgoingTextContext context, OutgoingTextOptions options)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.OptimizationLevel)) throw new ArgumentOutOfRangeException(nameof(options));
        int? limit = context.IsChannel
            ? context.SenderName is null ? null : TextMessageValidator.GetChannelTextLimit(context.SenderName)
            : MeshCoreSharp.Protocol.ProtocolLimits.MaxTextBytes;
        return new(text, text, TextMessageValidator.Validate(text, limit));
    }
}
