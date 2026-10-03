using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MeshCoreMessenger.Core.Application;
using MeshCoreSharp.Models;

namespace MeshCoreMessenger.Desktop.ViewModels;

/// <summary>Presentation only; the existing editor retains all draft persistence responsibilities.</summary>
public sealed class ComposerViewModel : ObservableObject
{
    private string _previewText = string.Empty;
    private readonly IOutgoingTextProcessor _processor;
    private OutgoingTextContext _context = new(false);
    private OutgoingTextOptions _options = new();
    private SendReadiness _readiness = SendReadiness.Offline;
    private ProcessedOutgoingText _processedText;
    private string _previewExplanation = string.Empty;

    public ComposerViewModel(DraftEditorViewModel? draft = null, IOutgoingTextProcessor? processor = null)
    {
        Draft = draft;
        _processor = processor ?? new PassthroughOutgoingTextProcessor();
        _processedText = _processor.Process(Text, Context, Options);
        if (draft is not null) draft.PropertyChanged += OnDraftChanged;
    }

    public DraftEditorViewModel? Draft { get; }
    public string Text
    {
        get => Draft?.Text ?? _previewText;
        set
        {
            if (Draft is not null) Draft.Text = value;
            else if (SetProperty(ref _previewText, value ?? string.Empty)) RefreshText();
        }
    }
    public bool CanEdit => Draft?.CanEdit ?? true;
    // Validation/readiness is presentation only. D5/D6 will supply actual command admission.
    public bool CanSend => false;
    public ProcessedOutgoingText ProcessedText => _processedText;
    public bool IsReadyForSend => Readiness == SendReadiness.Ready && ProcessedText.Validation.IsValid;
    public string ByteCounter => $"{ProcessedText.Validation.Utf8ByteCount?.ToString() ?? "—"} / " +
        $"{(ProcessedText.Validation.MaxUtf8Bytes is { } limit ? Math.Max(0, limit).ToString() : "—")} байт";
    public OutgoingTextContext Context
    {
        get => _context;
        set { ArgumentNullException.ThrowIfNull(value); if (SetProperty(ref _context, value)) RefreshText(); }
    }
    public OutgoingTextOptions Options
    {
        get => _options;
        set { ArgumentNullException.ThrowIfNull(value); if (SetProperty(ref _options, value)) RefreshText(); }
    }
    public SendReadiness Readiness
    {
        get => _readiness;
        set
        {
            if (!SetProperty(ref _readiness, value)) return;
            OnPropertyChanged(nameof(StatusLine));
            OnPropertyChanged(nameof(IsReadyForSend));
        }
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
            var statuses = string.Join(" • ", new[] { ReadinessMessage, ValidationMessage, explanation }.Where(s => !string.IsNullOrWhiteSpace(s)));
            return statuses.Length == 0 ? ByteCounter : $"{ByteCounter} - {statuses}";
        }
    }

    private void OnDraftChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(DraftEditorViewModel.Text)) { OnPropertyChanged(nameof(Text)); RefreshText(); }
        if (args.PropertyName == nameof(DraftEditorViewModel.CanEdit)) OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(StatusLine));
    }

    private void RefreshText()
    {
        _processedText = _processor.Process(Text, Context, Options);
        OnPropertyChanged(nameof(ProcessedText));
        OnPropertyChanged(nameof(ByteCounter));
        OnPropertyChanged(nameof(ValidationMessage));
        OnPropertyChanged(nameof(IsReadyForSend));
        OnPropertyChanged(nameof(StatusLine));
    }

    public string? ValidationMessage => ProcessedText.Validation.MaxUtf8Bytes is <= 0
        ? "Имя ноды исчерпало лимит канального сообщения"
        : ProcessedText.Validation.MaxUtf8Bytes is null
            ? ProcessedText.Validation.Error is TextMessageValidationError.ContainsNul or TextMessageValidationError.InvalidUtf16
                ? TextError : "Лимит канала неизвестен"
            : TextError;

    private string? TextError => ProcessedText.Validation.Error switch
    {
        TextMessageValidationError.ContainsNul => "Текст содержит запрещённый NUL",
        TextMessageValidationError.InvalidUtf16 => "Некорректный Unicode в тексте",
        TextMessageValidationError.ExceedsLimit => $"Превышен лимит на {ProcessedText.Validation.Utf8ByteCount - ProcessedText.Validation.MaxUtf8Bytes} байт",
        _ => null,
    };

    private string? ReadinessMessage => Readiness switch
    {
        SendReadiness.Ready => null,
        SendReadiness.Offline => "offline",
        SendReadiness.Synchronizing => "Синхронизация ноды",
        SendReadiness.NotOnline => "Нода не готова к отправке",
        SendReadiness.WrongNode => "Чат другой ноды",
        SendReadiness.NoRecipient => "Выберите адресата",
        SendReadiness.UnsupportedRecipient => "Адресат не разрешён для отправки",
        SendReadiness.ContactMissing => "Контакт отсутствует на ноде",
        SendReadiness.AmbiguousPrefix => "Неоднозначный префикс ключа адресата",
        SendReadiness.ChannelMissing => "У канала нет активного слота",
        SendReadiness.ChooseSlot => "Нужно выбрать слот канала",
        SendReadiness.StaleBinding => "Привязка канального слота изменилась",
        SendReadiness.Checking => "Проверка адресата…",
        _ => "Не удалось проверить адресата",
    };
}
