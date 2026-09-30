using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Lifecycle;

namespace MeshCoreMessenger.Desktop.ViewModels;

public enum MessengerNavigationTab
{
    Personal = 0,
    Channels = 1,
    Devices = 2,
}

public sealed record MessengerNavigationTabItem(MessengerNavigationTab Tab, string Title);

public enum ChannelAccessFilter
{
    All = 0,
    PublicOrHashtag = 1,
    SharedSecret = 2,
    Unknown = 3,
}

public sealed record ChannelAccessFilterItem(ChannelAccessFilter Filter, string Title);

/// <summary>Owns the node-scoped, read-only directory and current short history projection.</summary>
public sealed class ConversationNavigationViewModel : ObservableObject
{
    private const int DirectoryPageSize = 100;
    private const int MessagePageSize = 100;
    private readonly IConversationDirectoryReader _directory;
    private readonly ILocalHistoryReader _history;
    private readonly ISettingsStore _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly SemaphoreSlim _persistence = new(1, 1);
    private readonly List<ConversationListItem> _loadedPrimary = [];
    private readonly List<ConversationListItem> _loadedUnknown = [];
    private Guid? _nodeId;
    private MessengerNavigationTabItem _selectedTab;
    private ConversationListItem? _selectedConversation;
    private ConversationDirectoryCursor? _primaryCursor;
    private ConversationDirectoryCursor? _unknownCursor;
    private ContactDetailsProjection? _contactDetails;
    private ChannelDetailsProjection? _channelDetails;
    private string _status = "Выберите ноду для просмотра истории";
    private string? _errorMessage;
    private bool _isNarrowLayout;
    private bool _isShowingDetail;
    private ChannelAccessFilterItem _selectedChannelFilter;
    private long _contextVersion;
    private int _stopped;

    public ConversationNavigationViewModel(
        IConversationDirectoryReader directory,
        ILocalHistoryReader history,
        ISettingsStore settings,
        IUiDispatcher dispatcher,
        ILogger logger)
    {
        _directory = directory;
        _history = history;
        _settings = settings;
        _dispatcher = dispatcher;
        _logger = logger;
        Tabs =
        [
            new(MessengerNavigationTab.Personal, "Личные"),
            new(MessengerNavigationTab.Channels, "Каналы"),
            new(MessengerNavigationTab.Devices, "Устройства"),
        ];
        _selectedTab = Tabs[0];
        ChannelFilters =
        [
            new(ChannelAccessFilter.All, "Все каналы"),
            new(ChannelAccessFilter.PublicOrHashtag, "Публичные / hashtag"),
            new(ChannelAccessFilter.SharedSecret, "Общий секрет"),
            new(ChannelAccessFilter.Unknown, "Доступ неизвестен"),
        ];
        _selectedChannelFilter = ChannelFilters[0];
        LoadMoreCommand = new AsyncRelayCommand(LoadMoreAsync, () => CanLoadMore);
        BackCommand = new RelayCommand(ShowDirectory);
    }

    public IReadOnlyList<MessengerNavigationTabItem> Tabs { get; }
    public IReadOnlyList<ChannelAccessFilterItem> ChannelFilters { get; }
    public ObservableCollection<ConversationListItem> PrimaryConversations { get; } = [];
    public ObservableCollection<ConversationListItem> UnknownConversations { get; } = [];
    public ObservableCollection<ConversationListItem> Conversations { get; } = [];
    public ObservableCollection<HistoryMessageListItem> Messages { get; } = [];
    public IAsyncRelayCommand LoadMoreCommand { get; }
    public IRelayCommand BackCommand { get; }

    public MessengerNavigationTabItem SelectedTab
    {
        get => _selectedTab;
        private set => SetProperty(ref _selectedTab, value);
    }

    public ChannelAccessFilterItem SelectedChannelFilter
    {
        get => _selectedChannelFilter;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!ChannelFilters.Contains(value) || !SetProperty(ref _selectedChannelFilter, value))
            {
                return;
            }

