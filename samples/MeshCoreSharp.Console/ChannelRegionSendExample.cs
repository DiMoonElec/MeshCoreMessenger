using System.Text;
using System.Text.Json;
using MeshCoreSharp;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;
using MeshCoreSharp.Transport;

// Opt-in, one text SEND only. Local acceptance is not an over-the-air delivery acknowledgement.
internal static class ChannelRegionSendExample
{
    internal sealed record Request(string ChannelName, string RegionName, string Text, string? ExpectedNodePublicKey = null);
    private sealed class Receipt(Request request)
    {
        public int SchemaVersion => 1;
        public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? CompletedUtc { get; set; }
        public string? NodeName { get; set; }
        public string? NodePublicKey { get; set; }
        public string ChannelName => request.ChannelName;
        public byte? ChannelIndex { get; set; }
        public string RegionName => request.RegionName;
        public string ScopeSelection => "ExplicitRegion";
        public string Text => request.Text;
        public int TextUtf8Bytes => Encoding.UTF8.GetByteCount(request.Text);
        public int? MaximumTextBytes { get; set; }
        public uint? Timestamp { get; set; }
        public bool Accepted { get; set; }
        public bool RemoteDeliveryConfirmed => false;
        public string Status { get; set; } = "NotSent";
        public string Cleanup { get; set; } = "NotRequired";
        public bool? DefaultUnchanged { get; set; }
        public string? Error { get; set; }
    }

    internal static async Task<int> RunAsync(IMeshCoreTransport transport, Request request, string outputPath, CancellationToken token)
    {
        // Fail on a bad/existing output path before connecting or transmitting.
        await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var receipt = new Receipt(request); var sendEntered = false;
        try
        {
            var region = RegionDefinition.Public(request.RegionName);
            var scope = FloodScopeSelection.ExplicitRegion(region.Key);
            await using var client = new MeshCoreClient(transport, new()
            { ApplicationName = "Scoped channel example", AutoReceiveMessages = false, CommandTimeout = TimeSpan.FromSeconds(10) });
            await client.ConnectAsync(token); var self = await client.StartAsync(token);
            receipt.NodeName = self.Name; receipt.NodePublicKey = Convert.ToHexString(self.PublicKey.Span);
            if (request.ExpectedNodePublicKey is { } expected &&
                !self.PublicKey.Span.SequenceEqual(Convert.FromHexString(expected)))
                throw new InvalidOperationException("Connected node does not match the requested full public key.");
            var before = await client.GetDefaultFloodScopeAsync(token);
            var channels = await client.GetChannelsAsync(token);
            var matches = channels.Where(c => !c.IsEmpty && string.Equals(c.Name, request.ChannelName, StringComparison.Ordinal)).OrderBy(c => c.Index).ToArray();
            if (matches.Length == 0) throw new InvalidOperationException("Requested channel is not configured; no channel was created and no text sent.");
            if (matches.Any(c => !c.Secret.Span.SequenceEqual(matches[0].Secret.Span)))
                throw new InvalidOperationException("Channel name matches different keys; select an unambiguous channel before sending.");
            var channel = matches[0];
            var fresh = await client.GetChannelAsync(channel.Index, token);
            if (fresh.Name != channel.Name || !fresh.Secret.Span.SequenceEqual(channel.Secret.Span))
                throw new InvalidOperationException("Channel changed during preflight; no text sent.");
            receipt.ChannelIndex = channel.Index;
            receipt.MaximumTextBytes = client.GetTextBudget(TextMessageKind.Channel, scope).MaximumTextBytes;
            if (receipt.TextUtf8Bytes > receipt.MaximumTextBytes) throw new ArgumentException("Text exceeds the actual scoped UTF-8 budget.");
            receipt.Timestamp = checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            Console.WriteLine($"Sending ONCE: node={self.Name}, channel={channel.Name}, slot={channel.Index}, region={region.Name}, UTF8={receipt.TextUtf8Bytes}/{receipt.MaximumTextBytes}, text={JsonSerializer.Serialize(request.Text)}");
            sendEntered = true;
            var result = await client.SendChannelTextAsync(channel.Index, request.Text, receipt.Timestamp.Value, scope, token);
            receipt.Accepted = true;
            receipt.Status = "Accepted";
            receipt.Cleanup = result.ScopeCleanupError is null ? "ResetConfirmed" : "Failed";
            if (result.ScopeCleanupError is { } cleanup)
            {
                receipt.Status = "AcceptedCleanupFailed"; receipt.Error = cleanup.Message;
                return 3; // Accepted text must never be replayed merely because RESET failed.
            }
            var after = await client.GetDefaultFloodScopeAsync(token);
            receipt.DefaultUnchanged = before.State == after.State && before.Name == after.Name && Equals(before.Key, after.Key);
            if (receipt.DefaultUnchanged != true) throw new InvalidOperationException("Default readback changed during the example; no default SET was issued.");
            await client.DisconnectAsync(token); await client.FlushEventsAsync(token);
            return 0;
        }
        catch (Exception error)
        {
            receipt.Error = $"{error.GetType().Name}: {error.Message}";
            if (receipt.Accepted) receipt.Status = "AcceptedVerificationFailed";
            else if (error is MeshCoreCommandException { Command: CommandType.SendChannelTextMessage }) receipt.Status = "Rejected";
            else if (!sendEntered || error is MeshCoreException { TextSendWasInvoked: false } or MeshCoreTextSendCanceledException or ArgumentException or NotSupportedException)
                receipt.Status = "NotSent";
            else receipt.Status = "Unknown";
            return receipt.Accepted ? 3 : 1;
        }
        finally
        {
            receipt.CompletedUtc = DateTimeOffset.UtcNow;
            var json = JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine("CHANNEL_REGION_SEND_JSON_BEGIN"); Console.WriteLine(json); Console.WriteLine("CHANNEL_REGION_SEND_JSON_END");
            await using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
            await writer.WriteAsync(json); await writer.FlushAsync();
            Console.WriteLine($"Agent-readable receipt: {Path.GetFullPath(outputPath)}");
        }
    }

