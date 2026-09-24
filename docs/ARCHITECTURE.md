# MeshCoreSharp architecture

This document records the intended runtime architecture and concurrency model. It exists so future Codex/agent sessions do not have to reconstruct the design from source history.

## Goals

MeshCoreSharp should provide a small, typed, transport-independent C# API for a MeshCore Companion while remaining one library project/assembly.

Primary goals:

- one `MeshCoreSharp.dll` for all Companion functionality;
- transport independence above `IMeshCoreTransport`;
- continuous asynchronous receive processing;
- correct coexistence of request/response traffic and unsolicited packets;
- strongly typed protocol decoding;
- explicit handling of multi-frame and deferred operations;
- predictable cancellation/timeout behavior.

## Runtime pipeline

The intended receive path is:

```text
TCP / Serial / BLE
        |
        v
IMeshCoreTransport
(logical Companion frame)
        |
        v
CompanionPacketDecoder
        |
        v
PacketRouter
   /       |        \
  /        |         \
Current   Deferred   Push / diagnostic
Command   Operations Events
Txn
```

The send path is:

```text
public MeshCoreClient API
        |
        v
CommandDispatcher
        |
    CommandGate (max 1 immediate command transaction)
        |
        v
IMeshCoreTransport.SendAsync(logical frame)
```

The RX loop is never owned by a command. It is started with the connection and runs continuously until disconnect/fault.

Public event callbacks are queued in order on a separate worker, so a consumer waiting
for a command from a push handler cannot block RX progress. Command completion does not
wait for event delivery. Disposal closes the event queue without waiting for consumer code;
already queued notifications may still be delivered.

## Layer responsibilities

### `MeshCoreSharp`

Public orchestration layer. `MeshCoreClient` owns:

- lifecycle (`ConnectAsync`, `DisconnectAsync`);
- APP_START readiness state;
- public high-level operations;
- receive loop;
- public events;
- translation from typed wire packets to application models.

It should not contain TCP framing logic or giant protocol parsing switches.

### `MeshCoreSharp.Transport`

`IMeshCoreTransport` exposes logical Companion frames.

The current transport abstraction must hide all transport-specific framing. TCP/Serial implementations frame outgoing bytes and deframe incoming stream bytes. A future BLE implementation should expose the same logical-frame contract even though BLE framing differs.

`SerialMeshCoreTransport` uses the same frame encoder/decoder as TCP, backed by
`System.IO.Ports.SerialPort` (8N1, no flow control). Each connection owns a decoder,
receive channel, cancellation source, write gate, and port. The dedicated RX thread
uses synchronous byte reads with a finite timeout; idle timeouts are normal.
Writes are serialized and bounded by the driver's write timeout. Synchronous driver
calls finish before cancellation is reported or the port is disposed, avoiding
abandoned I/O tasks and disposal under an active write. A failed write faults the
session because a partial frame may have reached the device. Session shutdown
completes RX, waits for I/O, and disposes the port; reconnect creates fresh state.
Upper layers and TCP implementation do not depend on the serial backend.

### `MeshCoreSharp.Protocol`

Pure protocol representation:

- command/packet enums;
- protocol limits;
- command encoders;
- endian-safe readers/writers;
- typed packet parsers;
- decoder and raw fallback packet.

Protocol parsing should avoid network/lifecycle concerns.

### `MeshCoreSharp.Runtime`

Internal concurrency and transaction machinery:

- `CommandDispatcher`;
- `PacketRouter`;
- transaction state machines;
- ACK tracker and future deferred Mesh trackers;
- incoming-message pump.

Keep this namespace internal unless a type is clearly required in the public API.

## Current immediate-command transaction

The existing sequence is intentionally:

```text
acquire CommandGate
       |
create transaction
       |
register transaction in PacketRouter
       |
transport.SendAsync(command)
       |
wait for matching packet / ERROR / timeout / cancellation
       |
unregister transaction
       |
release CommandGate
```

The registration-before-send ordering is a protocol requirement, not a style preference. A local Companion can respond immediately.

## Packet matching rule

A packet is a response only when the **active transaction accepts it**.
Arrival order alone never establishes ownership.

Example:

```text
GET_DEVICE_TIME ->

<- MESSAGES_WAITING
<- ADVERTISEMENT
<- ACK
<- CURRENT_TIME
```

Only `CURRENT_TIME` completes the `GET_DEVICE_TIME` transaction. The other packets must remain available for push/deferred processing.

## Multi-frame transactions

Single-packet transactions are insufficient for commands such as `GET_CONTACTS`.

The contacts transaction should behave conceptually like:

```text
State: WaitingStart
  CONTACT_START -> Receiving
  ERROR         -> Failed
  other         -> NotMine

State: Receiving
  CONTACT       -> append item; remain Receiving
  CONTACT_END   -> Completed
  ERROR         -> Failed
  push/other    -> NotMine
```

The `CommandGate` remains held for the complete multi-frame transaction.

