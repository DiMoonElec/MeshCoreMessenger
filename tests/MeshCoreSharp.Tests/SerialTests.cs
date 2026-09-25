using System.Buffers.Binary;
using MeshCoreSharp;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;
using MeshCoreSharp.Transport.Serial;

internal static class SerialTests
{
    public static (string Name, Func<Task> Run)[] Cases =>
    [
        ("Serial defaults and option validation", Options),
        ("Serial TX framing and payload bounds", SendFraming),
        ("Serial RX fragmented/coalesced frames and resynchronization", ReceiveFraming),
        ("Serial serializes writes and cancels queued sends", ConcurrentWrites),
        ("Serial cancellation during a write preserves a complete frame", CancelWrite),
        ("Serial disconnect completes RX and clears partial frame on reconnect", Reconnect),
        ("Serial idle read timeouts keep connection healthy", IdleTimeout),
        ("Serial read failures close the port and fault RX", ReadFailure),
        ("Serial write failure/timeout closes a damaged connection", WriteFailure),
        ("Serial failed/cancelled open releases port ownership", FailedOpen),
        ("Serial dispose waits for an active write", DisposeDuringWrite),
        ("Serial cancelled receive does not close the connection", CancelReceive),
        ("Serial native backend reports a nonexistent port", MissingPort),
        ("Serial transport works with contacts and automatic message pump", ClientIntegration),
    ];

    private static SerialMeshCoreTransportOptions Config => new()
    {
        PortName = "test-port", OpenDelay = TimeSpan.Zero, ReadTimeout = 20,
    };
    private static SerialMeshCoreTransport Transport(FakeSerialConnection port) => new(Config, _ => port);
    private static void Check(bool condition, string message = "Assertion failed")
    {
        if (!condition) throw new Exception(message);
    }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
    private static byte[] Wire(byte[] payload)
    {
        var wire = new byte[payload.Length + 3];
        wire[0] = 0x3E;
        BinaryPrimitives.WriteUInt16LittleEndian(wire.AsSpan(1), (ushort)payload.Length);
        payload.CopyTo(wire, 3);
        return wire;
    }

    private static async Task Options()
    {
        var defaults = new SerialMeshCoreTransportOptions { PortName = "COM1" };
        Check(defaults.BaudRate == 115200 && defaults.DtrEnable && defaults.RtsEnable);
        Check(defaults.ReadTimeout > 0 && defaults.WriteTimeout > 0);
        foreach (var invalid in new[]
        {
            Config with { PortName = " " }, Config with { BaudRate = 0 },
            Config with { ReadTimeout = -1 }, Config with { WriteTimeout = 0 },
            Config with { ReceiveBufferSize = 0 }, Config with { DecoderSafetyLimit = 0 },
            Config with { DecoderSafetyLimit = 65536 }, Config with { OpenDelay = TimeSpan.FromMilliseconds(-1) },
            Config with { OpenDelay = TimeSpan.MaxValue },
        })
            await Throws<ArgumentException>(() => { _ = new SerialMeshCoreTransport(invalid); return Task.CompletedTask; });
        await using var transport = Transport(new FakeSerialConnection());
        await Throws<InvalidOperationException>(() => transport.SendAsync(new byte[] { 1 }).AsTask());
        await transport.DisposeAsync();
        await transport.DisposeAsync();
        await transport.DisconnectAsync();
        await Throws<ObjectDisposedException>(() => transport.ConnectAsync());
    }

    private static async Task SendFraming()
    {
        var port = new FakeSerialConnection();
        await using var transport = Transport(port);
        await transport.ConnectAsync();
        await transport.SendAsync(new byte[] { 0x05 });
        Check((await port.Written.Reader.ReadAsync()).SequenceEqual(new byte[] { 0x3C, 1, 0, 5 }));
        var payload = Enumerable.Range(0, 257).Select(n => (byte)n).ToArray();
        await transport.SendAsync(payload);
        var wire = await port.Written.Reader.ReadAsync();
        Check(wire[0] == 0x3C && wire[1] == 1 && wire[2] == 1 && wire.AsSpan(3).SequenceEqual(payload));
        await Throws<ArgumentOutOfRangeException>(() => transport.SendAsync(Array.Empty<byte>()).AsTask());
        await Throws<ArgumentOutOfRangeException>(() => transport.SendAsync(new byte[65536]).AsTask());
    }

    private static async Task ReceiveFraming()
    {
        var port = new FakeSerialConnection();
        await using var transport = new SerialMeshCoreTransport(Config with { ReceiveBufferSize = 7 }, _ => port);
        await transport.ConnectAsync();
        await using var reader = transport.ReceiveAsync().GetAsyncEnumerator();
        var first = new byte[] { 9, 0x3E, 0xFF, 0, 0x3C };
        port.Emit([0x62, 0x6F, 0x6F, 0x74, 0x0A, 0x3E, 0, 0, 0x3E, 0xFF, 0xFF]);
        var wire = Wire(first);
        foreach (var value in wire) port.Emit([value]); // Split every header and payload byte.
        var large = Enumerable.Range(0, 257).Select(n => (byte)n).ToArray();
        port.Emit(Wire([0x83]).Concat(Wire(large)).ToArray());
        foreach (var expected in new[] { first, new byte[] { 0x83 }, large })
        {
            Check(await reader.MoveNextAsync());
            Check(reader.Current.Span.SequenceEqual(expected));
        }
    }

