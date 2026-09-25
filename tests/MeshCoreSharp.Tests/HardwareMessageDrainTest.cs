using System.Runtime.CompilerServices;
using MeshCoreSharp;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;
using MeshCoreSharp.Protocol.Commands;
using MeshCoreSharp.Transport;
using MeshCoreSharp.Transport.Serial;

internal static class HardwareMessageDrainTest
{
    private const string ApplicationName = "MeshCoreSharp.ManualDrainTest";

    public static async Task RunAsync(string portName, bool waitForNotification = false)
    {
        using var stop = new CancellationTokenSource(waitForNotification ? TimeSpan.FromSeconds(90) : TimeSpan.FromSeconds(30));
        var transport = new DrainOnlyTransport(new SerialMeshCoreTransport(new SerialMeshCoreTransportOptions
        {
            PortName = portName,
            DtrEnable = true,
            RtsEnable = true,
            OpenDelay = TimeSpan.FromMilliseconds(200),
        }));
        await using var client = new MeshCoreClient(transport, new MeshCoreClientOptions
        {
            ApplicationName = ApplicationName,
            ApplicationProtocolVersion = 3,
            AutoReceiveMessages = false,
        });

        var messages = 0;
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.MessageReceived += (_, args) =>
        {
            Interlocked.Increment(ref messages);
            Console.WriteLine(args.Message switch
            {
                ContactMessage message => $"MESSAGE private prefix={message.ContactPublicKeyPrefixHex}, bytes={System.Text.Encoding.UTF8.GetByteCount(message.Text)}",
                ChannelMessage message => $"MESSAGE channel={message.ChannelIndex}, bytes={System.Text.Encoding.UTF8.GetByteCount(message.Text)}",
                ChannelDataMessage message => $"MESSAGE data channel={message.ChannelIndex}, type=0x{message.DataType:X4}, bytes={message.Data.Length}",
                _ => $"MESSAGE {args.Message.GetType().Name}",
            });
        };
        client.PushPacketReceived += (_, args) =>
        {
            if (args.Packet.Type == PacketType.MessagesWaiting)
                waiting.TrySetResult();
        };
        client.BackgroundError += (_, args) => Console.WriteLine($"BACKGROUND ERROR: {args.Exception.Message}");

        Console.WriteLine($"MANUAL MESSAGE DRAIN {DateTimeOffset.Now:O}: {portName}, 115200/8N1, DTR=true, RTS=true (ESP32 reset-safe pair)");
        Console.WriteLine("Allowlist: APP_START and SYNC_NEXT_MESSAGE only. No RF-send or configuration commands.");
        await client.ConnectAsync(stop.Token);
        try
        {
            var self = await client.StartAsync(stop.Token);
            Console.WriteLine($"SELF: name={self.Name}, autoReceive=false");
            await client.DrainMessagesAsync(stop.Token);
            await Task.Delay(100, stop.Token); // Let the separate event queue print already decoded messages.
            Console.WriteLine($"INITIAL DRAIN: reached NO_MORE_MESSAGES; messages observed={messages}.");
            if (waitForNotification)
            {
                Console.WriteLine("WAITING: send a test message to this node within 60 seconds.");
                await waiting.Task.WaitAsync(TimeSpan.FromSeconds(60), stop.Token);
                Console.WriteLine("MESSAGES_WAITING received; starting explicit drain.");
                await client.DrainMessagesAsync(stop.Token);
                await Task.Delay(100, stop.Token);
                if (messages == 0)
                    throw new InvalidDataException("The node announced messages, but the explicit drain delivered none.");
            }
            Console.WriteLine($"PASS: manual drain reached NO_MORE_MESSAGES; messages observed={messages}; commands={transport.CommandsSent}.");
        }
        finally
        {
            await client.DisconnectAsync();
            Console.WriteLine($"CLOSE: connected={transport.IsConnected}");
        }
    }

    private sealed class DrainOnlyTransport(IMeshCoreTransport inner) : IMeshCoreTransport
    {
        private static readonly byte[] AppStart = CompanionCommands.AppStart(ApplicationName, 3);
        private static readonly byte[] SyncNext = CompanionCommands.SyncNextMessage();

        public int CommandsSent { get; private set; }
        public bool IsConnected => inner.IsConnected;
        public Task ConnectAsync(CancellationToken cancellationToken = default) => inner.ConnectAsync(cancellationToken);
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => inner.DisconnectAsync(cancellationToken);
        public ValueTask DisposeAsync() => inner.DisposeAsync();

        public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
        {
            if (!frame.Span.SequenceEqual(AppStart) && !frame.Span.SequenceEqual(SyncNext))
                throw new InvalidOperationException("Blocked a command outside the APP_START/SYNC_NEXT_MESSAGE allowlist.");
            CommandsSent++;
            Console.WriteLine(frame.Span.SequenceEqual(AppStart) ? "USB TX APP_START" : "USB TX SYNC_NEXT_MESSAGE");
            return inner.SendAsync(frame, cancellationToken);
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var frame in inner.ReceiveAsync(cancellationToken))
                yield return frame;
        }
    }
}
