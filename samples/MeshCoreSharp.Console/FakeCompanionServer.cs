using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MeshCoreSharp.Protocol;

internal sealed class FakeCompanionServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private Task? _serverTask;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public Task Completion => _serverTask ?? Task.CompletedTask;

    public void Start()
    {
        _listener.Start();
        _serverTask = ServeAsync();
    }

    private async Task ServeAsync()
    {
        using var client = await _listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();

        var messages = new Queue<byte[]>(
        [
            BuildMessage(PacketType.ContactMessageReceivedV3, "Hello from Alice"),
            BuildMessage(PacketType.ChannelMessageReceivedV3, "Bob: Hello channel"),
            [(byte)PacketType.ChannelDataReceived, 0xE3, 0, 0, 0, 0xFF, 0xFF, 0xFF, 3, 0, 1, 255],
        ]);
        while (true)
        {
            byte[] command;
            try { command = await ReadFrameAsync(stream, 0x3C); }
            catch (EndOfStreamException) { return; }
            if (command.Length == 0) throw new InvalidDataException("Empty command.");
            var type = (CommandType)command[0];

            if (type == CommandType.SyncNextMessage)
            {
                await WriteFrameAsync(stream, 0x3E, messages.TryDequeue(out var message)
                    ? message : [(byte)PacketType.NoMoreMessages]);
                continue;
            }

            // A tickle can arrive while a completely different command owns the dispatcher.
            await WriteFrameAsync(stream, 0x3E, [(byte)PacketType.MessagesWaiting]);
            if (type == CommandType.GetContacts)
            {
                await WriteFrameAsync(stream, 0x3E, BuildContactBoundary(PacketType.ContactStart, 2));
                await WriteFrameAsync(stream, 0x3E, BuildContact("Alice", 0xA1));
                await WriteFrameAsync(stream, 0x3E, [(byte)PacketType.MessagesWaiting]);
                await WriteFrameAsync(stream, 0x3E, BuildContact("Bob", 0xB2));
                await WriteFrameAsync(stream, 0x3E, BuildContactBoundary(PacketType.ContactEnd, 1_700_000_123));
                continue;
            }

            var response = type switch
            {
                CommandType.AppStart => BuildSelfInfo(),
                CommandType.DeviceQuery => BuildDeviceInfo(),
                CommandType.GetDeviceTime => BuildCurrentTime(),
                CommandType.GetBatteryAndStorage => BuildBattery(),
                _ => throw new InvalidDataException($"Unexpected command: {type}"),
            };
            await WriteFrameAsync(stream, 0x3E, response);
        }
    }

    private static byte[] BuildMessage(PacketType type, string text)
    {
        var contact = type == PacketType.ContactMessageReceivedV3;
        var bytes = Encoding.UTF8.GetBytes(text);
        var headerSize = contact ? 16 : 11;
        var frame = new byte[headerSize + bytes.Length];
        frame[0] = (byte)type;
        frame[1] = 0xE3; // SNR -7.25 dB
        if (contact) frame.AsSpan(4, 6).Fill(0xA1);
        var offset = contact ? 10 : 5;
        frame[offset] = 0xFF; // direct route
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(offset + 2), 1_700_000_123);
        bytes.CopyTo(frame.AsSpan(headerSize));
        return frame;
    }

    private static byte[] BuildSelfInfo()
    {
        var name = Encoding.UTF8.GetBytes("fake-companion");
        var frame = new byte[58 + name.Length];
        frame[0] = (byte)PacketType.SelfInfo;
        frame[1] = 1; // chat
        frame[2] = 20;
        frame[3] = 22;

        for (var i = 0; i < 32; i++)
            frame[4 + i] = (byte)i;

        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(36, 4), 51_500_000);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(40, 4), -100_000);
        frame[44] = 1;
        frame[45] = 1;
        frame[46] = 0b10_10_10;
        frame[47] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(48, 4), 869_525);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(52, 4), 250_000);
        frame[56] = 11;
        frame[57] = 5;
        name.CopyTo(frame.AsSpan(58));
        return frame;
    }

    private static byte[] BuildDeviceInfo()
    {
        var frame = new byte[82];
        frame[0] = (byte)PacketType.DeviceInfo;
        frame[1] = 10;
        frame[2] = 50; // 100 contacts
        frame[3] = 8;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4, 4), 123456);
        WriteFixed(frame.AsSpan(8, 12), "24 Sep 2026");
        WriteFixed(frame.AsSpan(20, 40), "MeshCore Fake Companion");
        WriteFixed(frame.AsSpan(60, 20), "1.17.1");
        frame[80] = 1;
        frame[81] = 1;
        return frame;
    }

    private static byte[] BuildCurrentTime()
    {
        var frame = new byte[5];
        frame[0] = (byte)PacketType.CurrentTime;
        BinaryPrimitives.WriteUInt32LittleEndian(
            frame.AsSpan(1, 4),
            checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        return frame;
    }

    private static byte[] BuildBattery()
    {
        var frame = new byte[11];
        frame[0] = (byte)PacketType.BatteryAndStorage;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1, 2), 4080);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(3, 4), 128);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(7, 4), 1024);
        return frame;
    }

    private static byte[] BuildContactBoundary(PacketType type, uint value)
    {
        var frame = new byte[5];
        frame[0] = (byte)type;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(1), value);
        return frame;
    }

    private static byte[] BuildContact(string name, byte key)
    {
        var frame = new byte[148];
        frame[0] = (byte)PacketType.Contact;
        frame.AsSpan(1, 32).Fill(key);
        frame[33] = 1; // chat
        frame[35] = 0xFF; // unknown path
        WriteFixed(frame.AsSpan(100, 32), name);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(132), 1_700_000_000);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(144), 1_700_000_123);
        return frame;
    }

    private static void WriteFixed(Span<byte> target, string value)
    {
        target.Clear();
        var bytes = Encoding.UTF8.GetBytes(value);
        bytes.AsSpan(0, Math.Min(bytes.Length, target.Length)).CopyTo(target);
    }

    private static async Task<byte[]> ReadFrameAsync(NetworkStream stream, byte marker)
    {
        var header = new byte[3];
        await ReadExactlyAsync(stream, header);
        if (header[0] != marker)
            throw new InvalidDataException($"Expected marker 0x{marker:X2}, got 0x{header[0]:X2}.");

        var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1, 2));
        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload);
        return payload;
    }

    private static async Task WriteFrameAsync(NetworkStream stream, byte marker, byte[] payload)
    {
        var frame = new byte[3 + payload.Length];
        frame[0] = marker;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1, 2), checked((ushort)payload.Length));
        payload.CopyTo(frame.AsSpan(3));
        await stream.WriteAsync(frame);
        await stream.FlushAsync();
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, Memory<byte> buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..]);
            if (read == 0)
                throw new EndOfStreamException();
            offset += read;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        if (_serverTask is not null)
        {
            try { await _serverTask; }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }
    }
}
