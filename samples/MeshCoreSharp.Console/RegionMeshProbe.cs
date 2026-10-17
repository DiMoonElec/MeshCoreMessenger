using System.Collections.Concurrent;
using System.Text.Json;
using MeshCoreSharp;
using MeshCoreSharp.Exceptions;
using MeshCoreSharp.Models;
using MeshCoreSharp.Protocol;
using MeshCoreSharp.Protocol.Packets;
using MeshCoreSharp.Transport;

// Explicit diagnostic policy only; production discovery never retries implicitly.
internal static class RegionMeshProbe
{
    internal sealed record Timing(TimeSpan ScanWindow, TimeSpan Pause, TimeSpan ReplyTimeout)
    {
        internal static Timing Hardware { get; } = new(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }
    internal sealed record Result(int Neighbors, int Responses, int Unanswered, int RestoreConflicts);
    internal static async Task<Result> RunAsync(IMeshCoreTransport transport, Timing timing, Action<string> log, CancellationToken token,
        byte[]? previouslyObservedZeroHopTarget = null, RegionCatalogTestCapture? capture = null)
    {
        await using var client = new MeshCoreClient(transport, new()
        {
            ApplicationName = "Region hardware probe", AutoReceiveMessages = false, CommandTimeout = TimeSpan.FromSeconds(10),
        });
        var observations = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        void Changed(ReadOnlyMemory<byte> key) => observations.AddOrUpdate(Convert.ToHexString(key.Span), 1, (_, v) => v + 1);
        client.AdvertisementReceived += (_, e) => Changed(e.Advertisement.PublicKey);
        client.PushPacketReceived += (_, e) =>
        {
            if (e.Packet is PathUpdatedPacket path) Changed(path.PublicKey);
            if (e.Packet is BinaryResponsePacket binary)
                log($"binary tag={binary.Tag:X8} length={binary.RawFrame.Length} payloadHex={Convert.ToHexString(binary.GetPayload())}");
        };
        client.BackgroundError += (_, e) => log($"background {e.Exception.GetType().Name}: {e.Exception.Message}");
        log(capture is null
            ? "OPT-IN RF: 3 discovery rounds (or explicit previously observed neighbor), <=5 zero-hop targets, <=3 queries/target; temporary route preparation and implicit contact creation allowed. No messages/drain/default/channel writes."
            : "OPT-IN RF CATALOG: <=3 discovery rounds until first success, ALL discovered full-key zero-hop targets, <=3 queries/target; temporary route preparation and implicit contact creation allowed. No messages/drain/default/channel writes.");
        await client.ConnectAsync(token); var self = await client.StartAsync(token); var info = await client.GetDeviceInfoAsync(token);
        if (capture is not null) { capture.NodePublicKey = Convert.ToHexString(self.PublicKey.Span); capture.NodeName = self.Name; }
        log($"node={self.Name} key={Convert.ToHexString(self.PublicKey.Span)} firmware={info.SemanticVersion} protocol={info.FirmwareProtocolVersion} model={info.Model}");
        var defaultBefore = await client.GetDefaultFloodScopeAsync(token); log($"default={defaultBefore.State} name={defaultBefore.Name}");
        var contacts = await client.GetContactsAsync(cancellationToken: token); log($"contacts={contacts.Count}");
        var neighbors = new Dictionary<string, DiscoveredNeighbor>(StringComparer.Ordinal);
        for (var round = 1; previouslyObservedZeroHopTarget is null && round <= 3; round++)
        {
            if (round > 1) await Pause(timing, token);
            var scan = await client.DiscoverRepeaterNeighborsAsync(new() { CollectionWindow = timing.ScanWindow }, token);
            log($"scan round={round} tag={scan.Tag:X8} replies={scan.Neighbors.Count} capacity={scan.CapacityReached}");
            capture?.Scans.Add(new(round, scan.Tag, scan.Neighbors.Count, scan.CapacityReached));
            foreach (var neighbor in scan.Neighbors)
            {
                var key = Convert.ToHexString(neighbor.GetKeyBytes());
                log($"neighbor key={key} full={neighbor.HasFullPublicKey} path={neighbor.PathDescriptor:X2} snr={neighbor.ReceivedSnr} inboundSnr={neighbor.InboundSnr} rssi={neighbor.ReceivedRssi}");
                if (neighbor.HasFullPublicKey && neighbor.PathDescriptor == 0) neighbors[key] = neighbor;
            }
            if (capture is not null && neighbors.Count > 0) break;
        }
        var targets = neighbors.Values.OrderByDescending(n => n.ReceivedSnr).Take(capture is null ? 5 : int.MaxValue).Select(n => n.GetKeyBytes()).ToArray();
        if (previouslyObservedZeroHopTarget is not null)
        {
            if (previouslyObservedZeroHopTarget.Length != ProtocolLimits.PublicKeySize) throw new ArgumentException("A full target key is required.");
            targets = [previouslyObservedZeroHopTarget.ToArray()];
            log($"explicit previously-observed zero-hop target={Convert.ToHexString(previouslyObservedZeroHopTarget)}; no fresh neighbor scan, current reachability unverified");
        }
        var responses = 0; var unanswered = 0; var conflicts = 0;
        if (capture is not null) foreach (var key in targets) capture.Repeaters.Add(new() { PublicKey = Convert.ToHexString(key) });
        foreach (var key in targets)
        {
            token.ThrowIfCancellationRequested();
            var hex = Convert.ToHexString(key);
            var captured = capture?.Repeaters.Single(r => r.PublicKey == hex);
            Contact? original = null; Contact? prepared = null; var preparedVersion = 0;
            var reply = false;
            try
            {
                original = await ReadContact(client, key, token);
                if (captured is not null) captured.Name = original?.Name;
                if (original is not null && original.AdvertisementType != AdvertisementType.Repeater)
                { if (captured is not null) captured.Status = "NotRepeater"; log($"skip key={hex}: current contact is not a repeater"); continue; }
                // Fresh zero-hop observation is the evidence for a direct reply path. Preserve full config.
                if (original is not null && original.OutPathLength != 0)
                {
                    await client.FlushEventsAsync(token); var version = observations.GetValueOrDefault(hex);
                    log($"prepare key={hex} name={original.Name} oldPath={original.OutPathLength:X2}");
                    if (captured is not null) captured.Restoration = "Unverified";
                    await client.AddOrUpdateContactAsync(ContactConfiguration.FromContact(original) with { OutPathLength = 0 }, token);
                    var proof = await client.GetContactAsync(key, token); await client.FlushEventsAsync(token);
                    if (!SameConfiguration(proof, original with { OutPathLength = 0 }) || observations.GetValueOrDefault(hex) != version)
                        throw new InvalidOperationException($"Preparation uncertain for {hex}; no blind restoration.");
                    prepared = proof; preparedVersion = version;
                }
                else log($"target key={hex} name={original?.Name ?? "(unknown; firmware may add contact)"} path=zero-hop");
                for (var attempt = 1; attempt <= 3; attempt++)
                {
                    // Separate, fresh transactions/tags after the prior terminal timeout. Never overlap requests.
                    await Pause(timing, token);
                    if (prepared is not null)
                    {
                        var current = await client.GetContactAsync(key, token); await client.FlushEventsAsync(token);
                        if (!SameSnapshot(current, prepared) || observations.GetValueOrDefault(hex) != preparedVersion)
                        { if (captured is not null) captured.Status = "ContactChanged"; log($"stop target={hex}: contact changed during retries"); break; }
                    }
                    log($"request key={hex} attempt={attempt}/3 timeoutSeconds={timing.ReplyTimeout.TotalSeconds}");
                    if (captured is not null) captured.Attempts = attempt;
                    try
                    {
                        var result = await client.RequestRegionsAsync(key, ReplyPath.ZeroHop, timing.ReplyTimeout, token);
                        log($"regions key={hex} attempt={attempt} tag={result.Tag:X8} clock={result.RemoteClock} potentiallyIncomplete=true names={JsonSerializer.Serialize(result.Names)}");
                        if (captured is not null) { captured.Status = "Success"; captured.Error = null; captured.Regions = result.Names.ToArray(); captured.Tag = result.Tag; captured.RemoteClock = result.RemoteClock; }
                        reply = true; responses++; break;
                    }
                    catch (MeshCoreTimeoutException) when (client.IsStarted)
                    { if (captured is not null) captured.Status = "Timeout"; log($"no response key={hex} attempt={attempt}; not evidence of absent regions"); }
                    catch (MeshCoreProtocolException error) when (capture is not null && client.IsStarted)
                    { captured!.Status = "Malformed"; captured.Error = error.Message; log($"malformed reply key={hex} attempt={attempt}: {error.Message}"); }
                    catch (MeshCoreCommandException error)
                    { if (captured is not null) { captured.Status = "Rejected"; captured.Error = error.ErrorCode.ToString(); } log($"rejected key={hex} code={error.ErrorCode}; no retry"); break; }
                }
                if (!reply) unanswered++;
                if (original is null)
                {
                    var added = await ReadContact(client, key, token);
                    log($"implicit contact key={hex} present={added is not null}; retained");
                }
            }
            catch (Exception error) when (capture is not null && client.IsStarted && error is MeshCoreProtocolException)
            {
                captured!.Status = "Malformed"; captured.Error = error.Message;
                log($"invalid reply key={hex}: {error.Message}; continuing with remaining repeaters");
            }
            catch (Exception error) when (capture is not null)
            {
                captured!.Status = error is OperationCanceledException ? "Cancelled" : "Failed";
                captured.Error = $"{error.GetType().Name}: {error.Message}";
                throw;
            }
            finally
            {
                if (prepared is not null)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    if (!client.IsStarted) throw new InvalidOperationException($"Connection lost; route restoration unverified for {hex}.");
                    var current = await client.GetContactAsync(key, cleanup.Token); await client.FlushEventsAsync(cleanup.Token);
                    if (!SameSnapshot(current, prepared) || observations.GetValueOrDefault(hex) != preparedVersion)
                    { if (captured is not null) captured.Restoration = "Conflict"; conflicts++; log($"restore CONFLICT key={hex}; newer contact preserved"); }
                    else
                    {
                        await client.AddOrUpdateContactAsync(ContactConfiguration.FromContact(original!), cleanup.Token);
                        var restored = await client.GetContactAsync(key, cleanup.Token);
                        if (!SameConfiguration(restored, original!)) throw new InvalidOperationException($"Restore readback mismatch for {hex}.");
                        log($"restore verified key={hex} path={restored.OutPathLength:X2}");
                        if (captured is not null) captured.Restoration = "Verified";
                    }
                }
            }
        }
        var defaultAfter = await client.GetDefaultFloodScopeAsync(token);
        if (defaultAfter.State != defaultBefore.State || defaultAfter.Name != defaultBefore.Name || !Equals(defaultAfter.Key, defaultBefore.Key))
            throw new InvalidOperationException("Default changed externally during probe; probe issued no default writes.");
        log($"summary zeroHopNeighbors={neighbors.Count} omitted={(capture is null ? Math.Max(0, neighbors.Count - 5) : 0)} responses={responses} unanswered={unanswered} restoreConflicts={conflicts} defaultUnchanged=true");
        await client.DisconnectAsync(token); await client.FlushEventsAsync(token);
        return new(neighbors.Count, responses, unanswered, conflicts);
    }

