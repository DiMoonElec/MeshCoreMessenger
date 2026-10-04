using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Lifecycle;

namespace MeshCoreMessenger.Desktop.ViewModels;

/// <summary>Read-only local facts for a captured node/full key, independent of later chat selection.</summary>
public sealed class ContactDetailsCardViewModel : ModalCardViewModel
{
    private readonly Guid _nodeId;
    private readonly byte[] _key;
    private readonly IConversationDirectoryReader _directory;
    private readonly IDirectoryStore? _updates;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<Task, Task> _track;
    private DeviceDetailsViewModel? _details;
    private string _route = "Данные не получены";
    private string? _error;
    private bool _loading;
    private long _revision;
    private bool _disposed;

    public ContactDetailsCardViewModel(Guid nodeId, ReadOnlyMemory<byte> publicKey,
        IConversationDirectoryReader directory, IDirectoryStore? updates, IUiDispatcher dispatcher,
        ILogger logger, Func<Task, Task> track) : base("О контакте")
    {
        if (publicKey.Length != 32) throw new ArgumentException("A full contact key is required.", nameof(publicKey));
        _nodeId = nodeId; _key = publicKey.ToArray(); _directory = directory; _updates = updates;
        _dispatcher = dispatcher; _logger = logger; _track = track;
        RetryCommand = new AsyncRelayCommand(() => _track(RefreshAsync()));
        if (_updates is not null) _updates.ContactRouteCommitted += OnRouteCommitted;
    }
    public DeviceDetailsViewModel? Details { get => _details; private set { if (SetProperty(ref _details, value)) OnPropertyChanged(nameof(HasDetails)); } }
    public bool HasDetails => Details is not null;
    public string PublicKey => Convert.ToHexString(_key).ToLowerInvariant();
    public string Route { get => _route; private set => SetProperty(ref _route, value); }
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
            var details = await _directory.GetContactDetailsAsync(_nodeId, _key, token).ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() =>
            {
                if (_disposed || revision != _revision) return;
                Details = details is null ? null : new(details);
                Route = details is null ? "Данные не получены" : ContactRouteFormatter.Format(details.OutPathLength, details.OutPath);
                if (details is null) Error = "Подробные сведения о контакте пока не сохранены.";
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to read contact card for node {NodeId}", _nodeId);
            await _dispatcher.InvokeAsync(() => { if (!_disposed && revision == _revision) Error = "Не удалось прочитать сведения. Попробуйте ещё раз."; }).ConfigureAwait(false);
        }
        finally
        {
            await _dispatcher.InvokeAsync(() => { if (!_disposed && revision == _revision) IsLoading = false; }).ConfigureAwait(false);
        }
    }
    private void OnRouteCommitted(object? sender, ContactRouteCommit commit)
    {
        if (commit.NodeId != _nodeId || !commit.PublicKey.Span.SequenceEqual(_key)) return;
        _ = _track(_dispatcher.InvokeAsync(() => { if (!_disposed) _ = _track(RefreshAsync()); }));
    }
    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true; ++_revision;
        if (_updates is not null) _updates.ContactRouteCommitted -= OnRouteCommitted;
        _lifetime.Cancel(); _lifetime.Dispose();
    }
}
