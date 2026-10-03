using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeshCoreMessenger.Core.Domain;
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
        SendCommand = new AsyncRelayCommand(SendAsync, () => CanSend);
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
    private Func<Task>? _send;
    private bool _sending;
    private bool _stopped;
    private string? _sendError;
    private byte? _selectedSlot;
    public IReadOnlyList<byte> ChannelSlots { get; private set; } = [];
    public bool HasSlotChoice => ChannelSlots.Count > 1;
    public byte? SelectedSlot
    {
        get => _selectedSlot;
        set { if (SetProperty(ref _selectedSlot, value)) InvalidateSend(); }
    }
    internal ChannelSendRequest? SendCapture { get; private set; }
    public IAsyncRelayCommand SendCommand { get; }
    public string SendTooltip => Context.IsChannel ? "Отправить в канал (Enter). Новая строка: Shift+Enter." : "Личная отправка будет подключена в D6.";
    public bool CanSend => !_stopped && !_sending && _send is not null && SendCapture is not null &&
        IsReadyForSend && !string.IsNullOrWhiteSpace(Text);
    internal void ConfigureSend(Func<Task> send) { _send = send; InvalidateSend(); }
    internal void SetSendContext(Guid nodeId, Guid sessionId, long generation, IReadOnlyList<OutgoingRecipient> targets)
    {
        var previousSlot = _selectedSlot;
        var slots = targets.Select(t => t.Slot!.Value).ToArray();
        var slotsChanged = !ChannelSlots.SequenceEqual(slots);
        if (slotsChanged) ChannelSlots = slots;
        if (ChannelSlots.Count == 1) _selectedSlot = ChannelSlots[0];
        else if (_selectedSlot is not null && !ChannelSlots.Contains(_selectedSlot.Value)) _selectedSlot = null;
        var target = targets.SingleOrDefault(t => t.Slot == _selectedSlot);
        SendCapture = target is null || Draft?.CanEdit != true ? null : new(nodeId, sessionId, generation, target, Draft.Capture(), Options);
        if (slotsChanged)
        {
            OnPropertyChanged(nameof(ChannelSlots));
            OnPropertyChanged(nameof(HasSlotChoice));
        }
        if (previousSlot != _selectedSlot) OnPropertyChanged(nameof(SelectedSlot));
        InvalidateSend();
    }
    internal void ResetSendContext(bool clearSlots = false)
    {
        SendCapture = null;
        if (clearSlots)
        {
            ChannelSlots = [];
            _selectedSlot = null;
            OnPropertyChanged(nameof(ChannelSlots)); OnPropertyChanged(nameof(HasSlotChoice)); OnPropertyChanged(nameof(SelectedSlot));
        }
        InvalidateSend();
    }
    internal void StopSend() { _stopped = true; InvalidateSend(); }
    private async Task SendAsync()
    {
        if (!CanSend) return;
        _sending = true; _sendError = null; InvalidateSend();
        try { await _send!(); }
        catch (Exception) { _sendError = "Не удалось завершить отправку. Проверьте статус сообщения и подключения."; }
        finally { _sending = false; InvalidateSend(); }
    }
    private void InvalidateSend()
    {
        OnPropertyChanged(nameof(CanSend)); OnPropertyChanged(nameof(StatusLine));
        SendCommand.NotifyCanExecuteChanged();
    }
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
            InvalidateSend();
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
            var statuses = string.Join(" • ", new[] { ReadinessMessage, ValidationMessage, explanation, _sendError }.Where(s => !string.IsNullOrWhiteSpace(s)));
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
        InvalidateSend();
        OnPropertyChanged(nameof(SendTooltip));
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