    private static Task Pause(Timing timing, CancellationToken token) => Task.Delay(timing.Pause +
        (timing.Pause >= TimeSpan.FromSeconds(1) ? TimeSpan.FromMilliseconds(Random.Shared.Next(0, 3000)) : TimeSpan.Zero), token);
    private static async Task<Contact?> ReadContact(MeshCoreClient client, byte[] key, CancellationToken token)
    {
        try { return await client.GetContactAsync(key, token); }
        catch (MeshCoreCommandException error) when (error.ErrorCode == MeshCoreErrorCode.NotFound) { return null; }
    }
    private static bool SameSnapshot(Contact a, Contact b) => a.LastModified == b.LastModified && SameConfiguration(a, b);
    private static bool SameConfiguration(Contact a, Contact b) => a.PublicKey.Span.SequenceEqual(b.PublicKey.Span) &&
        a.AdvertisementType == b.AdvertisementType && a.Flags == b.Flags && a.OutPathLength == b.OutPathLength &&
        a.OutPath.Span.SequenceEqual(b.OutPath.Span) && a.Name == b.Name && a.LastAdvertTimestamp == b.LastAdvertTimestamp &&
        a.AdvertisementLatitude == b.AdvertisementLatitude && a.AdvertisementLongitude == b.AdvertisementLongitude;

