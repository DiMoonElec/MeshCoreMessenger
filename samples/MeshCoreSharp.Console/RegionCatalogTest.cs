using System.Text.Json;
using MeshCoreSharp.Transport;

// Data-only output: full repeater identities, exact names and explicit partial/failure outcomes.
internal sealed class RegionCatalogTestCapture
{
    public int SchemaVersion => 1;
    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedUtc { get; set; }
    public bool Completed { get; set; }
    public bool PotentiallyIncomplete => true;
    public string? NodePublicKey { get; set; }
    public string? NodeName { get; set; }
    public string? Error { get; set; }
    public List<Scan> Scans { get; } = [];
    public List<Repeater> Repeaters { get; } = [];
    public object[] Regions => Repeaters.Where(r => r.Status == "Success")
        .SelectMany(r => r.Regions.Select(name => (Name: name, r.PublicKey)))
        .GroupBy(r => r.Name, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
        .Select(g => (object)new { Name = g.Key, SourcePublicKeys = g.Select(r => r.PublicKey).Distinct(StringComparer.Ordinal).ToArray() }).ToArray();
    internal sealed record Scan(int Attempt, uint Tag, int Replies, bool CapacityReached);
    internal sealed class Repeater
    {
        public required string PublicKey { get; init; }
        public string? Name { get; set; }
        public int Attempts { get; set; }
        public string Status { get; set; } = "NotAttempted";
        public string[] Regions { get; set; } = [];
        public uint? Tag { get; set; }
        public uint? RemoteClock { get; set; }
        public string Restoration { get; set; } = "NotRequired";
        public string? Error { get; set; }
    }
}

internal static class RegionCatalogTest
{
    internal static async Task<int> SelfTestAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"region-catalog-{Guid.NewGuid():N}.json");
        try
        {
            await using var server = new FakeCompanionServer { FirmwareProtocolVersion = 14 };
            for (byte key = 0xC1; key <= 0xC7; key++) server.AddRepeater(key, key == 0xC1 ? (byte)0xFF : (byte)0, "Area,*");
            server.BeforeNeighborScan = round =>
            {
                if (round == 2) for (byte key = 0xC1; key <= 0xC7; key++) server.NeighborRepeaters.Add(key);
            };
            var firstRequests = 0;
            server.BeforeRegionResponse = peer =>
            {
                if (peer == 0xC1 && ++firstRequests == 1) server.SilentRegionRepeaters.Add(peer);
                else server.SilentRegionRepeaters.Remove(peer);
                return Task.CompletedTask;
            };
            server.Start();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var exit = await RunAsync(new MeshCoreSharp.Transport.Tcp.TcpMeshCoreTransport("127.0.0.1", server.Port), path, deadline.Token,
                new(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(30)));
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path)); var root = document.RootElement;
            if (exit != 0 || server.NeighborScanCount != 2 || server.RegionRequests.Count != 8 || server.ContactRoutes[0xC1] != 0xFF ||
                !root.GetProperty("Completed").GetBoolean() || root.GetProperty("Repeaters").GetArrayLength() != 7 ||
                root.GetProperty("Regions").GetArrayLength() != 2 || root.GetProperty("Regions")[0].GetProperty("SourcePublicKeys").GetArrayLength() != 7 ||
                root.GetProperty("Repeaters")[0].GetProperty("Attempts").GetInt32() != 2)
                throw new InvalidOperationException("Region catalog specialized self-test failed.");
            Console.WriteLine("REGION CATALOG SELF-TEST PASSED: discovery stops on second round; all seven repeaters queried; timeout retry; JSON sources; restore.");
            return 0;
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    internal static async Task<int> RunAsync(IMeshCoreTransport transport, string outputPath, CancellationToken token,
        RegionMeshProbe.Timing? timing = null)
    {
        // Reserve a new file before touching hardware; never overwrite an earlier measurement.
        await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var capture = new RegionCatalogTestCapture();
        try
        {
            Console.WriteLine("SPECIALIZED REGION CATALOG TEST: explicit invocation only; first successful discovery, ALL full-key zero-hop repeaters, <=3 requests each.");
            await RegionMeshProbe.RunAsync(transport, timing ?? RegionMeshProbe.Timing.Hardware,
                line => Console.WriteLine($"{DateTimeOffset.UtcNow:O} {line}"), token, capture: capture);
            capture.Completed = true;
            return capture.Repeaters.Count > 0 && capture.Repeaters.All(r => r.Status == "Success" && r.Restoration != "Conflict") ? 0 : 3;
        }
        catch (Exception error)
        {
            capture.Error = $"{error.GetType().Name}: {error.Message}";
            return error is OperationCanceledException ? 130 : 1;
        }
        finally
        {
            capture.CompletedUtc = DateTimeOffset.UtcNow;
            var json = JsonSerializer.Serialize(capture, new JsonSerializerOptions { WriteIndented = true });
            await using var writer = new StreamWriter(output, new System.Text.UTF8Encoding(false), leaveOpen: true);
            await writer.WriteAsync(json); await writer.FlushAsync();
            Console.WriteLine("REGION_CATALOG_JSON_BEGIN"); Console.WriteLine(json); Console.WriteLine("REGION_CATALOG_JSON_END");
            Console.WriteLine($"Agent-readable result: {Path.GetFullPath(outputPath)}");
        }
    }
}
