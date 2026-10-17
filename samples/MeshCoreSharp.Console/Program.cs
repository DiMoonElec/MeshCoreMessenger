using MeshCoreSharp;
using MeshCoreSharp.Models;
using MeshCoreSharp.Transport;
using MeshCoreSharp.Transport.Serial;
using MeshCoreSharp.Transport.Tcp;
using MeshCoreSharp.Transport.Ble;
using MeshCoreSharp.Exceptions;

if (args.Length == 1 && args[0] == "--region-probe-self-test")
    return await RegionMeshProbe.SelfTestAsync();

if (args.Length >= 1 && args[0] == "--ble-drain-probe")
{
    if (args.Length != 3 || string.IsNullOrWhiteSpace(args[1]) || args[1].StartsWith("--", StringComparison.Ordinal)
        || string.IsNullOrWhiteSpace(args[2]) || args[2].StartsWith("--", StringComparison.Ordinal))
    {
        Console.Error.WriteLine("Usage: --ble-drain-probe <opaque-device-id> <new-local-capture.jsonl>");
        return 2;
    }
#if WINDOWS
    return await WindowsBleProbe.RunAsync(new(args[1], 1, MeshCoreSharp.Protocol.ProtocolLimits.CurrentFirmwareMaxFrameSize), args[2]);
#else
    Console.Error.WriteLine("BLE queue probe requires the Windows target.");
    return 1;
#endif
}

if (args.Length >= 1 && args[0] == "--ble-region-probe")
{
    if (args.Length is not (2 or 3) || string.IsNullOrWhiteSpace(args[1]) || args[1].StartsWith("--", StringComparison.Ordinal))
    { Console.Error.WriteLine("Usage: --ble-region-probe <opaque-device-id> [previously-observed-zero-hop-full-key-hex]"); return 2; }
    byte[]? target = null;
    if (args.Length == 3)
    {
        try { target = Convert.FromHexString(args[2]); }
        catch (FormatException) { Console.Error.WriteLine("Target must be a full 32-byte hexadecimal key."); return 2; }
        if (target.Length != MeshCoreSharp.Protocol.ProtocolLimits.PublicKeySize) { Console.Error.WriteLine("Target must be a full 32-byte hexadecimal key."); return 2; }
    }
#if WINDOWS
    return await WindowsBleProbe.RunRegionsAsync(args[1], target);
#else
    Console.Error.WriteLine("BLE region probe requires the Windows target."); return 1;
#endif
}

if (args.Length >= 1 && args[0] == "--ble-probe")
{
    if (!BleProbeArguments.TryParse(args, out var probe))
    {
        Console.Error.WriteLine("Usage: --ble-probe <opaque-device-id> [cycles=2, range 1..3] [--frame-limit <1..176>]");
        return 2;
    }
#if WINDOWS
    return await WindowsBleProbe.RunAsync(probe!);
#else
    Console.Error.WriteLine("BLE probe requires the Windows target.");
    return 1;
#endif
}

if (args.Length == 1 && args[0] == "--ble-probe-self-test")
    return await BleProbeSelfTest.RunAsync();

if (args.Length >= 1 && args[0] == "--list-ble")
{
    var seconds = 5;
    if (args.Length > 2 || (args.Length == 2 && (!int.TryParse(args[1], out seconds) || seconds is < 1 or > 60)))
    {
        Console.Error.WriteLine("Usage: --list-ble [seconds=5, range 1..60]");
        return 2;
    }
    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    Console.CancelKeyPress += cancel;
    try
    {
        var devices = await new BleDeviceDiscovery().FindAsync(new()
        {
            ScanDuration = TimeSpan.FromSeconds(seconds),
        }, cancellation.Token);
        Console.WriteLine($"BLE candidates: {devices.Count} (unverified; no GATT connection opened)");
        foreach (var device in devices)
        {
            Console.WriteLine($"{device.Name ?? "(unnamed)"} | paired={device.IsPaired} | NUS={device.AdvertisesCompanionService}");
            Console.WriteLine($"  {device.DeviceId}");
        }
        return 0;
    }
    catch (MeshCoreBleTransportException error)
    {
        Console.Error.WriteLine($"BLE discovery: {error.Reason}: {error.Message}");
        return 1;
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return 130; }
    catch (Exception error)
    {
        Console.Error.WriteLine($"BLE discovery failed: {error.GetType().Name}; HRESULT=0x{error.HResult:X8}: {error.Message}");
        return 1;
    }
    finally { Console.CancelKeyPress -= cancel; }
}

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
    Console.WriteLine("  dotnet run --project samples/MeshCoreSharp.Console -- --list-ble [seconds=5]");
    Console.WriteLine("  dotnet run --project samples/MeshCoreSharp.Console -- --ble-probe <opaque-device-id> [cycles=2] [--frame-limit <1..176>]");
    Console.WriteLine("  dotnet run --project samples/MeshCoreSharp.Console -- --ble-probe-self-test");
    Console.WriteLine("  dotnet run --project samples/MeshCoreSharp.Console -- --ble-drain-probe <opaque-device-id> <new-local-capture.jsonl>");
    Console.WriteLine("  dotnet run --project samples/MeshCoreSharp.Console -- --ble-region-probe <opaque-device-id> [previously-observed-zero-hop-full-key-hex]");
    Console.WriteLine("  dotnet run --project samples/MeshCoreSharp.Console -- --region-probe-self-test");
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
