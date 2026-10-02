# Deferred operations and concurrency classes

[Оглавление](../../ARCHITECTURE.md) · [Маршрутизация чтения](../../README.md)

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

### Local channel configuration and statistics

Channel reads use the existing single-packet transaction with an optional typed
predicate matching the requested slot index. Statistics use distinct typed packets
for core/radio/packet subtypes. Mismatched replies remain visible as unhandled packets.
The malformed-response type matcher fails known truncated replies without terminating
the connection. GetChannelsAsync reads DEVICE_INFO, then all slots sequentially;
other commands may run between slots. It returns no partial result on failure.
SetChannelAsync, SetHashtagChannelAsync and ClearChannelAsync use the same single-flight
dispatcher and complete only on OK/ERROR. Hashtag derivation is a pure public helper;
wire encoding, fixed-size padding and secrets remain below the client API. Configuration
does not trigger an implicit readback, retry or radio transmission.

### Contact mutation

AddOrUpdateContactAsync and RemoveContactAsync are ordinary single-flight local
commands waiting for OK/ERROR. ContactConfiguration is the public editable snapshot;
overloads convert decoded Contact and NEW_ADVERT data before command dispatch. The
encoder copies all key/path bytes before its first wait, validates the packed path
descriptor and fixed fields, and deliberately omits optional lastmod so firmware
uses its own clock. Mutations do not update a client-side cache and do not perform
an implicit GET_CONTACTS; applications refresh explicitly when needed.

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
The public overload taking `Contact` forwards its complete key to the same encoder;
the firmware command itself carries the protocol-defined six-byte prefix.

An independent private-text send gate covers admission and the immediate exchange,
not ACK waiting. The tracker keeps a window of at most eight attempted sends since
the oldest still-pending operation. Completed newer entries remain in the window:
just limiting the number of live waiters would not protect a firmware circular table
from overwriting an older waiter. Capacity waiting happens before CommandGate, so
other operations can continue. Failed attempts count conservatively until the window
advances; no automatic retransmission is performed. This assumes this client is the
only source of private-send commands on the connection.
