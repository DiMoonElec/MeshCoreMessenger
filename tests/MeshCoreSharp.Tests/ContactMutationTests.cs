using System.Buffers.Binary;
using MeshCoreSharp;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;
using MeshCoreSharp.Protocol.Commands;
using MeshCoreSharp.Protocol.Packets;
using MeshCoreSharp.Protocol.Parsing;

internal static class ContactMutationTests
{
    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("Contact mutation wire encoding and model conversion", Encoding),
        ("Contact mutation validation", Validation),
        ("Route reset full key, push, rejection, timeout and cancellation", ResetRouting),
        ("Single contact read captures full key, ignores pushes and wrong contacts, and recovers", ReadSingleContact),
        ("PATH_UPDATED typed parsing validates full key and tolerates extensions", ParsePathUpdated),
        ("Contact add/update handles interleaved push", AddRouting),
        ("Contact removal, errors, cancellation and recovery", RemoveAndRecovery),
        ("NEW_ADVERT can be stored without manual wire fields", AdvertisementOverload),
    ];

    private static void Check(bool condition, string message = "Assertion failed")
    {
        if (!condition) throw new Exception(message);
    }

    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T exception) { return exception; }
        throw new Exception($"Expected {typeof(T).Name}");
    }

    private static Contact Contact() =>
        ((ContactPacket)new CompanionPacketDecoder().Decode(Fixtures.Contact("Node"))).Contact;

    private static ContactConfiguration Configuration() => ContactConfiguration.FromContact(Contact());

    private static MeshCoreClient Client(TestTransport transport) => new(transport,
        new MeshCoreClientOptions { AutoReceiveMessages = false, CommandTimeout = TimeSpan.FromMilliseconds(300) });

    private static async Task Start(MeshCoreClient client)
    {
        await client.ConnectAsync();
        await client.StartAsync();
    }

    private static async Task<byte[]> Sent(TestTransport transport) =>
        await transport.Sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

    private static Task Encoding()
    {
        var source = Contact();
        var config = ContactConfiguration.FromContact(source);
        var command = CompanionCommands.AddOrUpdateContact(config);
        Check(command.Length == 144 && command[0] == 9);
        Check(command.AsSpan(1, 32).SequenceEqual(source.PublicKey.Span));
        Check(command[33] == (byte)AdvertisementType.Repeater && command[34] == 0x81 && command[35] == 0x82);
        Check(command.AsSpan(36, 64).SequenceEqual(source.OutPath.Span));
        Check(command.AsSpan(100, 4).SequenceEqual("Node"u8));
        Check(command.AsSpan(104, 28).IndexOfAnyExcept((byte)0) < 0);
        Check(BinaryPrimitives.ReadUInt32LittleEndian(command.AsSpan(132)) == 1_700_000_000);
        Check(BinaryPrimitives.ReadInt32LittleEndian(command.AsSpan(136)) == -33_865_143);
        Check(BinaryPrimitives.ReadInt32LittleEndian(command.AsSpan(140)) == 151_209_900);
        Check(CompanionCommands.RemoveContact(source.PublicKey.Span)
            .SequenceEqual(new byte[] { 15 }.Concat(source.PublicKey.ToArray())));

        var mutableKey = source.PublicKey.ToArray();
        var mutablePath = source.OutPath.ToArray();
        var mutable = source with { PublicKey = mutableKey, OutPath = mutablePath };
        var snapshot = ContactConfiguration.FromContact(mutable);
        mutableKey[0] ^= 0xFF;
        mutablePath[0] ^= 0xFF;
        Check(snapshot.PublicKey.Span[0] == source.PublicKey.Span[0] &&
            snapshot.OutPath.Span[0] == source.OutPath.Span[0]);
        return Task.CompletedTask;
    }

    private static async Task Validation()
    {
        var valid = Configuration();
        await Throws<ArgumentException>(() => Task.FromResult(CompanionCommands.AddOrUpdateContact(valid with { PublicKey = new byte[31] })));
        await Throws<ArgumentOutOfRangeException>(() => Task.FromResult(CompanionCommands.AddOrUpdateContact(valid with { AdvertisementType = AdvertisementType.None })));
        await Throws<ArgumentException>(() => Task.FromResult(CompanionCommands.AddOrUpdateContact(valid with { OutPath = new byte[63] })));
        await Throws<ArgumentOutOfRangeException>(() => Task.FromResult(CompanionCommands.AddOrUpdateContact(valid with { OutPathLength = 0xC0 })));
        await Throws<ArgumentException>(() => Task.FromResult(CompanionCommands.AddOrUpdateContact(valid with { Name = "a\0b" })));
        await Throws<ArgumentOutOfRangeException>(() => Task.FromResult(CompanionCommands.AddOrUpdateContact(valid with { Name = new string('x', 32) })));
        await Throws<System.Text.EncoderFallbackException>(() => Task.FromResult(CompanionCommands.AddOrUpdateContact(valid with { Name = "\uD800" })));
        await Throws<ArgumentOutOfRangeException>(() => Task.FromResult(CompanionCommands.AddOrUpdateContact(valid with { AdvertisementLatitude = 91 })));
        await Throws<ArgumentOutOfRangeException>(() => Task.FromResult(CompanionCommands.AddOrUpdateContact(valid with { AdvertisementLongitude = double.NaN })));
        await Throws<ArgumentException>(() => Task.FromResult(CompanionCommands.RemoveContact(new byte[31])));
        Check(CompanionCommands.AddOrUpdateContact(valid with { OutPathLength = 0xFF }).Length == 144);
    }

    private static async Task ResetRouting()
    {
        var transport = new TestTransport();
        await using var client = Client(transport);
        await Start(client);
        await Throws<ArgumentException>(() => client.ResetPathAsync(new byte[6]));
        Check(!transport.Sent.Reader.TryRead(out _));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Throws<OperationCanceledException>(() => client.ResetPathAsync(new byte[32], cancelled.Token));
        Check(!transport.Sent.Reader.TryRead(out _));
        var key = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var captured = key.ToArray();
        var reset = client.ResetPathAsync(key);
        Array.Fill(key, (byte)0xAA);
        var frame = await Sent(transport);
        Check(frame.Length == 33 && frame[0] == (byte)CommandType.ResetPath && frame.AsSpan(1).SequenceEqual(captured));
        var push = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.AdvertisementReceived += (_, _) => push.TrySetResult();
        transport.Emit(Fixtures.Advertisement(0x42));
        await push.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(!reset.IsCompleted);
        transport.Emit([0]); await reset;
        var rejected = client.ResetPathAsync(captured); await Sent(transport); transport.Emit([1, 2]);
        var error = await Throws<MeshCoreCommandException>(() => rejected);
        Check(error.Command == CommandType.ResetPath && error.ErrorCode == MeshCoreErrorCode.NotFound);
        var timeout = client.ResetPathAsync(captured); await Sent(transport);
        await Throws<MeshCoreTimeoutException>(() => timeout);
        Check(!transport.Sent.Reader.TryRead(out _)); // No retry/replay.
        var recovered = client.ResetPathAsync(captured); await Sent(transport); transport.Emit([0]); await recovered;
    }

    private static async Task AddRouting()
    {
        var transport = new TestTransport();
        await using var client = Client(transport);
        await Start(client);
        var push = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.AdvertisementReceived += (_, _) => push.TrySetResult();
        var contact = Contact();
        var add = client.AddOrUpdateContactAsync(contact);
        Check((await Sent(transport)).SequenceEqual(CompanionCommands.AddOrUpdateContact(ContactConfiguration.FromContact(contact))));
        transport.Emit(Fixtures.Advertisement(0x42));
        await push.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(!add.IsCompleted);
        transport.Emit([0]);
        await add;
    }

    private static Task ParsePathUpdated()
    {
        var decoder = new CompanionPacketDecoder();
        var key = Enumerable.Repeat((byte)0xA1, 32).ToArray();
        var packet = (PathUpdatedPacket)decoder.Decode(new byte[] { 0x81 }.Concat(key).Concat(new byte[] { 99 }).ToArray());
        Check(packet.PublicKey.Span.SequenceEqual(key) && packet.IsPush);
        try { decoder.Decode(new byte[] { 0x81 }.Concat(new byte[31]).ToArray()); }
        catch (MeshCoreProtocolException) { return Task.CompletedTask; }
        throw new Exception("Short path update was accepted.");
    }

    private static async Task ReadSingleContact()
    {
        var transport = new TestTransport();
        await using var client = Client(transport);
        await Start(client);
        await Throws<ArgumentException>(() => client.GetContactAsync(new byte[6]));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Throws<OperationCanceledException>(() => client.GetContactAsync(new byte[32], cancelled.Token));
        Check(!transport.Sent.Reader.TryRead(out _));
        var key = Enumerable.Repeat((byte)0xA5, 32).ToArray();
        var read = client.GetContactAsync(key);
        Array.Fill(key, (byte)0xCC);
        var command = await Sent(transport);
        Check(command[0] == 30 && command.Length == 33 && command.AsSpan(1).IndexOfAnyExcept((byte)0xA5) < 0);
        var push = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PushPacketReceived += (_, args) => { if (args.Packet is PathUpdatedPacket) push.TrySetResult(); };
        transport.Emit(new byte[] { 0x81 }.Concat(new byte[32]).ToArray());
        transport.Emit(Fixtures.Contact("Other", 0xB2));
        await push.Task.WaitAsync(TimeSpan.FromSeconds(2));
        transport.Emit(Fixtures.Contact("Wanted", 0xA5));
        Check((await read).Name == "Wanted");
        var rejected = client.GetContactAsync(new byte[32]); await Sent(transport); transport.Emit([1, 2]);
        Check((await Throws<MeshCoreCommandException>(() => rejected)).Command == CommandType.GetContactByKey);
        var timeout = client.GetContactAsync(new byte[32]); await Sent(transport);
        await Throws<MeshCoreTimeoutException>(() => timeout);
        var recovered = client.GetContactAsync(Enumerable.Repeat((byte)0xA5, 32).ToArray()); await Sent(transport);
        transport.Emit(Fixtures.Contact()); await recovered;
    }

    private static async Task RemoveAndRecovery()
    {
        var transport = new TestTransport();
        await using var client = Client(transport);
        await Start(client);
        var contact = Contact();
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Throws<OperationCanceledException>(() => client.RemoveContactAsync(contact, cancelled.Token));
            Check(!transport.Sent.Reader.TryRead(out _));
        }

        var remove = client.RemoveContactAsync(contact.PublicKey);
        Check((await Sent(transport)).SequenceEqual(CompanionCommands.RemoveContact(contact.PublicKey.Span)));
        transport.Emit([1, 2]);
        var error = await Throws<MeshCoreCommandException>(() => remove);
        Check(error.Command == CommandType.RemoveContact && error.ErrorCode == MeshCoreErrorCode.NotFound);

        var add = client.AddOrUpdateContactAsync(ContactConfiguration.FromContact(contact));
        await Sent(transport);
        transport.Emit([1, 3]);
        error = await Throws<MeshCoreCommandException>(() => add);
        Check(error.Command == CommandType.AddOrUpdateContact && error.ErrorCode == MeshCoreErrorCode.TableFull);

        var recovered = client.RemoveContactAsync(contact);
        await Sent(transport);
        transport.Emit([0]);
        await recovered;
    }

    private static async Task AdvertisementOverload()
    {
        var transport = new TestTransport();
        await using var client = Client(transport);
        await Start(client);
        var decoder = new CompanionPacketDecoder();
        var full = ((AdvertisementPacket)decoder.Decode(Fixtures.NewAdvertisement())).Info;
        var add = client.AddOrUpdateContactAsync(full);
        Check((await Sent(transport)).SequenceEqual(
            CompanionCommands.AddOrUpdateContact(ContactConfiguration.FromContact(full.DiscoveredContact!))));
        transport.Emit([0]);
        await add;
        var keyOnly = ((AdvertisementPacket)decoder.Decode(Fixtures.Advertisement())).Info;
        await Throws<ArgumentException>(() => client.AddOrUpdateContactAsync(keyOnly));
        Check(!transport.Sent.Reader.TryRead(out _));
    }
}
