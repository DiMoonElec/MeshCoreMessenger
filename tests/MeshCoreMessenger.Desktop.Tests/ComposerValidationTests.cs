using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Desktop.ViewModels;
using MeshCoreSharp;
using MeshCoreSharp.Models;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed class ComposerValidationTests
{
    [Fact]
    public void ByteCounterUsesProcessorOutputWithoutRewritingEditorOrEnablingWire()
    {
        var processor = new ProbeProcessor();
        var composer = new ComposerViewModel(processor: processor) { Readiness = SendReadiness.Ready, Text = "оооо" };
        Assert.Equal("оооо", composer.Text);
        Assert.Equal("test", composer.ProcessedText.TransmissionText);
        Assert.Equal("4 / 160 байт", composer.ByteCounter);
        Assert.True(composer.IsReadyForSend);
        Assert.False(composer.CanSend);
        composer.Options = new(TextOptimizationLevel.Aggressive);
        Assert.Equal(TextOptimizationLevel.Aggressive, processor.Options!.OptimizationLevel);
    }

    [Fact]
    public void ChannelBudgetTracksActualNameAndUnknownOrExhaustedBudgetIsExplicit()
    {
        var composer = new ComposerViewModel { Context = new(true, "🐈"), Readiness = SendReadiness.Ready, Text = new string('x', 155) };
        Assert.Equal("155 / 154 байт", composer.ByteCounter);
        Assert.Contains("Превышен лимит на 1 байт", composer.StatusLine);
        Assert.False(composer.IsReadyForSend);
        composer.Context = new(true, "A");
        Assert.True(composer.IsReadyForSend);
        composer.Context = new(true);
        composer.Readiness = SendReadiness.Offline;
        Assert.Equal("155 / — байт", composer.ByteCounter);
        Assert.Contains("offline • Лимит канала неизвестен", composer.StatusLine);
        Assert.False(composer.IsReadyForSend);
        composer.Context = new(true, new string('x', 160));
        Assert.Equal("155 / 0 байт", composer.ByteCounter);
        Assert.Contains("Имя ноды исчерпало", composer.StatusLine);
    }

    [Fact]
    public void EmptyEditorAndInvalidUnicodeHaveHonestCountsAndReasons()
    {
        var composer = new ComposerViewModel { Readiness = SendReadiness.Ready };
        Assert.Equal("0 / 160 байт", composer.StatusLine);
        Assert.False(composer.IsReadyForSend);
        composer.Text = "\uD800";
        Assert.Equal("— / 160 байт", composer.ByteCounter);
        Assert.Contains("Некорректный Unicode", composer.StatusLine);
        composer.Text = "\0";
        Assert.Equal("1 / 160 байт", composer.ByteCounter);
        Assert.Contains("NUL", composer.StatusLine);
        composer.Text = " \n👋";
        Assert.True(composer.IsReadyForSend);
        Assert.Equal("6 / 160 байт", composer.StatusLine);
    }

    private sealed class ProbeProcessor : IOutgoingTextProcessor
    {
        public OutgoingTextOptions? Options { get; private set; }
        public ProcessedOutgoingText Process(string text, OutgoingTextContext context, OutgoingTextOptions options)
        {
            Options = options;
            return new(text, "test", TextMessageValidator.Validate("test"));
        }
    }
}