Do not treat every packet between START and END as belonging to the transaction. Push packets may be interleaved.

## Deferred operations and concurrency classes

The Companion protocol has several distinct concurrency classes. They should not be collapsed into one generic “request task” implementation.

| Operation class | Expected concurrency |
|---|---:|
| Immediate command transaction | 1 |
| Multi-frame command transaction | 1 command slot until terminal frame |
| Outgoing text ACK wait after `MSG_SENT` | multiple may coexist; firmware has a finite ACK table |
| Remote Mesh request (login/status/telemetry/binary/path discovery) | effectively 1 shared request in current firmware |
| Trace operations with explicit tag | potentially multiple, subject to verified firmware behavior |
| Unsolicited push packets | arbitrary |

### CommandGate

Serializes the immediate Companion command/response phase. It should be released when the immediate transaction completes.

### Future MeshRequestGate

Needed for remote Mesh operations where firmware tracks one shared pending remote request. It must remain held beyond `MSG_SENT` until the final remote response or timeout.

Conceptually:

```text
acquire MeshRequestGate
  acquire CommandGate
    send remote command
    wait MSG_SENT
  release CommandGate

  wait STATUS_RESPONSE / TELEMETRY_RESPONSE / BINARY_RESPONSE / ...
release MeshRequestGate
```

Do not hold `CommandGate` during the entire radio wait; unrelated immediate local commands should remain possible when safe.

### Local channel and statistics reads

Channel reads use the existing single-packet transaction with an optional typed
predicate matching the requested slot index. Statistics use distinct typed packets
for core/radio/packet subtypes. Mismatched replies remain visible as unhandled packets.
The malformed-response type matcher fails known truncated replies without terminating
the connection. GetChannelsAsync reads DEVICE_INFO, then all slots sequentially;
other commands may run between slots. It returns no partial result on failure.

### AckTracker

`SEND_TXT_MSG` may return `MSG_SENT` with `expected_ack`. Delivery confirmation comes later as push `ACK (0x82)`.

The implemented single-packet transaction invokes an internal non-blocking hook on RX
before completing MSG_SENT. It binds the ACK tag to a pending send, so an ACK in the
next logical frame is retained even if transport.SendAsync has not returned yet.
ACK routing runs before public callbacks; it never waits for application code. This
relies on firmware's MSG_SENT-before-ACK order; unsolicited ACKs are not cached for
future commands. Duplicate active tags fail both ambiguous operations.

SendTextAsync returns on MSG_SENT with a separate Delivery task. The task returns
Confirmed/TimedOut/NotExpected; caller cancellation cancels it, connection shutdown
faults it. ACK deadlines use a bounded firmware suggestion plus margin, measured from
MSG_SENT. Cleanup unregisters the tag on every terminal path. Reconnection creates a
fresh tracker. Distinct increasing timestamps avoid deterministic collisions for the
same text sent to different contacts in the same second (ACK hashes omit recipient).

An independent private-text send gate covers admission and the immediate exchange,
not ACK waiting. The tracker keeps a window of at most eight attempted sends since
the oldest still-pending operation. Completed newer entries remain in the window:
just limiting the number of live waiters would not protect a firmware circular table
from overwriting an older waiter. Capacity waiting happens before CommandGate, so
other operations can continue. Failed attempts count conservatively until the window
advances; no automatic retransmission is performed. This assumes this client is the
only source of private-send commands on the connection.

## Incoming message pump

`MESSAGES_WAITING (0x83)` is a notification, not the message body.

Implemented high-level message delivery is:

```text
MESSAGES_WAITING
      |
      v
enqueue/drain operation through normal command dispatcher
      |
SYNC_NEXT_MESSAGE
      |
message packet or NO_MORE_MESSAGES
      |
repeat until NO_MORE_MESSAGES
```

Important constraints:

- the pump must not bypass `CommandDispatcher`;
- repeated `MESSAGES_WAITING` notifications should coalesce rather than start parallel drains;
- actual messages should be published as high-level events/models;
- low-level packet events may still be retained for diagnostics.

One pump is created per connection, gated on successful APP_START. It performs an
initial drain for the offline backlog. A bounded one-item wake channel coalesces
notifications; each query consumes notifications received before it, retaining
any tickle arriving during the final query. Every query uses CommandDispatcher
and releases CommandGate after its single message/NO_MORE_MESSAGES response.

The receive loop publishes decoded message models through the event queue even
if a response arrives after its request timed out. This avoids dropping already
dequeued firmware messages while ensuring a late message cannot complete an
unrelated command. Delivery is once per received frame, not durable or deduplicated.
Malformed known message responses fail the matching transaction and surface RX
diagnostics. Other command failures are reported by the pump. The current drain
stops on failure and waits for a later notification. Disconnect/fault cancels the
pump, including queries still waiting for CommandGate; explicit reconnection
creates a fresh pump without retained notifications.

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
