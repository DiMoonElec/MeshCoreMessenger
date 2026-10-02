# Timeout model

[Оглавление](../../ARCHITECTURE.md) · [Маршрутизация чтения](../../README.md)

## Timeout model

Do not use one timeout policy for every operation.

Suggested categories:

- **Immediate command timeout**: current default 5 seconds, configurable.
- **Stream inactivity timeout**: for contacts and similar streams, reset after each accepted frame; optionally combine with a larger absolute maximum.
- **Deferred Mesh timeout**: use the `MSG_SENT.suggested_timeout` returned by firmware, plus a configurable safety margin/minimum.
- **Terminal command**: reboot/reset may complete by disconnect/reboot rather than a normal response packet.

Timeout values are client policy, not part of the transport framing.

## Public API direction

Keep callers on high-level APIs, for example:

```csharp
await client.ConnectAsync(ct);
var self = await client.StartAsync(ct);
var contacts = await client.GetContactsAsync(ct);
```

Users should not need to construct `PacketRouter`, transactions, raw command bytes, or framing codecs.

Low-level packet access may remain exposed through diagnostic events where useful, but must not be required for normal usage.

## Error handling principles

- malformed logical packets should result in protocol diagnostics, not framing desynchronization;
- unknown packet types should be represented by a raw packet when possible;
- connection loss must fail the active command transaction;
- cancellation must unregister pending transactions and release gates;
- error response packets should surface as command exceptions, while preserving raw error codes.

## Future project additions

GUI/CLI applications belong as separate solution projects referencing `MeshCoreSharp`. They should not absorb protocol logic from the library.