    private static async Task ConcurrentWrites()
    {
        var port = new FakeSerialConnection();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        port.OnWrite = bytes => { if (bytes[3] == 1) { entered.TrySetResult(); release.Wait(); } };
        await using var transport = Transport(port);
        await transport.ConnectAsync();
        var first = transport.SendAsync(new byte[] { 1 }).AsTask();
        try
        {
            await entered.Task;
            var second = transport.SendAsync(new byte[] { 2 }).AsTask();
            using var cts = new CancellationTokenSource();
            var cancelled = transport.SendAsync(new byte[] { 3 }, cts.Token).AsTask();
            cts.Cancel();
            await Throws<OperationCanceledException>(() => cancelled);
            Check(!second.IsCompleted && port.MaxConcurrentWrites == 1);
            release.Set();
            await Task.WhenAll(first, second);
            Check((await port.Written.Reader.ReadAsync())[3] == 1);
            Check((await port.Written.Reader.ReadAsync())[3] == 2);
            Check(!port.Written.Reader.TryRead(out _));
        }
        finally { release.Set(); }
    }

    private static async Task CancelWrite()
    {
        var port = new FakeSerialConnection();
        using var cts = new CancellationTokenSource();
        port.OnWrite = _ => cts.Cancel();
        await using var transport = Transport(port);
        await transport.ConnectAsync();
        await Throws<OperationCanceledException>(() => transport.SendAsync(new byte[] { 4 }, cts.Token).AsTask());
        Check((await port.Written.Reader.ReadAsync()).SequenceEqual(new byte[] { 0x3C, 1, 0, 4 }));
        Check(transport.IsConnected);
        port.OnWrite = null;
        await transport.SendAsync(new byte[] { 5 });
    }

    private static async Task Reconnect()
    {
        var first = new FakeSerialConnection();
        var second = new FakeSerialConnection();
        var opens = 0;
        await using var transport = new SerialMeshCoreTransport(Config, _ => ++opens == 1 ? first : second);
        await transport.ConnectAsync();
        await transport.ConnectAsync();
        Check(opens == 1);
        await using var reader = transport.ReceiveAsync().GetAsyncEnumerator();
        var pending = reader.MoveNextAsync().AsTask();
        first.Emit([0x3E, 5, 0, 9]);
        await transport.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Check(!await pending && !transport.IsConnected && first.DisposeCount == 1);
        await transport.ConnectAsync();
        await using var next = transport.ReceiveAsync().GetAsyncEnumerator();
        second.Emit(Wire([0x83]));
        Check(await next.MoveNextAsync() && next.Current.Span.SequenceEqual(new byte[] { 0x83 }));
        await transport.DisconnectAsync();
        Check(second.DisposeCount == 1);
    }

    private static async Task IdleTimeout()
    {
        var port = new FakeSerialConnection();
        await using var transport = Transport(port);
        await transport.ConnectAsync();
        await using var reader = transport.ReceiveAsync().GetAsyncEnumerator();
        for (var i = 0; i < 5; i++) port.FailRead(new TimeoutException());
        port.Emit(Wire([0x83]));
        Check(await reader.MoveNextAsync() && transport.IsConnected);
    }

    private static async Task ReadFailure()
    {
        foreach (var eof in new[] { false, true })
        {
            var port = new FakeSerialConnection();
            await using var transport = Transport(port);
            await transport.ConnectAsync();
            await using var reader = transport.ReceiveAsync().GetAsyncEnumerator();
            var pending = reader.MoveNextAsync().AsTask();
            if (eof) port.Emit([]); else port.FailRead(new IOException("USB unplugged"));
            var error = await Throws<MeshCoreTransportException>(() => pending);
            Check(error.InnerException is IOException && !transport.IsConnected);
            await transport.DisconnectAsync();
            Check(port.DisposeCount == 1);
        }
    }

    private static async Task WriteFailure()
    {
        foreach (var error in new Exception[] { new IOException("USB unplugged"), new TimeoutException("Partial write") })
        {
            var port = new FakeSerialConnection { OnWrite = _ => throw error };
            await using var transport = Transport(port);
            await transport.ConnectAsync();
            await using var reader = transport.ReceiveAsync().GetAsyncEnumerator();
            var pending = reader.MoveNextAsync().AsTask();
            var writeError = await Throws<MeshCoreTransportException>(() => transport.SendAsync(new byte[] { 5 }).AsTask());
            Check(ReferenceEquals(writeError.InnerException, error) && !transport.IsConnected);
            await Throws<MeshCoreTransportException>(() => pending);
            await transport.DisconnectAsync();
            Check(port.DisposeCount == 1);
        }
    }

