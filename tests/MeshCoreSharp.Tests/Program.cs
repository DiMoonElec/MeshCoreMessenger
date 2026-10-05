using MeshCoreSharp;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;
using MeshCoreSharp.Protocol.Commands;
using MeshCoreSharp.Protocol.Packets;
using MeshCoreSharp.Protocol.Parsing;
using MeshCoreSharp.Runtime;
using MeshCoreSharp.Runtime.Transactions;

if (args.Length == 2 && args[0] == "--serial-contact-restore-send-test")
{
    return await RunHardwareAsync(() => HardwareContactRestoreSendTest.RunAsync(args[1]));
}

if (args.Length == 2 && args[0] == "--serial-contact-mutation-test")
{
    return await RunHardwareAsync(() => HardwareContactMutationTest.RunAsync(args[1]));
}

if (args.Length == 2 && args[0] == "--serial-channel-config-test")
{
    return await RunHardwareAsync(() => HardwareChannelConfigurationTest.RunAsync(args[1]));
}

if (args.Length is 2 or 3 && args[0] == "--serial-private-send-test")
{
    return await RunHardwareAsync(() => HardwarePrivateMessageTest.RunAsync(args[1], args.Length == 3 ? args[2] : null));
}

if (args.Length == 2 && args[0] == "--serial-message-drain-test")
{
    return await RunHardwareAsync(() => HardwareMessageDrainTest.RunAsync(args[1]));
}

if (args.Length == 2 && args[0] == "--serial-message-drain-live-test")
{
    return await RunHardwareAsync(() => HardwareMessageDrainTest.RunAsync(args[1], waitForNotification: true));
}

if (args.Length == 2 && args[0] == "--serial-no-reset-test")
{
    return await RunHardwareAsync(() => HardwareSerialNoResetTest.RunAsync(args[1]));
}

if (args.Length == 2 && args[0] == "--serial-advert-test")
{
    return await RunHardwareAsync(() => HardwareAdvertisementTest.RunAsync(args[1]));
}

if (args.Length == 2 && args[0] == "--serial-send-test")
{
    return await RunHardwareAsync(() => HardwareSendTest.RunAsync(args[1]));
}

if (args.Length == 2 && args[0] == "--serial-read-only")
{
    return await RunHardwareAsync(() => HardwareReadOnlyTest.RunAsync(args[1]));
}

if (args.Length == 2 && args[0] == "--serial-pty")
{
    return await RunHardwareAsync(() => SerialTests.PtySmokeAsync(args[1]));
}

static async Task<int> RunHardwareAsync(Func<Task> action)
{
    try { await action(); return 0; }
    catch (Exception error)
    {
        Console.Error.WriteLine($"HARDWARE TEST FAILED: {error.Message}");
        return 1;
    }
}

var tests = new (string Name, Func<Task> Run)[]
{
    ("Contact wire fields and raw fallback", DecodeFields),
    ("Truncated contact frames", TruncatedFrames),
    ("Empty list waits for END", EmptyList),
    ("Interleaved packets and advertised count", InterleavedPackets),
    ("Invalid stream ordering", InvalidOrdering),
    ("Inactivity resets only on accepted frames", Inactivity),
    ("Absolute timeout bounds an active stream", AbsoluteTimeout),
    ("Subscribe before send", SubscribeBeforeSend),
    ("Public API and command serialization", PublicApi),
    ("Blocked push callback does not block RX", BlockedCallback),
    ("Cancellation releases the command gate", Cancellation),
    ("Cancellation while queued sends no command", QueuedCancellation),
    ("Timeout releases the command gate", TimeoutRecovery),
    ("ERROR before and after START", CommandErrors),
    ("Malformed contacts fail locally", MalformedRecovery),
    ("Connection loss and explicit disconnect", Disconnection),
    ("Send failure releases the command gate", SendFailure),
    ("Timeout option validation", OptionValidation),
};
var failed = 0;
var allTests = tests.Concat(MessageTests.Cases).Concat(SerialTests.Cases).Concat(ChannelStatsTests.Cases)
    .Concat(SendTests.Cases).Concat(AdvertisementTests.Cases).Concat(ContactMutationTests.Cases)
    .Concat(EventBarrierTests.Cases).Concat(TextValidationTests.Cases).ToArray();
foreach (var test in allTests)
{
    try
    {
        await test.Run().WaitAsync(TimeSpan.FromSeconds(10));
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}: {ex}");
    }
}
Console.WriteLine($"{allTests.Length - failed}/{allTests.Length} passed");
return failed == 0 ? 0 : 1;