    internal static async Task<int> SelfTestAsync()
    {
        foreach (var conflict in new[] { false, true })
        {
            await using var server = new FakeCompanionServer { FirmwareProtocolVersion = 14 };
            server.AddRepeater(0xC1, 0xFF, "Area,*"); server.NeighborRepeaters.Add(0xC1);
            if (!conflict) { server.NeighborRepeaters.Add(0xC3); server.RepeaterRegionCsv[0xC3] = "$private"; }
            server.BeforeRegionResponse = async peer =>
            {
                if (conflict) await server.SendPathUpdatedAsync(peer, 1);
                else if (peer == 0xC1)
                { if (server.RegionRequests.Count == 1) server.SilentRegionRepeaters.Add(peer); else server.SilentRegionRepeaters.Remove(peer); }
            };
            server.Start();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = await RunAsync(new MeshCoreSharp.Transport.Tcp.TcpMeshCoreTransport("127.0.0.1", server.Port),
                new(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(30)), Console.WriteLine, deadline.Token);
            if (result.Responses != (conflict ? 1 : 2) || result.RestoreConflicts != (conflict ? 1 : 0) ||
                server.ContactRoutes[0xC1] != (conflict ? 1 : 0xFF) || server.RegionRequests.Count != (conflict ? 1 : 3) ||
                server.ContactUpdates.Count != (conflict ? 1 : 2) || server.PrivateTransmissions.Count != 0 || server.ChannelSendCount != 0)
                throw new InvalidOperationException("Region probe self-test failed.");
        }
        Console.WriteLine("REGION PROBE SELF-TEST PASSED: retry then success, implicit contact, verified restore, conflict preserves new route.");
        return 0;
    }
}
