using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Lifecycle;

namespace MeshCoreMessenger.Desktop.ViewModels;

/// <summary>Local channel metadata scoped to a captured node and fingerprint, never to a mutable slot.</summary>
public sealed class ChannelDetailsCardViewModel : ModalCardViewModel
{
    private readonly Guid _nodeId;
    private readonly byte[] _fingerprint;
    private readonly IConversationDirectoryReader _directory;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private ChannelDetailsViewModel? _details;
    private string? _error;
    private bool _loading;
    private bool _disposed;
    private long _revision;

    public ChannelDetailsCardViewModel(Guid nodeId, ReadOnlyMemory<byte> fingerprint,
        IConversationDirectoryReader directory, IUiDispatcher dispatcher, ILogger logger, Func<Task, Task> track) : base("О канале")
    {
        if (fingerprint.Length != 32) throw new ArgumentException("A full channel fingerprint is required.", nameof(fingerprint));
        _nodeId = nodeId; _fingerprint = fingerprint.ToArray();
        _directory = directory; _dispatcher = dispatcher; _logger = logger;
        RetryCommand = new AsyncRelayCommand(() => track(RefreshAsync()));
    }
    public ChannelDetailsViewModel? Details { get => _details; private set { if (SetProperty(ref _details, value)) OnPropertyChanged(nameof(HasDetails)); } }
    public bool HasDetails => Details is not null;
    public string Fingerprint => Convert.ToHexString(_fingerprint).ToLowerInvariant();
    public string? Error { get => _error; private set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => Error is not null;
    public bool IsLoading { get => _loading; private set => SetProperty(ref _loading, value); }
    public AsyncRelayCommand RetryCommand { get; }

    public async Task RefreshAsync()
    {
        if (_disposed) return;
        var revision = ++_revision;
        var token = _lifetime.Token;
        IsLoading = true; Error = null;
        try
        {
            var details = await _directory.GetChannelDetailsAsync(_nodeId, _fingerprint, token).ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() =>
            {
                if (_disposed || revision != _revision) return;
                Details = details is null ? null : new(details);
                if (details is null) Error = "Подробные сведения о канале пока не сохранены.";
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to read channel card for node {NodeId}", _nodeId);
            await _dispatcher.InvokeAsync(() => { if (!_disposed && revision == _revision) Error = "Не удалось прочитать сведения. Попробуйте ещё раз."; }).ConfigureAwait(false);
        }
        finally { await _dispatcher.InvokeAsync(() => { if (!_disposed && revision == _revision) IsLoading = false; }).ConfigureAwait(false); }
    }
    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true; ++_revision;
        _lifetime.Cancel(); _lifetime.Dispose();
    }
}

public sealed class ChannelDetailsViewModel(ChannelDetailsProjection details)
{
    public string Name { get; } = string.IsNullOrWhiteSpace(details.DisplayName) ? "Канал без названия" : details.DisplayName;
    public string Access { get; } = details.AccessKind switch
    {
        ChannelAccessKind.PublicOrHashtag => "Публичный / hashtag",
        ChannelAccessKind.SharedSecret => "Общий секрет",
        _ => "Тип доступа неизвестен",
    };
    public string Presence { get; } = details.ActiveSlots.Count > 0 ? "Настроен на ноде" : "Сохранён локально · сейчас не настроен на ноде";
    public string Slots { get; } = details.ActiveSlots.Count > 0 ? string.Join(", ", details.ActiveSlots.Order()) : "Нет активных слотов";
    public string Created { get; } = details.CreatedUtc.ToLocalTime().ToString("g");
    public string Updated { get; } = details.UpdatedUtc.ToLocalTime().ToString("g");
}
