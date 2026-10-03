using Microsoft.Extensions.Logging.Abstractions;
using MeshCoreMessenger.Core.Application;
using MeshCoreMessenger.Core.Domain;
using MeshCoreMessenger.Core.Persistence;
using MeshCoreMessenger.Desktop.Lifecycle;
using MeshCoreMessenger.Desktop.ViewModels;
using Xunit;

namespace MeshCoreMessenger.Desktop.Tests;

public sealed class DraftEditorViewModelTests
{
    private static readonly Guid NodeA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid NodeB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task FastChatAndNodeSwitchesRestoreEachOwnersNewestAcceptedText()
    {
        var drafts = new FakeDraftBuffer();
        var model = Create(drafts);
        var chatA = Item(NodeA, "a", ConversationDirectorySection.ChatContacts, 1);
        var chatB = Item(NodeA, "b", ConversationDirectorySection.ChatContacts, 2);
        var otherNode = Item(NodeB, "a", ConversationDirectorySection.ChatContacts, 1);

        await model.OpenAsync(chatA, CancellationToken, dispatchResult: false);
        model.Text = "A new 🐈";
        await model.OpenAsync(chatB, CancellationToken, dispatchResult: false);
        model.Text = "B new";
        await model.OpenAsync(chatA, CancellationToken, dispatchResult: false);
        Assert.Equal("A new 🐈", model.Text);

        await model.OpenAsync(otherNode, CancellationToken, dispatchResult: false);
        model.Text = "other node";
        await model.OpenAsync(chatA, CancellationToken, dispatchResult: false);

        Assert.Equal("A new 🐈", model.Text);
        Assert.Contains(drafts.Flushes, target => target.NodeId == NodeA && target.Identity[0] == 1);
        Assert.Contains(drafts.Flushes, target => target.NodeId == NodeB);
        await model.StopAsync();
    }

    [Fact]
    public async Task OnlyChatAndKnownChannelExposeEditor()
    {
        var model = Create(new FakeDraftBuffer());

        await model.OpenAsync(
            Item(NodeA, "service", ConversationDirectorySection.ServiceContacts, 3),
            CancellationToken,
            dispatchResult: false);
        Assert.False(model.CanEdit);
        await model.OpenAsync(
            Item(NodeA, "unknown", ConversationDirectorySection.UnknownContacts, 4),
            CancellationToken,
            dispatchResult: false);
        Assert.False(model.CanEdit);
        await model.OpenAsync(
            Item(NodeA, "channel", ConversationDirectorySection.Channels, 5),
            CancellationToken,
            dispatchResult: false);
        Assert.True(model.CanEdit);
        await model.StopAsync();
    }

    [Fact]
    public async Task DebounceIsInjectableAndStopKeepsLastTextAcceptedForShutdownBarrier()
    {
        var drafts = new FakeDraftBuffer();
        var delay = new ControlledDraftDelay();
        var model = Create(drafts, delay);
        await model.OpenAsync(
            Item(NodeA, "chat", ConversationDirectorySection.ChatContacts, 6),
            CancellationToken,
            dispatchResult: false);

        model.Text = "first";
        model.Text = "last before close";

        Assert.Equal(2, delay.Requests.Count);
        Assert.True(delay.Requests[0].CancellationToken.IsCancellationRequested);
        Assert.Equal(DraftEditorViewModel.DebounceDelay, delay.Requests[1].Delay);
        Assert.Equal("last before close", drafts.Updates[^1].Text);
        await model.StopAsync();
        Assert.True(delay.Requests[1].CancellationToken.IsCancellationRequested);
        Assert.Equal("last before close", drafts.Updates[^1].Text);
    }

    [Fact]
    public async Task LateLoadCannotOverwriteTextTypedIntoTheNewContext()
    {
        var drafts = new GatedDraftBuffer();
        var model = Create(drafts);
        var chat = Item(NodeA, "chat", ConversationDirectorySection.ChatContacts, 7);

        var open = model.OpenAsync(chat, CancellationToken, dispatchResult: false);
        await drafts.LoadStarted.Task.WaitAsync(CancellationToken);
        model.Text = "typed while loading";
        drafts.LoadGate.SetResult("old persisted value");
        await open;

        Assert.Equal("typed while loading", model.Text);
        await model.StopAsync();
    }

    [Fact]
    public async Task ShutdownBarrierPersistsTextAcceptedBeforeDebounceElapsed()
    {
        var store = new MemoryDraftStore();
        var tracker = new DraftWriteTracker(store, TimeProvider.System);
        var delay = new ControlledDraftDelay();
        var model = Create(tracker, delay);
        await model.OpenAsync(
            Item(NodeA, "chat", ConversationDirectorySection.ChatContacts, 8),
            CancellationToken,
            dispatchResult: false);
        model.Text = "последний символ перед закрытием";

        await model.StopAsync();
        await ((IDurableDraftWrites)tracker).FlushAsync(CancellationToken);

        Assert.Equal("последний символ перед закрытием", store.Saved?.Text);
    }