            ApplyFilter();
        }
    }

    public bool IsChannelTab => SelectedTab.Tab == MessengerNavigationTab.Channels;

    public ConversationListItem? SelectedConversation
    {
        get => _selectedConversation;
        private set
        {
            if (SetProperty(ref _selectedConversation, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(HasEmptyHistory));
                OnPropertyChanged(nameof(SelectedTitle));
                OnPropertyChanged(nameof(SelectedIdentity));
                OnPropertyChanged(nameof(SelectedMetadata));
            }
        }
    }

    public ContactDetailsProjection? ContactDetails
    {
        get => _contactDetails;
        private set
        {
            if (SetProperty(ref _contactDetails, value))
            {
                OnPropertyChanged(nameof(SelectedMetadata));
            }
        }
    }

    public ChannelDetailsProjection? ChannelDetails
    {
        get => _channelDetails;
        private set
        {
            if (SetProperty(ref _channelDetails, value))
            {
                OnPropertyChanged(nameof(SelectedMetadata));
            }
        }
    }

    public bool HasPrimaryConversations => PrimaryConversations.Count > 0;
    public bool HasUnknownConversations => UnknownConversations.Count > 0;
    public bool HasSelection => SelectedConversation is not null;
    public bool HasMessages => Messages.Count > 0;
    public bool HasEmptyHistory => HasSelection && !HasMessages;
    public bool IsEmpty => Conversations.Count == 0;
    public bool CanLoadMore => _primaryCursor is not null || _unknownCursor is not null;
    public string PrimaryGroupTitle => SelectedTab.Tab switch
    {
        MessengerNavigationTab.Personal => "Контакты",
        MessengerNavigationTab.Channels => "Каналы",
        MessengerNavigationTab.Devices => "Служебные устройства",
        _ => string.Empty,
    };
    public string UnknownGroupTitle => SelectedTab.Tab == MessengerNavigationTab.Channels
        ? "Неопознанные каналы"
        : "Неопознанные контакты";
    public bool HasUnknownGroup => SelectedTab.Tab is not MessengerNavigationTab.Devices;
    public string EmptyText => SelectedTab.Tab switch
    {
        MessengerNavigationTab.Personal => "Личных диалогов пока нет",
        MessengerNavigationTab.Channels => "Каналов пока нет",
        MessengerNavigationTab.Devices => "Служебных устройств пока нет",
        _ => "Список пуст",
    };
    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }
    public bool HasError => ErrorMessage is not null;
    public string SelectedTitle => SelectedConversation?.Title ?? "Выберите диалог";
    public string SelectedIdentity => SelectedConversation?.IdentityHex ?? string.Empty;
    public string SelectedMetadata => DescribeSelectedMetadata();

    public bool IsNarrowLayout
    {
        get => _isNarrowLayout;
        private set
        {
            if (SetProperty(ref _isNarrowLayout, value))
            {
                OnPropertyChanged(nameof(IsDirectoryVisible));
                OnPropertyChanged(nameof(IsDetailVisible));
                OnPropertyChanged(nameof(CanNavigateBack));
            }
        }
    }

    public bool IsDirectoryVisible => !IsNarrowLayout || !_isShowingDetail;
    public bool IsDetailVisible => !IsNarrowLayout || _isShowingDetail;
    public bool CanNavigateBack => IsNarrowLayout && _isShowingDetail;

    public void SetNarrowLayout(bool value) => IsNarrowLayout = value;

    public void ClearNode()
    {
        Interlocked.Increment(ref _contextVersion);
        _nodeId = null;
        ApplyEmpty("Выберите ноду для просмотра истории");
    }

    public async Task LoadNodeAsync(
        Guid nodeId,
        CancellationToken cancellationToken = default,
        bool dispatchResult = true)
    {
        var version = Interlocked.Increment(ref _contextVersion);
        _nodeId = nodeId;
        using var linked = CreateLinkedCancellation(cancellationToken);
        var storedTab = await _settings.GetAsync(TabSettingKey(nodeId), linked.Token);
        var tab = Enum.TryParse<MessengerNavigationTab>(storedTab, out var parsed)
            ? parsed
            : MessengerNavigationTab.Personal;
        var selectedKey = await _settings.GetAsync(ConversationSettingKey(nodeId), linked.Token);
        var projection = await ReadProjectionAsync(nodeId, tab, selectedKey, linked.Token);
        await ApplyAsync(
            () =>
            {
                if (!IsCurrent(nodeId, version))
                {
                    return;
                }

                ApplyProjection(projection);
            },
            dispatchResult,
            linked.Token);
    }

    public async Task SelectTabAsync(
        MessengerNavigationTabItem tab,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (!Tabs.Contains(tab))
        {
            throw new ArgumentException("The tab is not part of this navigation model.", nameof(tab));
        }

        var nodeId = _nodeId;
        if (nodeId is null || tab.Tab == SelectedTab.Tab)
        {
            return;
        }

        var version = Interlocked.Increment(ref _contextVersion);
        using var linked = CreateLinkedCancellation(cancellationToken);
        var selectedKey = await _settings.GetAsync(ConversationSettingKey(nodeId.Value), linked.Token);
        var projection = await ReadProjectionAsync(nodeId.Value, tab.Tab, selectedKey, linked.Token);
        await _dispatcher.InvokeAsync(
            () =>
            {
                if (IsCurrent(nodeId.Value, version))
                {
                    ApplyProjection(projection);
                }
            },
            linked.Token);
        await PersistSelectionAsync(nodeId.Value, linked.Token);
    }

    public async Task SelectConversationAsync(
        ConversationListItem? conversation,
        CancellationToken cancellationToken = default)
    {
        var nodeId = _nodeId;
        if (conversation is not null && conversation.NodeId != nodeId)
        {
            throw new ArgumentException("The conversation does not belong to the viewed node.", nameof(conversation));
        }

        if (conversation?.StableKey == SelectedConversation?.StableKey)
        {
            ShowDetail();
            return;
        }

        var version = Interlocked.Increment(ref _contextVersion);
        using var linked = CreateLinkedCancellation(cancellationToken);
        var detail = nodeId is not null && conversation is not null
            ? await ReadSelectionAsync(nodeId.Value, conversation.Entry, linked.Token)
            : SelectionProjection.Empty;
        await _dispatcher.InvokeAsync(
            () =>
            {
                if (nodeId is null || !IsCurrent(nodeId.Value, version))
                {
                    return;
                }

                ApplySelection(conversation, detail);
                ShowDetail();
            },
            linked.Token);
        if (nodeId is not null)
        {
            await PersistSelectionAsync(nodeId.Value, linked.Token);
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var nodeId = _nodeId;
        if (nodeId is null)
        {
            return;
        }

        var version = Interlocked.Increment(ref _contextVersion);
        var selectedKey = SelectedConversation?.StableKey;
        var tab = SelectedTab.Tab;
        using var linked = CreateLinkedCancellation(cancellationToken);
        var projection = await ReadProjectionAsync(nodeId.Value, tab, selectedKey, linked.Token);
        await _dispatcher.InvokeAsync(
            () =>
            {
                if (IsCurrent(nodeId.Value, version))
                {
                    ApplyProjection(projection);
                }
            },
            linked.Token);
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        _lifetimeCancellation.Cancel();
        LoadMoreCommand.Cancel();
        if (LoadMoreCommand.ExecutionTask is { } execution)
        {
            try
            {
                await execution.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task LoadMoreAsync(CancellationToken cancellationToken)
    {
        var nodeId = _nodeId;
        if (nodeId is null)
        {
            return;
        }

        var version = Volatile.Read(ref _contextVersion);
        var tab = SelectedTab.Tab;
        var (primarySection, unknownSection) = Sections(tab);
        using var linked = CreateLinkedCancellation(cancellationToken);
        var primary = _primaryCursor is null
            ? null
            : await _directory.GetPageAsync(nodeId.Value, primarySection, _primaryCursor, DirectoryPageSize, linked.Token);
        var unknown = unknownSection is null || _unknownCursor is null
            ? null
            : await _directory.GetPageAsync(nodeId.Value, unknownSection.Value, _unknownCursor, DirectoryPageSize, linked.Token);
        await _dispatcher.InvokeAsync(
            () =>
            {
                if (!IsCurrent(nodeId.Value, version) || SelectedTab.Tab != tab)
                {
                    return;
                }

                AppendPage(_loadedPrimary, primary);
                AppendPage(_loadedUnknown, unknown);
                ApplyFilter();
                _primaryCursor = primary?.NextCursor;
                _unknownCursor = unknown?.NextCursor;
                RaiseListProperties();
            },
            linked.Token);
    }

    private async Task<NavigationProjection> ReadProjectionAsync(
        Guid nodeId,
        MessengerNavigationTab tab,
        string? selectedKey,
        CancellationToken cancellationToken)
    {
        var (primarySection, unknownSection) = Sections(tab);
        var primaryTask = _directory.GetPageAsync(nodeId, primarySection, null, DirectoryPageSize, cancellationToken);
        var unknownTask = unknownSection is { } section
            ? _directory.GetPageAsync(nodeId, section, null, DirectoryPageSize, cancellationToken)
            : Task.FromResult(new ConversationDirectoryPage([], null));
        await Task.WhenAll(primaryTask, unknownTask);
        var primary = await primaryTask;
        var unknown = await unknownTask;
        var entries = primary.Items.Concat(unknown.Items).ToArray();
        var selected = entries.FirstOrDefault(item => item.StableKey == selectedKey)
            ?? primary.Items.FirstOrDefault()
            ?? unknown.Items.FirstOrDefault();
        var selection = selected is null
            ? SelectionProjection.Empty
            : await ReadSelectionAsync(nodeId, selected, cancellationToken);
        return new(tab, primary, unknown, selected?.StableKey, selection);
    }

    private async Task<SelectionProjection> ReadSelectionAsync(
        Guid nodeId,
        ConversationDirectoryEntry entry,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<HistoryMessage> messages = entry.ConversationId is { } conversationId
            ? await _history.GetMessagesAsync(nodeId, conversationId, null, MessagePageSize, cancellationToken)
            : [];
        ContactDetailsProjection? contact = null;
        ChannelDetailsProjection? channel = null;
        if (entry.Section is ConversationDirectorySection.ChatContacts or ConversationDirectorySection.ServiceContacts)
        {
            contact = await _directory.GetContactDetailsAsync(nodeId, entry.Identity, cancellationToken);
        }
        else if (entry.Section == ConversationDirectorySection.Channels)
        {
            channel = await _directory.GetChannelDetailsAsync(nodeId, entry.Identity, cancellationToken);
        }

        return new(messages, contact, channel);
    }

    private void ApplyProjection(NavigationProjection projection)
    {
        SelectedTab = Tabs.Single(item => item.Tab == projection.Tab);
        Replace(_loadedPrimary, projection.Primary.Items);
        Replace(_loadedUnknown, projection.Unknown.Items);
        ApplyFilter();
        _primaryCursor = projection.Primary.NextCursor;
        _unknownCursor = projection.Unknown.NextCursor;
        var selected = projection.SelectedStableKey is { } key
            ? Conversations.FirstOrDefault(item => item.StableKey == key)
            : null;
        ApplySelection(selected, projection.Selection);
        ErrorMessage = null;
        Status = Conversations.Count == 0
            ? EmptyText
            : $"Найдено: {Conversations.Count}";
        RaiseListProperties();
    }

    private void ApplySelection(ConversationListItem? conversation, SelectionProjection projection)
    {
        SelectedConversation = conversation;
        ContactDetails = projection.Contact;
        ChannelDetails = projection.Channel;
        Messages.Clear();
        foreach (var message in projection.Messages)
        {
            Messages.Add(new HistoryMessageListItem(message));
        }
        OnPropertyChanged(nameof(HasMessages));
        OnPropertyChanged(nameof(HasEmptyHistory));
    }

    private void ApplyEmpty(string status)
    {
        PrimaryConversations.Clear();
        UnknownConversations.Clear();
        Conversations.Clear();
        _loadedPrimary.Clear();
        _loadedUnknown.Clear();
        Messages.Clear();
        OnPropertyChanged(nameof(HasMessages));
        OnPropertyChanged(nameof(HasEmptyHistory));
        SelectedConversation = null;
        ContactDetails = null;
        ChannelDetails = null;
        _primaryCursor = null;
        _unknownCursor = null;
        Status = status;
        ErrorMessage = null;
        _isShowingDetail = false;
        RaiseListProperties();
        OnPropertyChanged(nameof(IsDirectoryVisible));
        OnPropertyChanged(nameof(IsDetailVisible));
        OnPropertyChanged(nameof(CanNavigateBack));
    }

    private static void Replace(
        List<ConversationListItem> target,
        IReadOnlyList<ConversationDirectoryEntry> entries)
    {
        target.Clear();
        foreach (var entry in entries)
        {
            target.Add(new ConversationListItem(entry));
        }
    }

    private static void AppendPage(
        List<ConversationListItem> target,
        ConversationDirectoryPage? page)
    {
        if (page is null)
        {
            return;
        }

        var existing = target.Select(item => item.StableKey).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in page.Items)
        {
            if (existing.Add(entry.StableKey))
            {
                target.Add(new ConversationListItem(entry));
            }
        }
    }

    private void RebuildCombined()
    {
        Conversations.Clear();
        foreach (var item in PrimaryConversations.Concat(UnknownConversations))
        {
            Conversations.Add(item);
        }
    }

    private void ApplyFilter()
    {
        PrimaryConversations.Clear();
        foreach (var item in _loadedPrimary.Where(IsVisibleByFilter))
        {
            PrimaryConversations.Add(item);
        }

        UnknownConversations.Clear();
        foreach (var item in _loadedUnknown)
        {
            UnknownConversations.Add(item);
        }

        RebuildCombined();
        RaiseListProperties();
    }

    private bool IsVisibleByFilter(ConversationListItem item)
    {
        if (SelectedTab.Tab != MessengerNavigationTab.Channels ||
            SelectedChannelFilter.Filter == ChannelAccessFilter.All)
        {
            return true;
        }

        return SelectedChannelFilter.Filter switch
        {
            ChannelAccessFilter.PublicOrHashtag =>
                item.Entry.ChannelAccessKind == ChannelAccessKind.PublicOrHashtag,
            ChannelAccessFilter.SharedSecret =>
                item.Entry.ChannelAccessKind == ChannelAccessKind.SharedSecret,
            ChannelAccessFilter.Unknown =>
                item.Entry.ChannelAccessKind is null or ChannelAccessKind.Unknown,
            _ => true,
        };
    }

    private void RaiseListProperties()
    {
        OnPropertyChanged(nameof(HasPrimaryConversations));
        OnPropertyChanged(nameof(HasUnknownConversations));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanLoadMore));
        OnPropertyChanged(nameof(PrimaryGroupTitle));
        OnPropertyChanged(nameof(UnknownGroupTitle));
        OnPropertyChanged(nameof(HasUnknownGroup));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(IsChannelTab));
        LoadMoreCommand.NotifyCanExecuteChanged();
    }

    private void ShowDetail()
    {
        _isShowingDetail = SelectedConversation is not null;
        OnPropertyChanged(nameof(IsDirectoryVisible));
        OnPropertyChanged(nameof(IsDetailVisible));
        OnPropertyChanged(nameof(CanNavigateBack));
    }

    private void ShowDirectory()
    {
        _isShowingDetail = false;
        OnPropertyChanged(nameof(IsDirectoryVisible));
        OnPropertyChanged(nameof(IsDetailVisible));
        OnPropertyChanged(nameof(CanNavigateBack));
    }

    private async Task PersistSelectionAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        await _persistence.WaitAsync(cancellationToken);
        try
        {
            if (_nodeId != nodeId)
            {
                return;
            }

            await _settings.SetAsync(TabSettingKey(nodeId), SelectedTab.Tab.ToString(), cancellationToken);
            await _settings.SetAsync(
                ConversationSettingKey(nodeId),
                SelectedConversation?.StableKey ?? string.Empty,
                cancellationToken);
        }
        finally
        {
            _persistence.Release();
        }
    }

    private string DescribeSelectedMetadata()
    {
        if (SelectedConversation is null)
        {
            return string.Empty;
        }

        if (ContactDetails is { } contact)
        {
            var presence = contact.PresentOnNode ? "есть в справочнике ноды" : "сохранён в истории";
            var advert = contact.LastAdvertUtc is { } lastAdvert
                ? $"advert: {lastAdvert.ToLocalTime():g}"
                : "advert: неизвестно";
            var location = contact.Latitude is { } latitude && contact.Longitude is { } longitude
                ? $"координаты: {latitude:F5}, {longitude:F5}"
                : "координаты: неизвестны";
            var route = contact.OutPath is { Length: > 0 } outPath
                ? $"маршрут: {Convert.ToHexString(outPath).ToLowerInvariant()}"
                : "маршрут: неизвестен";
            return $"{SelectedConversation.TypeLabel} · {presence} · {advert} · {location} · {route}";
        }

        if (ChannelDetails is { } channel)
        {
            var slots = channel.ActiveSlots.Count == 0
                ? "нет активного слота"
                : $"слоты: {string.Join(", ", channel.ActiveSlots)}";
            return $"{SelectedConversation.AccessLabel} · {slots}";
        }

        return SelectedConversation.TypeLabel;
    }

    private Task ApplyAsync(Action action, bool dispatch, CancellationToken cancellationToken)
    {
        if (dispatch)
        {
            return _dispatcher.InvokeAsync(action, cancellationToken);
        }

        action();
        return Task.CompletedTask;
    }

    private CancellationTokenSource CreateLinkedCancellation(CancellationToken cancellationToken) =>
        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCancellation.Token);

    private bool IsCurrent(Guid nodeId, long version) =>
        Volatile.Read(ref _stopped) == 0 &&
        _nodeId == nodeId &&
        Volatile.Read(ref _contextVersion) == version;

    private static (ConversationDirectorySection Primary, ConversationDirectorySection? Unknown) Sections(
        MessengerNavigationTab tab) => tab switch
    {
        MessengerNavigationTab.Personal =>
            (ConversationDirectorySection.ChatContacts, ConversationDirectorySection.UnknownContacts),
        MessengerNavigationTab.Channels =>
            (ConversationDirectorySection.Channels, ConversationDirectorySection.UnknownChannels),
        MessengerNavigationTab.Devices =>
            (ConversationDirectorySection.ServiceContacts, null),
        _ => throw new ArgumentOutOfRangeException(nameof(tab)),
    };

    internal static string TabSettingKey(Guid nodeId) => $"desktop.node.{nodeId:D}.navigation-tab";
    internal static string ConversationSettingKey(Guid nodeId) => $"desktop.node.{nodeId:D}.conversation-key";

    private sealed record NavigationProjection(
        MessengerNavigationTab Tab,
        ConversationDirectoryPage Primary,
        ConversationDirectoryPage Unknown,
        string? SelectedStableKey,
        SelectionProjection Selection);

    private sealed record SelectionProjection(
        IReadOnlyList<HistoryMessage> Messages,
        ContactDetailsProjection? Contact,
        ChannelDetailsProjection? Channel)
    {
        public static SelectionProjection Empty { get; } = new([], null, null);
    }
}

