using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MeshCoreMessenger.Desktop.ViewModels;

public enum ModalCloseReason { CloseButton, Escape, Backdrop, Completed, Shutdown }
public sealed record ModalResult(ModalCloseReason Reason, object? Value = null);
public sealed record ModalAction(string Label, ICommand Command);

/// <summary>Content supplies presentation and close policy; the host owns one active card per window.</summary>
public abstract class ModalCardViewModel(string title, string? description = null) : ObservableObject, IDisposable
{
    public string Title { get; } = title;
    public string? Description { get; } = description;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public virtual IReadOnlyList<ModalAction> Actions => [];
    public bool HasActions => Actions.Count != 0;
    public virtual bool CloseOnBackdrop => false;
    public virtual bool CanClose(ModalCloseReason reason) => true;
    public virtual void Dispose() { }
}

public sealed class ModalHostViewModel : ObservableObject
{
    private ModalCardViewModel? _active;
    private TaskCompletionSource<ModalResult>? _completion;
    public ModalHostViewModel() => CloseCommand = new RelayCommand(() => Close(ModalCloseReason.CloseButton));
    public RelayCommand CloseCommand { get; }
    public ModalCardViewModel? Active
    {
        get => _active;
        private set
        {
            if (SetProperty(ref _active, value))
            {
                OnPropertyChanged(nameof(IsOpen));
                OnPropertyChanged(nameof(IsClosed));
            }
        }
    }
    public bool IsOpen => Active is not null;
    public bool IsClosed => !IsOpen;
    public Task<ModalResult> ShowAsync(ModalCardViewModel card)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (IsOpen) throw new InvalidOperationException("A modal card is already open.");
        _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = _completion.Task;
        Active = card;
        return task;
    }
    public bool Close(ModalCloseReason reason, object? value = null)
    {
        if (Active is not { } card || (reason != ModalCloseReason.Shutdown && !card.CanClose(reason))) return false;
        if (reason == ModalCloseReason.Backdrop && !card.CloseOnBackdrop) return false;
        var completion = _completion;
        _completion = null;
        Active = null;
        card.Dispose();
        completion?.TrySetResult(new(reason, value));
        return true;
    }
}
