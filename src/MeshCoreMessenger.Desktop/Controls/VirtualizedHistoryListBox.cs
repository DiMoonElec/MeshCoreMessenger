using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace MeshCoreMessenger.Desktop.Controls;

public sealed class VirtualizedHistoryListBox : ListBox
{
    internal const double ItemCacheLength = 1;

    protected override Type StyleKeyOverride => typeof(ListBox);

    public VirtualizedHistoryListBox()
    {
        ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel
        {
            CacheLength = ItemCacheLength,
        });
    }

    internal Panel CreateItemsPanelForTest() =>
        ItemsPanel.Build() ?? throw new InvalidOperationException("The history items panel was not created.");
}
