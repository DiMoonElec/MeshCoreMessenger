using System.Globalization;
using System.Runtime.CompilerServices;
using MeshCoreSharp;
using MeshCoreSharp.Protocol;
using MeshCoreSharp.Transport;
using MeshCoreSharp.Transport.Tcp;

internal sealed record BleProbeArguments(string DeviceId, int Cycles, int MaximumFrameSize)
{
    internal static bool TryParse(string[] args, out BleProbeArguments? result)
    {
        result = null;
        if (args.Length is < 2 or > 5 || args[0] != "--ble-probe"
            || string.IsNullOrWhiteSpace(args[1]) || args[1].StartsWith("--", StringComparison.Ordinal)) return false;
        var cycles = 2;
        var index = 2;
        if (index < args.Length && args[index] != "--frame-limit")
        {
            if (!int.TryParse(args[index++], NumberStyles.None, CultureInfo.InvariantCulture, out cycles)
                || cycles is < 1 or > 3) return false;
        }
        var limit = ProtocolLimits.CurrentFirmwareMaxFrameSize;
        if (index < args.Length)
        {
            if (args.Length - index != 2 || args[index] != "--frame-limit"
                || !int.TryParse(args[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out limit)
                || limit is < 1 or > ProtocolLimits.CurrentFirmwareMaxFrameSize) return false;
        }
        result = new(args[1], cycles, limit);
        return true;
    }
}

// Defense against accidentally enabling message draining or adding a configuration command to this probe.
// Log only types/lengths: channel keys, contact details, messages and raw buffers never enter the report.
internal sealed class ReadOnlyProbeTransport(IMeshCoreTransport inner) : IMeshCoreTransport
{
    private readonly object _sync = new();
    private readonly Dictionary<byte, (int Count, int Min, int Max)> _received = [];
    private readonly Dictionary<byte, (int Count, int Min, int Max)> _sent = [];
    public bool IsConnected => inner.IsConnected;
    public Task ConnectAsync(CancellationToken cancellationToken = default) => inner.ConnectAsync(cancellationToken);
    public Task DisconnectAsync(CancellationToken cancellationToken = default) => inner.DisconnectAsync(cancellationToken);
    public ValueTask DisposeAsync() => inner.DisposeAsync();
    internal static bool Allows(CommandType type) => type is CommandType.AppStart or CommandType.DeviceQuery
        or CommandType.GetDeviceTime or CommandType.GetContacts or CommandType.GetChannel;

    public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
    {
        if (frame.IsEmpty || !Allows((CommandType)frame.Span[0]))
            throw new InvalidOperationException("The BLE read-only probe blocked a command outside its allowlist.");
        lock (_sync) Record(_sent, frame);
        return inner.SendAsync(frame, cancellationToken);
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var frame in inner.ReceiveAsync(cancellationToken).ConfigureAwait(false))
        {
            lock (_sync) Record(_received, frame);
            yield return frame;
        }
    }

    private static void Record(Dictionary<byte, (int Count, int Min, int Max)> target, ReadOnlyMemory<byte> frame)
    {
        var type = frame.IsEmpty ? (byte)0 : frame.Span[0];
        target[type] = target.TryGetValue(type, out var old)
            ? (old.Count + 1, Math.Min(old.Min, frame.Length), Math.Max(old.Max, frame.Length))
            : (1, frame.Length, frame.Length);
    }

    internal void PrintStatistics()
    {
        lock (_sync)
        {
            foreach (var (type, stats) in _sent.OrderBy(item => item.Key))
                Console.WriteLine($"TX type=0x{type:X2} count={stats.Count} bytes={stats.Min}..{stats.Max}");
            foreach (var (type, stats) in _received.OrderBy(item => item.Key))
                Console.WriteLine($"RX type=0x{type:X2} count={stats.Count} bytes={stats.Min}..{stats.Max}");
        }
    }
}