    [Fact]
    public async Task ComposerKeepsDraftOwnershipAndFullOfflineUnicodeTextWithoutEnablingSend()
    {
        var drafts = new FakeDraftBuffer();
        var editor = Create(drafts, new ControlledDraftDelay());
        var composer = new ComposerViewModel(editor);
        await editor.OpenAsync(Item(NodeA, "chat", ConversationDirectorySection.ChatContacts, 1),
            CancellationToken, dispatchResult: false);
        var text = string.Concat(Enumerable.Repeat("Кириллица 👋\n", 200));
        composer.Text = text;
        Assert.Equal(text, editor.Text);
        Assert.Equal(text, drafts.Updates.Single().Text);
        Assert.True(composer.CanEdit);
        Assert.False(composer.CanSend);
        Assert.Contains("Черновик не сохранён", composer.StatusLine);
        Assert.Null(composer.ByteCounter); // no invented production byte budget before D2
        await editor.OpenAsync(Item(NodeA, "other", ConversationDirectorySection.ChatContacts, 2),
            CancellationToken, dispatchResult: false);
        Assert.Equal(string.Empty, composer.Text);
        await editor.OpenAsync(Item(NodeA, "chat", ConversationDirectorySection.ChatContacts, 1),
            CancellationToken, dispatchResult: false);
        Assert.Equal(text, composer.Text);
        await editor.StopAsync();
    }

    [Fact]
    public async Task ComposerExposesDraftPersistenceFailureWhileSendRemainsDisabled()
    {
        var drafts = new FakeDraftBuffer { FlushFailure = new IOException("test failure") };
        var delay = new ControlledDraftDelay();
        var editor = Create(drafts, delay);
        var composer = new ComposerViewModel(editor);
        await editor.OpenAsync(Item(NodeA, "chat", ConversationDirectorySection.ChatContacts, 1),
            CancellationToken, dispatchResult: false);
        composer.Text = "не потерять 🐈";
        delay.Requests.Single().Completion.SetResult();
        for (var attempt = 0; attempt < 200 && !editor.HasError; attempt++) await Task.Delay(10, CancellationToken);
        Assert.True(editor.HasError);
        Assert.Contains("Не удалось сохранить черновик", composer.StatusLine);
        Assert.Equal("не потерять 🐈", composer.Text);
        Assert.False(composer.CanSend);
        await editor.StopAsync();
    }

    private static DraftEditorViewModel Create(
        IDraftBuffer drafts,
        IDraftDelay? delay = null) =>
        new(
            drafts,
            delay ?? new ImmediateDraftDelay(),
            new ImmediateUiDispatcher(),
            NullLogger.Instance);

    private static ConversationListItem Item(
        Guid nodeId,
        string key,
        ConversationDirectorySection section,
        byte identity) =>
        new(new ConversationDirectoryEntry(
            nodeId,
            key,
            section,
            section == ConversationDirectorySection.Channels
                ? ConversationKind.Channel
                : section == ConversationDirectorySection.UnknownContacts
                    ? ConversationKind.UnknownContact
                    : ConversationKind.Contact,
            Enumerable.Repeat(identity, 32).ToArray(),
            null,
            key,
            section == ConversationDirectorySection.ServiceContacts ? 2 : 1,
            true,
            section == ConversationDirectorySection.Channels
                ? ChannelAccessKind.PublicOrHashtag
                : null,
            [],
            false,
            DateTimeOffset.UnixEpoch,
            0,
            DateTimeOffset.UnixEpoch,
            null,
            null,
            null,
            null,
            null,
            null,
            0));

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private sealed class GatedDraftBuffer : IDraftBuffer
    {
        public TaskCompletionSource LoadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> LoadGate { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> LoadTextAsync(
            DraftTarget target,
            CancellationToken cancellationToken = default)
        {
            LoadStarted.TrySetResult();
            return await LoadGate.Task.WaitAsync(cancellationToken);
        }

        public void Update(DraftTarget target, string text, long revision)
        {
        }

        public Task FlushAsync(
            DraftTarget target,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class MemoryDraftStore : IDraftStore
    {
        public DraftRecord? Saved { get; private set; }

        public Task<DraftRecord?> GetAsync(
            DraftTarget target,
            CancellationToken cancellationToken = default) => Task.FromResult(Saved);

        public Task<DraftRecord?> SaveAsync(
            DraftTarget target,
            string text,
            DateTimeOffset updatedUtc,
            CancellationToken cancellationToken = default)
        {
            Saved = text.Length == 0
                ? null
                : new DraftRecord(target.ConversationId ?? Guid.NewGuid(), text, updatedUtc);
            return Task.FromResult(Saved);
        }
    }
}
