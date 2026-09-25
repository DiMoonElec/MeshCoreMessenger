using CommunityToolkit.Mvvm.ComponentModel;
using MeshCoreMessenger.Core;

namespace MeshCoreMessenger.Desktop.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    public string Title => AppInformation.ProductName;
    public string Status => "Каркас приложения готов";
}
