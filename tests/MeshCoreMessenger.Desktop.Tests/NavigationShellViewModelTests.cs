using MeshCoreMessenger.Desktop.ViewModels;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed class NavigationShellViewModelTests
{
    [Theory]
    [InlineData(ShellSection.PublicChats)]
    [InlineData(ShellSection.PrivateChats)]
    [InlineData(ShellSection.Devices)]
    [InlineData(ShellSection.Connection)]
    public void SettingsReturnsToPreviousScreenWithoutChangingSelectionTwice(ShellSection previous)
    {
        var shell = new NavigationShellViewModel();
        shell.SelectSection(previous);
        Assert.False(shell.ReturnFromSettingsCommand.CanExecute(null));
        shell.SelectSection(ShellSection.Settings);
        shell.SelectSection(ShellSection.Settings);
        Assert.True(shell.ReturnFromSettingsCommand.CanExecute(null));
        shell.ReturnFromSettingsCommand.Execute(null);
        Assert.Equal(previous, shell.SelectedItem.Section);
        Assert.False(shell.ReturnFromSettingsCommand.CanExecute(null));
    }

    [Fact]
    public void NavigationGroupsHaveRequiredOrderAndDefaultSelection()
    {
        var shell = new NavigationShellViewModel();

        Assert.Equal(
            [ShellSection.PublicChats, ShellSection.PrivateChats, ShellSection.Devices],
            shell.TopItems.Select(item => item.Section));
        Assert.Equal(
            [ShellSection.Connection, ShellSection.Settings],
            shell.BottomItems.Select(item => item.Section));
        Assert.Same(shell.TopItems[0], shell.SelectedItem);
        Assert.True(shell.SelectedItem.IsSelected);
    }

    [Fact]
    public void CommandsSwitchContentAndKeepOneSelectionAcrossBothGroups()
    {
        var shell = new NavigationShellViewModel();
        var items = shell.TopItems.Concat(shell.BottomItems).ToArray();
        var notifications = 0;
        shell.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(shell.SelectedItem))
            {
                notifications++;
            }
        };

        foreach (var item in items.Reverse())
        {
            item.SelectCommand.Execute(null);
            Assert.Same(item, shell.SelectedItem);
            Assert.Same(item, Assert.Single(items, candidate => candidate.IsSelected));
            Assert.Equal(item.Title, shell.SelectedItem.Title);
        }

        Assert.Equal(5, notifications);
        shell.SelectedItem.SelectCommand.Execute(null);
        Assert.Equal(5, notifications);
        Assert.True(shell.SelectedItem.IsSelected);
    }
}
