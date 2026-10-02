using Avalonia.Controls.Documents;
using Avalonia.Controls;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Desktop.Controls;
using MeshCoreMessenger.Desktop.Presentation;
using MeshCoreMessenger.Desktop.ViewModels;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed class MentionTests
{
    [Theory]
    [InlineData("")]
    [InlineData("Обычный текст 👋\r\nвторая строка")]
    [InlineData("@[] @[   ] @[нет конца")]
    [InlineData("@[два\nимени] @[два\rимени]")]
    [InlineData("@[вложенное @[имя]]")]
    public void InvalidOrPlainTextIsPreserved(string text)
    {
        var parts = MentionParser.Parse(text, "имя");
        Assert.Equal(text, string.Concat(parts.Select(part => part.RawText)));
        Assert.All(parts, part => Assert.Equal(MentionKind.Plain, part.Kind));
    }

    [Fact]
    public void MultipleAdjacentUnicodeMentionsPreserveEveryCharacter()
    {
        const string text = "Автор: @[RnD Morpheus]@[Нода 🐈] и @[第三者]\n@[RnD Morpheus]!";
        var parts = MentionParser.Parse(text, "RnD Morpheus");
        Assert.Equal(text, string.Concat(parts.Select(part => part.RawText)));
        Assert.Equal([MentionKind.Own, MentionKind.Other, MentionKind.Other, MentionKind.Own],
            parts.Where(part => part.Kind != MentionKind.Plain).Select(part => part.Kind));
    }

    [Theory]
    [InlineData("RnD Morpheus", true)]
    [InlineData("rnd morpheus", false)]
    [InlineData("Morpheus", false)]
    [InlineData("RnD Morpheus ", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void OwnNameRequiresExactOrdinalMatch(string? name, bool own) =>
        Assert.Equal(own ? MentionKind.Own : MentionKind.Other, Assert.Single(MentionParser.Parse("@[RnD Morpheus]", name)).Kind);

    [Fact]
    public void MalformedMentionDoesNotPreventLaterValidMention()
    {
        var parts = MentionParser.Parse("@[] @[bad\nname] и @[Нода]", "Нода");
        Assert.Equal(MentionKind.Own, parts[^1].Kind);
        Assert.Equal("@[] @[bad\nname] и ", parts[0].RawText);
    }

    [Fact]
    public void ControlReclassifiesNameAndClearsOldRunsWhenReused()
    {
        var control = new MentionTextBlock
        {
            OwnNodeName = "A", MessageText = "text @[A] @[B] tail",
        };
        Assert.Equal("text @[A] @[B] tail", Flatten(control));
        Assert.Contains("own", Chips(control)[0].Classes);
        Assert.DoesNotContain("own", Chips(control)[1].Classes);
        control.OwnNodeName = "B";
        Assert.DoesNotContain("own", Chips(control)[0].Classes);
        Assert.Contains("own", Chips(control)[1].Classes);
        control.OwnNodeName = null;
        Assert.All(Chips(control), chip => Assert.DoesNotContain("own", chip.Classes));
        control.MessageText = "другой текст";
        Assert.Single(Runs(control));
        Assert.Equal("другой текст", Flatten(control));
        control.MessageText = null;
        Assert.Empty(Runs(control));
    }

    [Fact]
    public async Task CopyUsesOriginalModelRegardlessOfDisplayOrNodeName()
    {
        const string raw = "TGN R6LOW📋: @[RnD Morpheus 💊] мы тут! ☝\nВторая строка";
        var model = new HistoryMessageListItem(new HistoryMessage(
            Guid.NewGuid(), 1, Guid.NewGuid(), MessageDirection.Incoming, StoredMessageKind.Text, raw, DateTimeOffset.UtcNow));
        var control = new MentionTextBlock { MessageText = "different display", OwnNodeName = "other" };
        string? copied = null;
        await MessageCopy.CopyAsync(model, text => { copied = text; return Task.CompletedTask; });
        Assert.Equal(raw, copied);
        Assert.NotEqual(control.MessageText, copied);
    }

    [Fact]
    public void UnnamedNodeHasNoMentionIdentityEvenThoughHeaderHasFallback()
    {
        var node = new KnownNodeListItem(new NodeRecord(Guid.NewGuid(), new byte[32], "", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        Assert.Equal("Нода без имени", node.Name);
        Assert.Null(node.MentionName);
    }

    private static Run[] Runs(MentionTextBlock control) => control.Inlines!.OfType<Run>().ToArray();
    private static Border[] Chips(MentionTextBlock control) => control.Inlines!.OfType<InlineUIContainer>().Select(inline => (Border)inline.Child).ToArray();
    private static string Flatten(MentionTextBlock control) => string.Concat(control.Inlines!.Select(inline => inline is Run run
        ? run.Text : ((TextBlock)((Border)((InlineUIContainer)inline).Child).Child!).Text));
}
