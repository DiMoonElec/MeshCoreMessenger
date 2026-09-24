using MeshCoreSharp;
using MeshCoreSharp.Models;
using MeshCoreSharp.Transport;
using MeshCoreSharp.Transport.Serial;
using MeshCoreSharp.Transport.Tcp;

if (args.Length == 1 && args[0] == "--list-ports")
{
    foreach (var name in SerialMeshCoreTransport.GetPortNames())
        Console.WriteLine(name);
    return 0;
}

if (args.Length >= 2 && args[0] == "--serial")
{
    var baudRate = 115200;
    var serialObserveSeconds = 10;
    if (args.Length > 4 ||
        (args.Length >= 3 && (!int.TryParse(args[2], out baudRate) || baudRate <= 0)) ||
        (args.Length >= 4 && (!int.TryParse(args[3], out serialObserveSeconds) || serialObserveSeconds < 0)))
    {
        Console.Error.WriteLine("Usage: --serial <portName> [baudRate=115200] [observeSeconds=10]");
        return 2;
    }
    await RunAsync(new SerialMeshCoreTransport(args[1], baudRate), args[1], serialObserveSeconds);
    return 0;
}

if (args.Length == 1 && args[0] == "--self-test")
{
    await using var server = new FakeCompanionServer();
    server.Start();
    Console.WriteLine($"Fake companion: 127.0.0.1:{server.Port}");
    await RunAsync(new TcpMeshCoreTransport("127.0.0.1", server.Port), $"127.0.0.1:{server.Port}", 0, expectedMessages: 3);
    await server.Completion;
    Console.WriteLine("SELF-TEST PASSED");
    return 0;
}

if (args.Length < 2 || !int.TryParse(args[1], out var port))
{
    Console.WriteLine("Real companion:");
    Console.WriteLine("  dotnet run --project samples/MeshCoreSharp.Console -- <host> <port> [observeSeconds]");
    Console.WriteLine("  dotnet run --project samples/MeshCoreSharp.Console -- --serial <portName> [baudRate] [observeSeconds]");
    Console.WriteLine("  dotnet run --project samples/MeshCoreSharp.Console -- --list-ports");
    Console.WriteLine();
    Console.WriteLine("Local framing/router test:");
    Console.WriteLine("  dotnet run --project samples/MeshCoreSharp.Console -- --self-test");
    return 2;
}

var host = args[0];
var observeSeconds = args.Length >= 3 && int.TryParse(args[2], out var value) ? value : 10;
await RunAsync(new TcpMeshCoreTransport(host, port), $"{host}:{port}", observeSeconds);
return 0;

static async Task RunAsync(IMeshCoreTransport transport, string destination, int observeSeconds, int expectedMessages = 0)
{
    await using var client = new MeshCoreClient(
        transport,
        new MeshCoreClientOptions
        {
            ApplicationName = "MeshCoreSharp.Console",
            ApplicationProtocolVersion = 3,
            CommandTimeout = TimeSpan.FromSeconds(5),
        });

    var messageCount = 0;
    var allMessages = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    client.MessageReceived += (_, e) =>
    {
        Console.WriteLine(e.Message switch
        {
            ContactMessage message => $"PRIVATE {message.ContactPublicKeyPrefixHex}: {message.Text}",
            ChannelMessage message => $"CHANNEL {message.ChannelIndex}: {message.Text}",
            ChannelDataMessage message => $"DATA channel={message.ChannelIndex}, type=0x{message.DataType:X4}: {Convert.ToHexString(message.Data.Span)}",
            _ => $"MESSAGE {e.Message}",
        });
        if (Interlocked.Increment(ref messageCount) == expectedMessages)
            allMessages.TrySetResult();
    };

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

    Console.WriteLine($"Connecting to {destination}...");
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

    if (expectedMessages > 0)
    {
        await allMessages.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (messageCount != expectedMessages)
            throw new InvalidDataException($"Expected {expectedMessages} messages, received {messageCount}.");
    }

    await client.DisconnectAsync();
}