    private static async Task FailedOpen()
    {
        var failed = new FakeSerialConnection { OnOpen = () => throw new IOException("Cannot open") };
        await using (var transport = Transport(failed))
        {
            await Throws<MeshCoreTransportException>(() => transport.ConnectAsync());
            Check(failed.DisposeCount == 1 && !transport.IsConnected);
        }
        var opened = new FakeSerialConnection();
        await using (var transport = new SerialMeshCoreTransport(Config with { OpenDelay = TimeSpan.FromMinutes(1) }, _ => opened))
        {
            using var cts = new CancellationTokenSource();
            var connecting = transport.ConnectAsync(cts.Token);
            await opened.Opened.Task;
            cts.Cancel();
            await Throws<OperationCanceledException>(() => connecting);
            Check(opened.DisposeCount == 1 && !transport.IsConnected);
        }
        await using (var transport = new SerialMeshCoreTransport(Config, _ => throw new Exception("Must not create port")))
            await Throws<OperationCanceledException>(() => transport.ConnectAsync(new CancellationToken(true)));
    }

    private static async Task DisposeDuringWrite()
    {
        var port = new FakeSerialConnection();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        port.OnWrite = _ => { entered.TrySetResult(); release.Wait(); };
        await using var transport = Transport(port);
        await transport.ConnectAsync();
        await using var reader = transport.ReceiveAsync().GetAsyncEnumerator();
        var receiving = reader.MoveNextAsync().AsTask();
        var sending = transport.SendAsync(new byte[] { 5 }).AsTask();
        try
        {
            await entered.Task;
            var disposing = transport.DisposeAsync().AsTask();
            Check(!await receiving);
            Check(port.DisposeCount == 0 && !disposing.IsCompleted);
            release.Set();
            await Throws<MeshCoreTransportException>(() => sending);
            await disposing;
            Check(port.DisposeCount == 1);
        }
        finally { release.Set(); }
    }

    private static async Task CancelReceive()
    {
        var port = new FakeSerialConnection();
        await using var transport = Transport(port);
        await transport.ConnectAsync();
        using var cts = new CancellationTokenSource();
        await using (var reader = transport.ReceiveAsync(cts.Token).GetAsyncEnumerator())
        {
            var waiting = reader.MoveNextAsync().AsTask();
            cts.Cancel();
            await Throws<OperationCanceledException>(() => waiting);
        }
        Check(transport.IsConnected);
        await using var next = transport.ReceiveAsync().GetAsyncEnumerator();
        port.Emit(Wire([0x83]));
        Check(await next.MoveNextAsync());
    }

    private static async Task MissingPort()
    {
        var name = OperatingSystem.IsWindows() ? "COM9876" : $"/dev/meshcoresharp-missing-{Guid.NewGuid():N}";
        await using var transport = new SerialMeshCoreTransport(name);
        var error = await Throws<MeshCoreTransportException>(() => transport.ConnectAsync());
        Check(error.InnerException is not null && !transport.IsConnected);
    }

    private static async Task ClientIntegration()
    {
        var port = new FakeSerialConnection();
        var messages = 0;
        port.OnWrite = wire =>
        {
            Check(wire[0] == 0x3C && BinaryPrimitives.ReadUInt16LittleEndian(wire.AsSpan(1)) == wire.Length - 3);
            switch ((CommandType)wire[3])
            {
                case CommandType.AppStart:
                    var self = new byte[58];
                    self[0] = (byte)PacketType.SelfInfo;
                    port.Emit(Wire(self));
                    break;
                case CommandType.GetContacts:
                    port.Emit(Wire(Fixtures.Start(1)).Concat(Wire(Fixtures.Contact()))
                        .Concat(Wire([(byte)PacketType.MessagesWaiting])).Concat(Wire(Fixtures.End())).ToArray());
                    break;
                case CommandType.SyncNextMessage:
                    port.Emit(Wire(messages++ == 0 ? MessageFixtures.Text(PacketType.ContactMessageReceivedV3) : [(byte)PacketType.NoMoreMessages]));
                    break;
                default: throw new Exception("Unexpected command");
            }
        };
        await using var transport = Transport(port);
        await using var client = new MeshCoreClient(transport);
        var message = new TaskCompletionSource<ReceivedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.MessageReceived += (_, e) => message.TrySetResult(e.Message);
        await client.ConnectAsync();
        await client.StartAsync();
        Check((await client.GetContactsAsync()).Single().Name == "Узел");
        Check(await message.Task is ContactMessage { Text: "  Привет!\n" });
        await client.DisconnectAsync();
        Check(port.DisposeCount == 1);
    }

    public static async Task PtySmokeAsync(string portName)
    {
        await using var transport = new SerialMeshCoreTransport(new SerialMeshCoreTransportOptions
        {
            PortName = portName, DtrEnable = false, OpenDelay = TimeSpan.Zero,
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await transport.ConnectAsync(cts.Token);
        await transport.SendAsync(new byte[] { 0x05 }, cts.Token);
        await using var reader = transport.ReceiveAsync(cts.Token).GetAsyncEnumerator();
        Check(await reader.MoveNextAsync() && reader.Current.Span.SequenceEqual(new byte[] { 9, 0x78, 0x56, 0x34, 0x12 }));
        await transport.DisconnectAsync();
        Console.WriteLine("SERIAL PTY TEST PASSED");
    }
}
