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

## Region API (implemented)

All operations below belong to the same MeshCoreSharp assembly and accept a
CancellationToken. Applications do not need raw Companion frames.

| API | Contract |
| --- | --- |
| GetDefaultFloodScopeAsync | Reads the node's actual name/key pair; Unknown differs from KnownEmpty |
| SetDefaultFloodScopeAsync / ClearDefaultFloodScopeAsync | Explicit persistent node configuration; application services add readback and local snapshot persistence |
| SendChannelTextAsync(..., FloodScopeSelection, ...) | One send with InheritDefault, ForceUnscoped or ExplicitRegion; temporary scope cleanup follows acceptance |
| SendTextAsync(..., FloodScopeSelection, ...) | Library-level scope selection for a private send; Messenger normally uses the node default, without per-contact region settings |
| GetTextBudget | Evaluates current UTF-8 limits for recipient kind, transport and scope without I/O; send revalidates after waiting for gates |
| DiscoverRepeaterNeighborsAsync | Full-key zero-hop repeater discovery, collected by tag for the caller's bounded window; no contact mutations or automatic retries |
| RequestRegionsAsync | Full repeater key plus an explicit reply path; caller must ensure a suitable direct outgoing contact route; final response correlated to MSG_SENT tag |

An explicit responseTimeout overload controls the final region response wait.
The default overload uses the firmware's suggested wait with the library's bounds.
A timeout is not an empty region list. The result contains names, remote clock
and tag; private keys, pagination and completeness guarantees are absent.
Firmware may create an unknown target as a zero-hop contact (reference protocol13+).
The library itself does not prepare or restore contact routes and does not retry.

An accepted scoped send can return ScopeCleanupError. That does not retract the
accepted transmission and must not trigger an automatic resend. Channel acceptance
does not mean remote delivery. The separate Messenger Core owns durable policy/
attempt captures, catalogue storage, route preparation, bounded discovery retries
and UI progress; these responsibilities are not added to the library.

See [wire and firmware limits](../protocol/regions.md),
[Messenger user guide](../../messenger/regions-user-guide.md) and
[accepted scope / deferred hardware checks](../../testing/region-user-acceptance.md).

## Error handling principles

- malformed logical packets should result in protocol diagnostics, not framing desynchronization;
- unknown packet types should be represented by a raw packet when possible;
- connection loss must fail the active command transaction;
- cancellation must unregister pending transactions and release gates;
- error response packets should surface as command exceptions, while preserving raw error codes.

## Future project additions

GUI/CLI applications belong as separate solution projects referencing `MeshCoreSharp`. They should not absorb protocol logic from the library.
