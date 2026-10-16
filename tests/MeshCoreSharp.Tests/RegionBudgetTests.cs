using MeshCoreSharp;
using MeshCoreSharp.Models;
using MeshCoreSharp.Transport;
using MeshCoreSharp.Transport.Ble;
using static RegionChannelTests;

internal static class RegionBudgetTests
{
    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("REGION6 BLE scope overhead and UTF-8 budgets across MTUs", Formula),
        ("REGION6 public budget follows current-session default observations", Observations),
        ("REGION6 public budget equals send admission and result metadata", Admission),
        ("REGION6 text budget is rechecked after waiting for scope gate", ChangedBudget),
    ];
    private static void Check(bool condition) => RegionChannelTests.Check(condition);
    private static Task<T> Throws<T>(Func<Task> action) where T : Exception => RegionChannelTests.Throws<T>(action);
    private sealed class BudgetTransport : IMeshCoreTransport, IBleTextBudgetProvider
    {
        internal readonly TestTransport Inner = new();
        internal ushort Mtu = 160;
        internal bool LastScoped;
        public bool IsConnected => Inner.IsConnected;
        public Task ConnectAsync(CancellationToken ct = default) => Inner.ConnectAsync(ct);
        public Task DisconnectAsync(CancellationToken ct = default) => Inner.DisconnectAsync(ct);
        public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default) => Inner.SendAsync(frame, ct);
        public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken ct = default) => Inner.ReceiveAsync(ct);
        public ValueTask DisposeAsync() => Inner.DisposeAsync();
        public BleTextBudget GetTextBudget(BleTextMessageKind kind, string? senderName = null, bool regionScoped = true)
        { LastScoped = regionScoped; return BleTextBudget.Calculate(Mtu, kind, senderName, regionScoped); }
    }
    private static Task Formula()
    {
        foreach (var mtu in new ushort[] { 23, 160, 176, 247 })
        foreach (var kind in Enum.GetValues<BleTextMessageKind>())
        {
            var unknown = BleTextBudget.Calculate(mtu, kind, EffectiveFloodScope.Unknown, "Я🐈");
            var scoped = BleTextBudget.Calculate(mtu, kind, EffectiveFloodScope.Scoped, "Я🐈");
            var unscoped = BleTextBudget.Calculate(mtu, kind, EffectiveFloodScope.Unscoped, "Я🐈");
            Check(unknown == scoped && scoped.RegionOverheadBytes == 4 && unscoped.RegionOverheadBytes == 0);
            Check(scoped.SenderPrefixBytes == (kind == BleTextMessageKind.Channel ? 8 : 0));
            Check(scoped.MaximumTextBytes <= unscoped.MaximumTextBytes);
        }
        return Task.CompletedTask;
    }
    private static async Task Observations()
    {
        var transport = new BudgetTransport(); await using var client = new MeshCoreClient(transport,
            new MeshCoreClientOptions { AutoReceiveMessages = false }); await Start(client, transport.Inner);
        Check(client.GetTextBudget(TextMessageKind.Channel).Scope.EffectiveScope == EffectiveFloodScope.Unknown && transport.LastScoped);
        var get = client.GetDefaultFloodScopeAsync(); await Sent(transport.Inner); transport.Inner.Emit([0x1C]); await get;
        var empty = client.GetTextBudget(TextMessageKind.Channel);
        Check(empty.Scope.EffectiveScope == EffectiveFloodScope.Unscoped && !transport.LastScoped);
        var clear = client.ClearDefaultFloodScopeAsync(); await Sent(transport.Inner); transport.Inner.Emit([0]); await clear;
        Check(client.GetTextBudget(TextMessageKind.Channel).Scope.EffectiveScope == EffectiveFloodScope.Unknown && transport.LastScoped);
        await client.DisconnectAsync(); await Start(client, transport.Inner);
        var fresh = client.GetTextBudget(TextMessageKind.Channel);
        Check(fresh.Scope.EffectiveScope == EffectiveFloodScope.Unknown && fresh.Scope.SessionId != empty.Scope.SessionId);
        Check(empty.Scope.EffectiveScope == EffectiveFloodScope.Unscoped);
    }
    private static async Task Admission()
    {
        var transport = new BudgetTransport(); await using var client = new MeshCoreClient(transport,
            new MeshCoreClientOptions { AutoReceiveMessages = false }); await Start(client, transport.Inner);
        foreach (var scope in new[] { FloodScopeSelection.InheritDefault, FloodScopeSelection.ForceUnscoped,
            FloodScopeSelection.ExplicitRegion(RegionKeys.DerivePublic("A")) })
        {
            var budget = client.GetTextBudget(TextMessageKind.Channel, scope);
            var text = new string('я', budget.MaximumTextBytes / 2) + (budget.MaximumTextBytes % 2 == 0 ? "" : "x");
            var send = client.SendChannelTextAsync(0, text, 1, scope);
            await CompleteChannel(transport.Inner, scope, 0); var accepted = await send;
            Check(accepted.TextBudget!.MaximumTextBytes == budget.MaximumTextBytes && accepted.TextBudget.Scope.EffectiveScope == budget.Scope.EffectiveScope);
            Check(accepted.TextBudget.Scope.KeyFingerprint == budget.Scope.KeyFingerprint);
            await Throws<ArgumentOutOfRangeException>(() => client.SendChannelTextAsync(0, text + "x", 2, scope));
            Check(!transport.Inner.Sent.Reader.TryRead(out _));
        }
    }
    private static async Task ChangedBudget()
    {
        var transport = new BudgetTransport(); await using var client = new MeshCoreClient(transport,
            new MeshCoreClientOptions { AutoReceiveMessages = false }); await Start(client, transport.Inner);
        var first = client.SendChannelTextAsync(0, "a", 1, FloodScopeSelection.ForceUnscoped);
        await Sent(transport.Inner);
        var queued = client.SendChannelTextAsync(0, new string('x', 100), 2, FloodScopeSelection.ForceUnscoped);
        transport.Mtu = 23; transport.Inner.Emit([0]);
        Check((await Sent(transport.Inner))[0] == 3); transport.Inner.Emit([0]); await Sent(transport.Inner); transport.Inner.Emit([0]); await first;
        Check((await Sent(transport.Inner))[0] == 54); transport.Inner.Emit([0]);
        Check((await Sent(transport.Inner)).SequenceEqual(new byte[] { 54, 0 })); transport.Inner.Emit([0]);
        await Throws<ArgumentOutOfRangeException>(async () => await queued);
        Check(client.IsConnected && !transport.Inner.Sent.Reader.TryRead(out _));
    }
}