internal static class BleReadOnlyProbe
{
    internal static async Task RunAsync(ReadOnlyProbeTransport transport, int cycles, Func<ushort?> getMtu,
        Func<Task> cleanup, CancellationToken cancellationToken)
    {
        await using var client = new MeshCoreClient(transport, new()
        {
            ApplicationName = "MeshCoreSharp.BLE8", AutoReceiveMessages = false,
            CommandTimeout = TimeSpan.FromSeconds(15), ContactsInactivityTimeout = TimeSpan.FromSeconds(15),
            ContactsAbsoluteTimeout = TimeSpan.FromSeconds(90),
        });
        client.BackgroundError += (_, e) => Console.Error.WriteLine($"Background: {e.Exception.GetType().Name}: {e.Exception.Message}");
        try
        {
            for (var cycle = 1; cycle <= cycles; cycle++)
            {
                Console.WriteLine($"Cycle {cycle}/{cycles}: connect");
                await client.ConnectAsync(cancellationToken);
                Console.WriteLine($"GATT Ready; ATT MTU={getMtu()?.ToString() ?? "n/a (self-test)"}");
                var self = await client.StartAsync(cancellationToken);
                Console.WriteLine($"SelfInfo: name={self.Name}");
                var device = await client.GetDeviceInfoAsync(cancellationToken);
                Console.WriteLine($"DeviceInfo: model={device.Model}; version={device.SemanticVersion}; build={device.FirmwareBuild}; protocol={device.FirmwareProtocolVersion}");
                var time = await client.GetDeviceTimeAsync(cancellationToken);
                Console.WriteLine($"DeviceTime: {time:O}");
                var contacts = await client.GetContactsAsync(cancellationToken);
                Console.WriteLine($"Contacts: {contacts.Count} (details omitted)");
                var channels = await client.GetChannelsAsync(cancellationToken);
                Console.WriteLine($"Channels: {channels.Count} slots, {channels.Count(item => !item.IsEmpty)} configured (names/keys omitted)");
                await client.FlushEventsAsync(cancellationToken);
                await client.DisconnectAsync(cancellationToken);
                await cleanup().WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                Console.WriteLine($"Cycle {cycle}: disconnected; physical cleanup complete");
            }
        }
        finally { transport.PrintStatistics(); }
    }
}

internal static class BleProbeSelfTest
{
    internal static async Task<int> RunAsync()
    {
        foreach (var invalid in new string[][] { [], ["--ble-probe"], ["--ble-probe", " "],
            ["--ble-probe", "--list-ble"], ["--ble-probe", "id", "0"], ["--ble-probe", "id", "4"],
            ["--ble-probe", "id", "x"], ["--ble-probe", "id", "1", "extra"], ["--other", "id"],
            ["--ble-probe", "id", "--frame-limit"], ["--ble-probe", "id", "--frame-limit", "0"],
            ["--ble-probe", "id", "--frame-limit", "177"], ["--ble-probe", "id", "--frame-limit", "abc"] })
            if (BleProbeArguments.TryParse(invalid, out _)) throw new InvalidOperationException("Invalid BLE probe arguments accepted.");
        if (!BleProbeArguments.TryParse(["--ble-probe", "opaque ID"], out var defaults) || defaults?.Cycles != 2
            || defaults.MaximumFrameSize != ProtocolLimits.CurrentFirmwareMaxFrameSize
            || !BleProbeArguments.TryParse(["--ble-probe", "id", "3"], out var explicitCycles) || explicitCycles?.Cycles != 3
            || !BleProbeArguments.TryParse(["--ble-probe", "id", "2", "--frame-limit", "173"], out var limited) || limited?.MaximumFrameSize != 173)
            throw new InvalidOperationException("Valid BLE probe arguments rejected.");
        var allowed = new[] { CommandType.AppStart, CommandType.DeviceQuery, CommandType.GetDeviceTime,
            CommandType.GetContacts, CommandType.GetChannel };
        for (var value = 0; value <= byte.MaxValue; value++)
            if (ReadOnlyProbeTransport.Allows((CommandType)value) != allowed.Contains((CommandType)value))
                throw new InvalidOperationException("Probe command allowlist mismatch.");
#if WINDOWS
        await using (var dispatcher = new BleProbeDispatcher())
        {
            await dispatcher.Ready.WaitAsync(TimeSpan.FromSeconds(5));
            var invoked = false;
            await dispatcher.InvokeAsync(() => invoked = Thread.CurrentThread.GetApartmentState() == ApartmentState.STA);
            if (!invoked) throw new InvalidOperationException("BLE probe device access must run on the STA host.");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try { await dispatcher.InvokeAsync(() => throw new InvalidDataException("Cancelled work ran"), cancelled.Token); }
            catch (OperationCanceledException) { }
        }
#endif
        await using var server = new FakeCompanionServer();
        server.Start();
        var transport = new ReadOnlyProbeTransport(new TcpMeshCoreTransport("127.0.0.1", server.Port));
        foreach (var forbidden in new[] { Array.Empty<byte>(), new byte[] { (byte)CommandType.SyncNextMessage },
            new byte[] { (byte)CommandType.SetDeviceTime }, new byte[] { (byte)CommandType.SendTextMessage } })
        {
            try { await transport.SendAsync(forbidden); throw new InvalidDataException("Forbidden command was sent."); }
            catch (InvalidOperationException) { }
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await BleReadOnlyProbe.RunAsync(transport, 1, () => null, () => Task.CompletedTask, timeout.Token);
        await server.Completion;
        Console.WriteLine("BLE PROBE SELF-TEST PASSED (no Bluetooth used)");
        return 0;
    }
}