public sealed class ConversationListItem
{
    public ConversationListItem(ConversationDirectoryEntry entry)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        Id = entry.ConversationId;
        NodeId = entry.NodeId;
        StableKey = entry.StableKey;
        Section = entry.Section;
        Kind = entry.Kind;
        IdentityHex = Convert.ToHexString(entry.Identity).ToLowerInvariant();
        Title = string.IsNullOrWhiteSpace(entry.DisplayName)
            ? entry.Section switch
            {
                ConversationDirectorySection.Channels or ConversationDirectorySection.UnknownChannels =>
                    "Канал без названия",
                ConversationDirectorySection.ServiceContacts => "Устройство без имени",
                _ => "Неизвестный контакт",
            }
            : entry.DisplayName;
        Preview = entry.LastMessageKind switch
        {
            StoredMessageKind.Binary => "Двоичное сообщение",
            StoredMessageKind.Text when !string.IsNullOrEmpty(entry.LastMessageText) => entry.LastMessageText,
            _ => "Нет сообщений",
        };
        ActivityTime = entry.ActivityUtc.ToLocalTime().ToString("g");
        TypeLabel = DescribeType(entry);
        AccessLabel = entry.ChannelAccessKind switch
        {
            ChannelAccessKind.PublicOrHashtag => "Публичный / hashtag",
            ChannelAccessKind.SharedSecret => "Общий секрет",
            _ => "Тип доступа неизвестен",
        };
    }

    internal ConversationDirectoryEntry Entry { get; }
    public Guid? Id { get; }
    public Guid NodeId { get; }
    public string StableKey { get; }
    public ConversationDirectorySection Section { get; }
    public ConversationKind Kind { get; }
    public string IdentityHex { get; }
    public string Title { get; }
    public string Preview { get; }
    public string ActivityTime { get; }
    public string TypeLabel { get; }
    public string AccessLabel { get; }
    public bool IsPresentOnNode => Entry.PresentOnNode is not false;
    public bool HasConversation => Id is not null;

    private static string DescribeType(ConversationDirectoryEntry entry) => entry.Section switch
    {
        ConversationDirectorySection.UnknownContacts => "Неопознанный контакт",
        ConversationDirectorySection.UnknownChannels => "Неопознанный канал",
        ConversationDirectorySection.Channels => "Канал",
        ConversationDirectorySection.ChatContacts => "Личный контакт",
        ConversationDirectorySection.ServiceContacts => entry.ContactType switch
        {
            0 => "Без типа",
            2 => "Ретранслятор",
            3 => "Комната",
            4 => "Датчик",
            _ => $"Устройство (тип {entry.ContactType?.ToString() ?? "?"})",
        },
        _ => "Диалог",
    };
}

public sealed class HistoryMessageListItem
{
    public HistoryMessageListItem(HistoryMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        LocalSequence = message.LocalSequence;
        Direction = message.Direction == MessageDirection.Outgoing ? "Вы" : "Входящее";
        Body = message.MessageKind == StoredMessageKind.Binary
            ? "Двоичное сообщение"
            : message.Text ?? string.Empty;
        ReceivedTime = message.ReceivedUtc.ToLocalTime().ToString("g");
    }

    public long LocalSequence { get; }
    public string Direction { get; }
    public string Body { get; }
    public string ReceivedTime { get; }
}
