using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using MeshCoreMessenger.Desktop.Presentation;

namespace MeshCoreMessenger.Desktop.Controls;

/// <summary>Presentation adapter for semantic parts. Copy belongs to the message, not this visual.</summary>
public sealed class MentionTextBlock : TextBlock
{
    public static readonly StyledProperty<string?> MessageTextProperty =
        AvaloniaProperty.Register<MentionTextBlock, string?>(nameof(MessageText));
    public static readonly StyledProperty<string?> OwnNodeNameProperty =
        AvaloniaProperty.Register<MentionTextBlock, string?>(nameof(OwnNodeName));

    public string? MessageText { get => GetValue(MessageTextProperty); set => SetValue(MessageTextProperty, value); }
    public string? OwnNodeName { get => GetValue(OwnNodeNameProperty); set => SetValue(OwnNodeNameProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != MessageTextProperty && change.Property != OwnNodeNameProperty) return;
        Inlines = new InlineCollection();
        foreach (var part in MentionParser.Parse(MessageText, OwnNodeName))
        {
            if (part.Kind == MentionKind.Plain) Inlines.Add(new Run(part.DisplayText));
            else
            {
                var label = new TextBlock { Text = part.DisplayText, TextWrapping = TextWrapping.Wrap };
                label.Classes.Add("mention-label");
                var chip = new MentionChip { Child = label };
                chip.Classes.Add("mention-chip");
                chip.Classes.Set("own", part.Kind == MentionKind.Own);
                Inlines.Add(new InlineUIContainer(chip) { BaselineAlignment = BaselineAlignment.Baseline });
            }
        }
    }
}

/// <summary>Forward the child text baseline through Border padding using Avalonia's attached property.</summary>
internal sealed class MentionChip : Border
{
    protected override Type StyleKeyOverride => typeof(Border);
    protected override Size MeasureOverride(Size availableSize)
    {
        var size = base.MeasureOverride(availableSize);
        if (Child is TextBlock label)
            TextBlock.SetBaselineOffset(this, Padding.Top + BorderThickness.Top + label.TextLayout.Baseline);
        return size;
    }
}