static CompanionPacket Decode(byte[] frame) => new CompanionPacketDecoder().Decode(frame);
static void Check(bool condition, string message = "Assertion failed")
{
    if (!condition) throw new Exception(message);
}
static async Task<T> Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T ex) { return ex; }
    throw new Exception($"Expected {typeof(T).Name}");
}
static Task DecodeFields()
{
    var frame = Fixtures.Contact();
    var packet = (ContactPacket)Decode(frame);
    var c = packet.Contact;
    Check(c.Name == "Узел" && c.PublicKeyHex == string.Concat(Enumerable.Repeat("A5", 32)));
    Check(c.AdvertisementType == AdvertisementType.Repeater && c.Flags == 0x81);
    Check(c.OutPathLength == 0x82 && c.OutPath.Length == 64 && c.OutPath.Span[63] == 0x5A);
    Check(c.AdvertisementLatitude == -33.865143 && c.AdvertisementLongitude == 151.2099);
    Check(c.LastAdvertTimestamp == 1_700_000_000 && c.LastModified == 1_700_000_123);
    Check(packet.RawFrame.Span.SequenceEqual(frame));
    frame[35] = 0xFF;
    Check(((ContactPacket)Decode(frame)).Contact.OutPathLength == 0xFF);
    Check(((ContactStartPacket)Decode(Fixtures.Start(70_000))).TotalCount == 70_000);
    Check(((ContactEndPacket)Decode(Fixtures.End())).MostRecentLastModified == 1_700_000_123);
    Check(Decode([0xFE, 0xAA]) is RawCompanionPacket raw && raw.IsPush && raw.Payload.Span[0] == 0xAA);
    Check(CompanionCommands.GetContacts().SequenceEqual(new byte[] { 0x04 }));
    return Task.CompletedTask;
}
static async Task TruncatedFrames()
{
    foreach (var frame in new[] { Fixtures.Start(1), Fixtures.Contact(), Fixtures.End() })
        for (var length = 1; length < frame.Length; length++)
            await Throws<MeshCoreProtocolException>(() => { Decode(frame[..length]); return Task.CompletedTask; });
}
static ContactsTransaction NewTransaction(ManualTimeProvider clock) =>
    new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20), clock);
