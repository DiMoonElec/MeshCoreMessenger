using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MeshCoreMessenger.Desktop.ViewModels;

/// <summary>Presentation only; the existing editor retains all draft persistence responsibilities.</summary>
public sealed class ComposerViewModel : ObservableObject
{
    private string _previewText = string.Empty;
    private string? _byteCounter;
    private string _availability = "Отправка пока недоступна";
    private string _previewExplanation = string.Empty;

    public ComposerViewModel(DraftEditorViewModel? draft = null)
    {
        Draft = draft;
        if (draft is not null) draft.PropertyChanged += OnDraftChanged;
    }

    public DraftEditorViewModel? Draft { get; }
    public string Text
    {
        get => Draft?.Text ?? _previewText;
        set
        {
            if (Draft is not null) Draft.Text = value;
            else SetProperty(ref _previewText, value);
        }
    }
    public bool CanEdit => Draft?.CanEdit ?? true;
    // D1 deliberately has no command adapter. Preview cannot enable a real send.
    public bool CanSend => false;
    public string? ByteCounter
    {
        get => _byteCounter;
        set { if (SetProperty(ref _byteCounter, value)) OnPropertyChanged(nameof(StatusLine)); }
    }
    public string Availability
    {
        get => _availability;
        set { if (SetProperty(ref _availability, value)) OnPropertyChanged(nameof(StatusLine)); }
    }
    public string PreviewExplanation
    {
        get => _previewExplanation;
        set { if (SetProperty(ref _previewExplanation, value)) OnPropertyChanged(nameof(StatusLine)); }
    }
    public string StatusLine
    {
        get
        {
            var explanation = Draft is null ? PreviewExplanation : Draft.ErrorMessage ?? Draft.Status;
            var statuses = string.Join(" • ", new[] { Availability, explanation }.Where(s => !string.IsNullOrWhiteSpace(s)));
            return string.IsNullOrEmpty(ByteCounter) ? statuses
                : string.IsNullOrEmpty(statuses) ? ByteCounter : $"{ByteCounter} - {statuses}";
        }
    }

    private void OnDraftChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(DraftEditorViewModel.Text)) OnPropertyChanged(nameof(Text));
        if (args.PropertyName == nameof(DraftEditorViewModel.CanEdit)) OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(StatusLine));
    }
}
