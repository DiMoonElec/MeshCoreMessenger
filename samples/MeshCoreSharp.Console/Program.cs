using MeshCoreSharp;
using MeshCoreSharp.Transport.Tcp;

if (args.Length == 1 && args[0] == "--self-test")
{
    await using var server = new FakeCompanionServer();
    server.Start();
    Console.WriteLine($"Fake companion: 127.0.0.1:{server.Port}");
    await RunAsync("127.0.0.1", server.Port, 0);
    await server.Completion;
    Console.WriteLine("SELF-TEST PASSED");
    return 0;
}

if (args.Length < 2 || !int.TryParse(args[1], out var port))
{
    Console.WriteLine("Real companion:");
    Console.WriteLine("  dotnet run --project samples/MeshCoreSharp.Console -- <host> <port> [observeSeconds]");
    Console.WriteLine();
    Console.WriteLine("Local framing/router test:");
    Console.WriteLine("  dotnet run --project samples/MeshCoreSharp.Console -- --self-test");
    return 2;
}

var host = args[0];
var observeSeconds = args.Length >= 3 && int.TryParse(args[2], out var value) ? value : 10;
await RunAsync(host, port, observeSeconds);
return 0;

static async Task RunAsync(string host, int port, int observeSeconds)
{
    await using var transport = new TcpMeshCoreTransport(host, port);
    await using var client = new MeshCoreClient(
        transport,
        new MeshCoreClientOptions
        {
            ApplicationName = "MeshCoreSharp.Console",
            ApplicationProtocolVersion = 3,
            CommandTimeout = TimeSpan.FromSeconds(5),
        });

    client.PacketReceived += (_, e) =>
    {
        Console.WriteLine($"RX 0x{e.Packet.RawType:X2} ({e.Packet.Type}) [{Convert.ToHexString(e.Packet.RawFrame.Span)}]");
    };

    client.PushPacketReceived += (_, e) =>
    {
        Console.WriteLine($"PUSH 0x{e.Packet.RawType:X2} ({e.Packet.Type})");
    };

    client.UnhandledPacketReceived += (_, e) =>
    {
        Console.WriteLine($"UNHANDLED 0x{e.Packet.RawType:X2} ({e.Packet.Type})");
    };

    client.BackgroundError += (_, e) =>
    {
        Console.Error.WriteLine($"BACKGROUND ERROR: {e.Exception}");
    };

    Console.WriteLine($"Connecting to {host}:{port}...");
    await client.ConnectAsync();

    var self = await client.StartAsync();
    Console.WriteLine($"Companion: {self.Name}");
    Console.WriteLine($"Public key: {self.PublicKeyHex}");
    Console.WriteLine($"Radio: {self.RadioFrequencyMHz:0.###} MHz, {self.RadioBandwidthKHz:0.###} kHz, SF{self.SpreadingFactor}, CR{self.CodingRate}");

    var device = await client.GetDeviceInfoAsync();
    Console.WriteLine($"Firmware protocol: {device.FirmwareProtocolVersion}");
    Console.WriteLine($"Model: {device.Model ?? "<not supplied>"}");
    Console.WriteLine($"Version: {device.SemanticVersion ?? "<not supplied>"}");
    Console.WriteLine($"Build: {device.FirmwareBuild ?? "<not supplied>"}");

    var time = await client.GetDeviceTimeAsync();
    Console.WriteLine($"Device time: {time:O}");

    var battery = await client.GetBatteryAndStorageAsync();
    Console.WriteLine($"Battery: {battery.BatteryMillivolts} mV");
    if (battery.UsedStorageKb.HasValue && battery.TotalStorageKb.HasValue)
        Console.WriteLine($"Storage: {battery.UsedStorageKb} / {battery.TotalStorageKb} KB");

    var contacts = await client.GetContactsAsync();
    Console.WriteLine($"Contacts: {contacts.Count}");
    foreach (var contact in contacts)
        Console.WriteLine($"  {contact.Name} ({contact.AdvertisementType}) {contact.PublicKeyHex}");

    if (observeSeconds > 0)
    {
        Console.WriteLine($"Observing asynchronous packets for {observeSeconds} s...");
        await Task.Delay(TimeSpan.FromSeconds(observeSeconds));
    }

    await client.DisconnectAsync();
}