static async Task EmptyList()
{
    using var t = NewTransaction(new ManualTimeProvider());
    Check(t.TryAccept(Decode(Fixtures.Start(0))) && !t.Completion.IsCompleted);
    Check(t.TryAccept(Decode(Fixtures.End(0))));
    Check(await t.Completion is ContactEndPacket && t.GetContacts().Count == 0);
}
static async Task InterleavedPackets()
{
    using var t = NewTransaction(new ManualTimeProvider());
    t.TryAccept(Decode(Fixtures.Start(10))); // Firmware count is advisory, not a completion condition.
    t.TryAccept(Decode(Fixtures.Contact("First")));
    Check(!t.TryAccept(Decode([(byte)PacketType.MessagesWaiting])));
    Check(!t.TryAccept(Decode(Fixtures.NewAdvertisement())));
    Check(!t.TryAccept(Decode(Fixtures.Number(PacketType.CurrentTime, 42))));
    t.TryAccept(Decode(Fixtures.Contact("Second", 0xBB)));
    Check(!t.Completion.IsCompleted);
    t.TryAccept(Decode(Fixtures.End()));
    await t.Completion;
    Check(t.GetContacts().Select(c => c.Name).SequenceEqual(new[] { "First", "Second" }));
    Check(!t.TryAccept(Decode(Fixtures.Contact("Late"))) && t.GetContacts().Count == 2);
}
static async Task InvalidOrdering()
{
    foreach (var invalid in new[] { Fixtures.Contact(), Fixtures.End(), Fixtures.Start(1) })
    {
        using var t = NewTransaction(new ManualTimeProvider());
        if (invalid[0] == (byte)PacketType.ContactStart) t.TryAccept(Decode(Fixtures.Start(1)));
        t.TryAccept(Decode(invalid));
        await Throws<MeshCoreProtocolException>(() => t.Completion);
    }
}
static async Task Inactivity()
{
    var clock = new ManualTimeProvider();
    using var t = NewTransaction(clock);
    clock.Advance(TimeSpan.FromSeconds(4));
    t.TryAccept(Decode(Fixtures.Start(2)));
    clock.Advance(TimeSpan.FromSeconds(4));
    t.TryAccept(Decode(Fixtures.Contact()));
    clock.Advance(TimeSpan.FromSeconds(4));
    Check(!t.Completion.IsCompleted, "Active stream must outlive its inactivity interval");
    t.TryAccept(Decode([(byte)PacketType.MessagesWaiting]));
    clock.Advance(TimeSpan.FromSeconds(1));
    var error = await Throws<MeshCoreTimeoutException>(() => t.Completion);
    Check(error.Timeout == TimeSpan.FromSeconds(5));

    using var noStart = NewTransaction(clock);
    clock.Advance(TimeSpan.FromSeconds(5));
    await Throws<MeshCoreTimeoutException>(() => noStart.Completion);
}
static async Task AbsoluteTimeout()
{
    var clock = new ManualTimeProvider();
    using var t = NewTransaction(clock);
    t.TryAccept(Decode(Fixtures.Start(100)));
    for (var i = 0; i < 4; i++)
    {
        clock.Advance(TimeSpan.FromSeconds(4));
        t.TryAccept(Decode(Fixtures.Contact()));
    }
    clock.Advance(TimeSpan.FromSeconds(4));
    var error = await Throws<MeshCoreTimeoutException>(() => t.Completion);
    Check(error.Timeout == TimeSpan.FromSeconds(20));
}
static async Task SubscribeBeforeSend()
{
    await using var transport = new TestTransport();
    var router = new PacketRouter();
    using var dispatcher = new CommandDispatcher(transport, router);
    transport.OnSend = (_, _) =>
    {
        Check(router.Route(Decode(Fixtures.Start(1))));
        Check(router.Route(Decode(Fixtures.Contact())));
        Check(router.Route(Decode(Fixtures.End())));
        return ValueTask.CompletedTask;
    };
    var contacts = await dispatcher.SendContactsAsync(CompanionCommands.GetContacts(), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
    Check(contacts.Count == 1);
}
static async Task<MeshCoreClient> Ready(TestTransport transport, MeshCoreClientOptions? options = null)
{
    var client = new MeshCoreClient(transport, options);
    await client.ConnectAsync();
    await client.StartAsync();
    return client;
}
static async Task ExpectCommand(TestTransport transport, CommandType type)
{
    var frame = await transport.Sent.Reader.ReadAsync();
    Check(frame.SequenceEqual(new[] { (byte)type }), $"Expected {type}");
}
static async Task TimeCommand(TestTransport transport, MeshCoreClient client)
{
    var time = client.GetDeviceTimeAsync();
    await ExpectCommand(transport, CommandType.GetDeviceTime);
    transport.Emit(Fixtures.Number(PacketType.CurrentTime, 42));
    Check((await time).ToUnixTimeSeconds() == 42);
}
static async Task PublicApi()
{
    await using var transport = new TestTransport();
    await using var client = await Ready(transport);
    var push = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    client.PushPacketReceived += (_, e) => { if (e.Packet.Type == PacketType.MessagesWaiting) push.TrySetResult(); };
    var contacts = client.GetContactsAsync();
    await ExpectCommand(transport, CommandType.GetContacts);
    transport.Emit(Fixtures.Start(1));
    transport.Emit(Fixtures.Contact());
    transport.Emit([(byte)PacketType.MessagesWaiting]);
    await push.Task;
    var time = client.GetDeviceTimeAsync();
    Check(!contacts.IsCompleted && !transport.Sent.Reader.TryRead(out _), "Slot must remain held until END");
    transport.Emit(Fixtures.End());
    Check((await contacts).Single().Name == "Узел");
    await ExpectCommand(transport, CommandType.GetDeviceTime);
    transport.Emit(Fixtures.Number(PacketType.CurrentTime, 42));
    await time;
}
static async Task BlockedCallback()
{
    await using var transport = new TestTransport();
    await using var client = await Ready(transport);
    using var release = new ManualResetEventSlim();
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var left = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    client.PushPacketReceived += (_, _) => throw new InvalidOperationException("Consumer failure");
    client.PushPacketReceived += (_, _) =>
    {
        entered.TrySetResult();
        release.Wait();
        left.TrySetResult();
    };
    try
    {
        var contacts = client.GetContactsAsync();
        await ExpectCommand(transport, CommandType.GetContacts);
        transport.Emit(Fixtures.Start(1));
        transport.Emit([(byte)PacketType.MessagesWaiting]);
        await entered.Task;
        transport.Emit(Fixtures.Contact());
        transport.Emit(Fixtures.End());
        Check((await contacts.WaitAsync(TimeSpan.FromSeconds(2))).Count == 1);
        await TimeCommand(transport, client);
    }
    finally
    {
        release.Set();
        if (entered.Task.IsCompleted) await left.Task;
    }
}
static async Task Cancellation()
{
    await using var transport = new TestTransport();
    await using var client = await Ready(transport);
    using var cts = new CancellationTokenSource();
    var contacts = client.GetContactsAsync(cts.Token);
    await ExpectCommand(transport, CommandType.GetContacts);
    transport.Emit(Fixtures.Start(1));
    cts.Cancel();
    await Throws<OperationCanceledException>(() => contacts);
    transport.Emit(Fixtures.Contact()); // Late frames cannot complete a different command.
    transport.Emit(Fixtures.End());
    await TimeCommand(transport, client);
}
static async Task QueuedCancellation()
{
    await using var transport = new TestTransport();
    await using var client = await Ready(transport);
    var first = client.GetContactsAsync();
    await ExpectCommand(transport, CommandType.GetContacts);
    using var cts = new CancellationTokenSource();
    var queued = client.GetContactsAsync(cts.Token);
    cts.Cancel();
    await Throws<OperationCanceledException>(() => queued);
    transport.Emit(Fixtures.Start(0));
    transport.Emit(Fixtures.End(0));
    await first;
    Check(!transport.Sent.Reader.TryRead(out _));
    await TimeCommand(transport, client);
}
static async Task TimeoutRecovery()
{
    await using var transport = new TestTransport();
    await using var client = await Ready(transport, new MeshCoreClientOptions { ContactsInactivityTimeout = TimeSpan.FromMilliseconds(100) });
    var contacts = client.GetContactsAsync();
    await ExpectCommand(transport, CommandType.GetContacts);
    transport.Emit(Fixtures.Start(1));
    transport.Emit(Fixtures.Contact()); // No END.
    await Throws<MeshCoreTimeoutException>(() => contacts);
    await TimeCommand(transport, client);
}
static async Task CommandErrors()
{
    await using var transport = new TestTransport();
    await using var client = await Ready(transport);
    foreach (var started in new[] { false, true })
    {
        var contacts = client.GetContactsAsync();
        await ExpectCommand(transport, CommandType.GetContacts);
        if (started) transport.Emit(Fixtures.Start(1));
        transport.Emit([(byte)PacketType.Error, (byte)MeshCoreErrorCode.BadState]);
        var error = await Throws<MeshCoreCommandException>(() => contacts);
        Check(error.Command == CommandType.GetContacts && error.ErrorCode == MeshCoreErrorCode.BadState);
        await TimeCommand(transport, client);
    }
}
static async Task MalformedRecovery()
{
    await using var transport = new TestTransport();
    await using var client = await Ready(transport);
    var diagnostic = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    client.BackgroundError += (_, _) => diagnostic.TrySetResult();
    var contacts = client.GetContactsAsync();
    await ExpectCommand(transport, CommandType.GetContacts);
    transport.Emit(Fixtures.Start(1));
    transport.Emit(Fixtures.Contact()[..100]);
    await Throws<MeshCoreProtocolException>(() => contacts);
    await diagnostic.Task;
    Check(client.IsConnected);
    transport.Emit(Fixtures.End());
    await TimeCommand(transport, client);
}
static async Task Disconnection()
{
    foreach (var explicitDisconnect in new[] { false, true })
    {
        await using var transport = new TestTransport();
        await using var client = await Ready(transport);
        var contacts = client.GetContactsAsync();
        await ExpectCommand(transport, CommandType.GetContacts);
        if (explicitDisconnect) await client.DisconnectAsync(); else transport.Close();
        await Throws<MeshCoreTransportException>(() => contacts);
    }
}
static async Task SendFailure()
{
    await using var transport = new TestTransport();
    await using var client = await Ready(transport);
    transport.OnSend = (_, _) => throw new IOException("Write failed");
    await Throws<IOException>(() => client.GetContactsAsync());
    transport.OnSend = null;
    await TimeCommand(transport, client);
}
static async Task OptionValidation()
{
    await using var transport = new TestTransport();
    foreach (var value in new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(-1), TimeSpan.MaxValue })
    {
        await Throws<ArgumentOutOfRangeException>(() => { _ = new MeshCoreClient(transport, new() { ContactsInactivityTimeout = value }); return Task.CompletedTask; });
        await Throws<ArgumentOutOfRangeException>(() => { _ = new MeshCoreClient(transport, new() { ContactsAbsoluteTimeout = value }); return Task.CompletedTask; });
    }
}
