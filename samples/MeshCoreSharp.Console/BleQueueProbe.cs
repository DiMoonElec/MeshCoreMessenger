using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using MeshCoreSharp;
using MeshCoreSharp.Models;
using MeshCoreSharp.Transport;
using MeshCoreSharp.Transport.Tcp;

// Opt-in queue consumption. Capture is local and contains private raw frames/text;
// console output contains lengths only. No file I/O runs in the RX/event callbacks.
internal static class BleQueueProbe
{
    internal static async Task RunAsync(IMeshCoreTransport inner, string capturePath,
        Func<ushort?> getMtu, Func<Task> cleanup, CancellationToken token)
    {
        await using var output = new FileStream(capturePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(output, new UTF8Encoding(false));
        var capture = new ConcurrentQueue<string>();
        void Record(object value) => capture.Enqueue(JsonSerializer.Serialize(value));
        var transport = new ReadOnlyProbeTransport(inner, true, frame => Record(new
        {
            Kind = "frame", Utc = DateTimeOffset.UtcNow, Length = frame.Length,
            Type = frame.IsEmpty ? 0 : frame.Span[0], Hex = Convert.ToHexString(frame.Span),
        }));
        await using var client = new MeshCoreClient(transport, new()
        {
            ApplicationName = "MeshCoreSharp.BLE8", AutoReceiveMessages = false,
            CommandTimeout = TimeSpan.FromSeconds(15),
        });
        var messages = new ConcurrentQueue<(string Kind, int? Slot, int Bytes)>();
        client.MessageReceived += (_, e) =>
        {
            switch (e.Message)
            {
                case ContactMessage contact:
                    messages.Enqueue(("private", null, Encoding.UTF8.GetByteCount(contact.Text)));
                    Record(new { Kind = "private", contact.Text, contact.Timestamp, contact.TextType });
                    break;
                case ChannelMessage channel:
                    messages.Enqueue(("channel", channel.ChannelIndex, Encoding.UTF8.GetByteCount(channel.Text)));
                    Record(new { Kind = "channel", channel.ChannelIndex, channel.Text, channel.Timestamp, channel.TextType });
                    break;
                default:
                    messages.Enqueue(("other", null, 0));
                    Record(new { Kind = "other-message", Type = e.Message.GetType().Name });
                    break;
            }
        };
        client.BackgroundError += (_, e) => Record(new { Kind = "error", Error = e.Exception.ToString() });
        try
        {
            await client.ConnectAsync(token);
            var self = await client.StartAsync(token);
            var device = await client.GetDeviceInfoAsync(token);
            Record(new { Kind = "device", Mtu = getMtu(), self.Name, device.Model, device.SemanticVersion, device.FirmwareProtocolVersion });
            Console.WriteLine($"Queue probe ready: ATT MTU={getMtu()?.ToString() ?? "n/a (self-test)"}; firmware={device.SemanticVersion}");
            await client.DrainMessagesAsync(token);
            await client.FlushEventsAsync(token);
            Record(new { Kind = "drain-complete", Count = messages.Count });
            Console.WriteLine($"Drain complete: {messages.Count} decoded queue items; NO_MORE_MESSAGES received.");
        }
        catch (Exception error)
        {
            Record(new { Kind = "probe-failed", Error = error.ToString() });
            throw;
        }
        finally
        {
            try
            {
                await client.DisconnectAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
                await cleanup().WaitAsync(TimeSpan.FromSeconds(10));
                await client.FlushEventsAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                while (capture.TryDequeue(out var line)) await writer.WriteLineAsync(line);
                await writer.FlushAsync();
                foreach (var (kind, slot, bytes) in messages)
                    Console.WriteLine($"Message kind={kind} slot={slot?.ToString() ?? "n/a"} text-UTF8-bytes={bytes}");
                transport.PrintStatistics();
                Console.WriteLine("Local raw capture saved (includes message contents; do not publish).");
            }
        }
    }

    internal static async Task SelfTestAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ble-queue-probe-{Guid.NewGuid():N}.jsonl");
        try
        {
            await using var server = new FakeCompanionServer();
            server.Start();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await RunAsync(new TcpMeshCoreTransport("127.0.0.1", server.Port), path,
                () => null, () => Task.CompletedTask, deadline.Token);
            await server.Completion;
            var rows = (await File.ReadAllLinesAsync(path)).Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                if (!rows.Any(row => row.RootElement.GetProperty("Kind").GetString() == "drain-complete"
                    && row.RootElement.GetProperty("Count").GetInt32() == 3)
                    || !rows.Any(row => row.RootElement.GetProperty("Kind").GetString() == "private")
                    || !rows.Any(row => row.RootElement.GetProperty("Kind").GetString() == "channel"))
                    throw new InvalidOperationException("Queue probe capture is incomplete.");
            }
            finally { foreach (var row in rows) row.Dispose(); }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
