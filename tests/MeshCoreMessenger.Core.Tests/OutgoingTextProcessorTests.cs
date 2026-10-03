using MeshCoreMessenger.Core.Application;
using MeshCoreSharp.Models;
using Xunit;

namespace MeshCoreMessenger.Core.Tests;

public sealed class OutgoingTextProcessorTests
{
    [Theory]
    [InlineData(TextOptimizationLevel.Disabled)]
    [InlineData(TextOptimizationLevel.Conservative)]
    [InlineData(TextOptimizationLevel.Moderate)]
    [InlineData(TextOptimizationLevel.Aggressive)]
    public void PlaceholderPreservesOriginalIncludingHomoglyphsMentionsAndUnicode(TextOptimizationLevel level)
    {
        const string original = "  оo рp аa еe сc хx @[Нода] 👋\ne\u0301\0";
        var processor = new PassthroughOutgoingTextProcessor();
        var result = processor.Process(original, new(false), new(level));
        Assert.Same(original, result.OriginalText);
        Assert.Same(original, result.TransmissionText);
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(original), result.Validation.Utf8ByteCount);
        Assert.Equal(TextMessageValidationError.ContainsNul, result.Validation.Error);
    }

    [Fact]
    public void ProcessorOwnsFinalByteBudgetAndDistinguishesUnknownChannelNameFromEmptyName()
    {
        var processor = new PassthroughOutgoingTextProcessor();
        Assert.Equal(160, processor.Process("я👋", new(false), new()).Validation.MaxUtf8Bytes);
        Assert.Equal(154, processor.Process("я👋", new(true, "🐈"), new()).Validation.MaxUtf8Bytes);
        Assert.Equal(158, processor.Process("я👋", new(true, ""), new()).Validation.MaxUtf8Bytes);
        var unknown = processor.Process("я👋", new(true), new());
        Assert.Equal(6, unknown.Validation.Utf8ByteCount);
        Assert.Null(unknown.Validation.MaxUtf8Bytes);
        Assert.Equal(TextMessageValidationError.LimitUnknown, unknown.Validation.Error);
        Assert.Throws<ArgumentOutOfRangeException>(() => processor.Process("test", new(false), new((TextOptimizationLevel)999)));
    }
}
