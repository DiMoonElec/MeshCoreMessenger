using MeshCoreSharp;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;
using MeshCoreSharp.Protocol.Commands;
using MeshCoreSharp.Protocol.Packets;
using MeshCoreSharp.Protocol.Parsing;

internal static class AdvertisementTests
{
    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("Advertisement modes, wire fields and truncation", Parsing),
        ("Advertisement events remain separate from contacts stream", DuringContacts),
        ("Send advertisement waits for OK and propagates error/timeout", Send),
        ("Advertisement cancellation and disconnect cancel queued sends", Cancellation),
        ("Malformed advertisement is diagnostic and RX remains usable", Malformed),
        ("Advertisement handler can issue a command without blocking RX", Callback),
    ];
    private static void Check(bool condition)
    {
        if (!condition) throw new Exception("Assertion failed");
    }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T exception) { return exception; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
    private static AdvertisementPacket Decode(byte[] frame) => (AdvertisementPacket)new CompanionPacketDecoder().Decode(frame);
    private static MeshCoreClient Client(TestTransport transport) => new(transport,
        new MeshCoreClientOptions { AutoReceiveMessages = false, CommandTimeout = TimeSpan.FromMilliseconds(300) });
    private static async Task Start(MeshCoreClient client)
    {
        await client.ConnectAsync();
        await client.StartAsync();
    }
    private static async Task<byte[]> Sent(TestTransport transport) =>
        await transport.Sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task Parsing()
    {
        Check(CompanionCommands.SendAdvertisement(AdvertisementMode.ZeroHop).SequenceEqual(new byte[] { 7, 0 }));
        Check(CompanionCommands.SendAdvertisement(AdvertisementMode.Flood).SequenceEqual(new byte[] { 7, 1 }));
        await Throws<ArgumentOutOfRangeException>(() => Task.FromResult(CompanionCommands.SendAdvertisement((AdvertisementMode)255)));
        var frame = Fixtures.Advertisement();
        var known = Decode(frame);
        Check(known.IsPush && known.Type == PacketType.Advertisement && !known.Info.IsNew &&
            known.Info.DiscoveredContact is null && known.Info.PublicKey.Length == 32);
        frame[1] = 0;
        Check(known.Info.PublicKey.Span[0] == 0xA5);
        var discovered = Decode(Fixtures.NewAdvertisement());
        Check(discovered.IsPush && discovered.Type == PacketType.NewAdvertisement && discovered.Info.IsNew);
        var contact = discovered.Info.DiscoveredContact!;
        Check(contact.Name == "Discovered node" && contact.Flags == 0x81 && contact.OutPathLength == 0x82 &&
            contact.OutPath.Length == 64 && contact.AdvertisementLatitude == -33.865143 &&
            contact.LastModified == 1_700_000_123 && contact.PublicKeyHex == discovered.Info.PublicKeyHex);
        foreach (var sample in new[] { Fixtures.Advertisement(), Fixtures.NewAdvertisement() })
        {
            for (var size = 1; size < sample.Length; size++)
                await Throws<MeshCoreProtocolException>(() => Task.FromResult(Decode(sample[..size])));
            byte[] extended = [.. sample, 0xAA];
            Check(Decode(extended).RawFrame.Length == sample.Length + 1);
        }
    }

    private static async Task DuringContacts()
    {
        var transport = new TestTransport();
        await using var client = Client(transport);
        await Start(client);
        var seen = new List<AdvertisementInfo>();
        var events = Signal();
        client.AdvertisementReceived += (_, args) =>
        {
            seen.Add(args.Advertisement);
            if (seen.Count == 2) events.TrySetResult();
        };
        var contacts = client.GetContactsAsync();
        await Sent(transport);
        transport.Emit(Fixtures.Start(1));
        transport.Emit(Fixtures.NewAdvertisement());
        transport.Emit(Fixtures.Contact("Stored node"));
        transport.Emit(Fixtures.Advertisement());
        transport.Emit(Fixtures.End());
        var result = await contacts;
        await events.Task;
        Check(result.Count == 1 && result[0].Name == "Stored node");
        Check(seen[0].IsNew && !seen[1].IsNew && !transport.Sent.Reader.TryRead(out _));
    }

    private static async Task Send()
    {
        var transport = new TestTransport();
        await using var client = Client(transport);
        await Start(client);
        var push = Signal();
        client.AdvertisementReceived += (_, _) => push.TrySetResult();
        var send = client.SendAdvertisementAsync(AdvertisementMode.Flood);
        Check((await Sent(transport)).SequenceEqual(new byte[] { 7, 1 }));
        transport.Emit(Fixtures.Advertisement());
        await push.Task;
        Check(!send.IsCompleted);
        transport.Emit([0]);
        await send;
        var failed = client.SendAdvertisementAsync();
        Check((await Sent(transport)).SequenceEqual(new byte[] { 7, 0 }));
        transport.Emit([1, 3]);
        var error = await Throws<MeshCoreCommandException>(() => failed);
        Check(error.Command == CommandType.SendSelfAdvertisement);
        var timeout = client.SendAdvertisementAsync();
        await Sent(transport);
        await Throws<MeshCoreTimeoutException>(() => timeout);
        Check(!transport.Sent.Reader.TryRead(out _));
    }

    private static async Task Cancellation()
    {
        var transport = new TestTransport();
        await using var client = Client(transport);
        await Throws<InvalidOperationException>(() => client.SendAdvertisementAsync());
        await Start(client);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Throws<OperationCanceledException>(() => client.SendAdvertisementAsync(cancellationToken: cancelled.Token));
        Check(!transport.Sent.Reader.TryRead(out _));
        var blocking = client.GetDeviceTimeAsync();
        await Sent(transport);
        var queued = client.SendAdvertisementAsync(AdvertisementMode.Flood);
        await client.DisconnectAsync();
        await Throws<MeshCoreTransportException>(() => blocking);
        await Throws<OperationCanceledException>(() => queued);
        await Start(client);
        Check(!transport.Sent.Reader.TryRead(out _));
    }

    private static async Task Malformed()
    {
        var transport = new TestTransport();
        await using var client = Client(transport);
        await Start(client);
        var error = Signal();
        client.BackgroundError += (_, args) => { if (args.Exception is MeshCoreProtocolException) error.TrySetResult(); };
        var request = client.GetContactsAsync();
        await Sent(transport);
        transport.Emit(Fixtures.Start(0));
        transport.Emit([0x8A, 0xAA]);
        await error.Task;
        Check(!request.IsCompleted && client.IsConnected);
        transport.Emit(Fixtures.End());
        Check((await request).Count == 0);
    }

    private static async Task Callback()
    {
        var transport = new TestTransport();
        await using var client = Client(transport);
        await Start(client);
        var handled = new TaskCompletionSource<DateTimeOffset>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.AdvertisementReceived += (_, _) => throw new Exception("User callback error");
        client.AdvertisementReceived += (_, _) =>
        {
            try { handled.TrySetResult(client.GetDeviceTimeAsync().GetAwaiter().GetResult()); }
            catch (Exception ex) { handled.TrySetException(ex); }
        };
        transport.Emit(Fixtures.Advertisement());
        Check((await Sent(transport))[0] == 5);
        transport.Emit(Fixtures.Number(PacketType.CurrentTime, 123));
        Check((await handled.Task).ToUnixTimeSeconds() == 123);
    }
}