    internal static async Task<int> SelfTestAsync()
    {
        foreach (var mode in new[] { "success", "set-rejected", "send-rejected", "cleanup-failed", "missing-channel" })
        {
            await using var server = new FakeCompanionServer { FirmwareProtocolVersion = 14, PrimaryChannelName = "#test" };
            server.RejectTemporaryScopeSet = mode == "set-rejected";
            server.RejectChannelSend = mode == "send-rejected";
            if (mode == "cleanup-failed") server.BeforeChannelResponse = () => { server.RejectTemporaryScopeSet = true; return Task.CompletedTask; };
            server.Start();
            var path = Path.Combine(Path.GetTempPath(), $"channel-region-{Guid.NewGuid():N}.json");
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var expectedKey = Convert.ToHexString(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
                var exit = await RunAsync(new MeshCoreSharp.Transport.Tcp.TcpMeshCoreTransport("127.0.0.1", server.Port),
                    new(mode == "missing-channel" ? "#absent" : "#test", "ru-ros", "ИИ-агент: тест регионов", expectedKey), path, deadline.Token);
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path)); var receipt = doc.RootElement;
                var accepted = mode is "success" or "cleanup-failed";
                if (receipt.GetProperty("Accepted").GetBoolean() != accepted || exit != (mode == "success" ? 0 : mode == "cleanup-failed" ? 3 : 1) ||
                    server.ChannelSendCount != (mode is "set-rejected" or "missing-channel" ? 0 : 1) ||
                    server.ChannelScopeKeys.Any(k => !k.AsSpan().SequenceEqual(RegionDefinition.Public("ru-ros").Key.ToArray())) ||
                    (accepted && server.LastChannelText != "ИИ-агент: тест регионов"))
                    throw new InvalidOperationException($"Scoped channel example self-test failed: {mode}.");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
        Console.WriteLine("CHANNEL REGION SELF-TEST PASSED: explicit wire scope, Unicode text, one SEND, rejected SET/SEND, accepted cleanup failure, missing channel.");
        return 0;
    }
}
