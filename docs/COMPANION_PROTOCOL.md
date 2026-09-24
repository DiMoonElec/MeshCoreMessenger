# MeshCore Companion protocol notes for MeshCoreSharp

This is an implementation-oriented companion to the upstream MeshCore documentation. It records protocol behavior already analyzed/tested for this project, including important concurrency implications and known source-version differences.

It is **not** intended to replace the upstream firmware source or protocol specification. When a behavior is uncertain, verify it against the targeted firmware revision.

## Source-of-truth priority

When sources disagree, use this order:

1. current MeshCore Companion firmware behavior/source;
2. current upstream `docs/companion_protocol.md`;
3. `meshcore_py` source/tests as a reference implementation/regression corpus.

Do not assume a Python enum/helper is supported by every firmware build.

## Logical Companion frame

At the protocol layer, a Companion frame is:

```text
+------------+-----------------------------+
| Type byte  | packet-specific payload ... |
+------------+-----------------------------+
```

For a command, the first byte is a command code. For received data, the first byte is a packet/response/push type.

Most multi-byte integer fields are little-endian. Individual payload formats may define exceptions; parse according to that packet's specification.

There is no universal request ID/correlation ID in every command/response.

## TCP/Serial stream framing

For the stream transports used by current `meshcore_py` TCP/Serial implementations:

### App -> Companion

```text
0x3C | length UInt16 LE | logical Companion payload
 '<'
```

### Companion -> App

```text
0x3E | length UInt16 LE | logical Companion payload
 '>'
```

The length counts only the logical Companion payload, not the 3-byte stream header.

Example logical command:

```text
05
```

TCP/Serial wire representation:

```text
3C 01 00 05
```

Example logical response:

```text
09 78 56 34 12
```

wire representation:

```text
3E 05 00 09 78 56 34 12
```

The stream framing has no separate CRC/checksum field.

TCP is a byte stream: a read may contain half a frame, exactly one frame, multiple frames, or the end of one frame plus part of another.

## BLE framing

BLE should be modeled as delivering logical Companion frames without the `0x3C/0x3E + UInt16` stream wrapper. Keep BLE-specific details inside the transport implementation.

## Frame size

Current baseline firmware examined for this project uses a Companion frame limit of 176 bytes (`MAX_FRAME_SIZE`).

Therefore for TCP/Serial:

```text
logical max: 176 bytes
wire max:    179 bytes (3-byte framing header + logical frame)
```

The UInt16 framing length is wider than the actual current firmware payload limit; do not infer a 65535-byte supported frame from the header type.

The current implementation may keep a somewhat larger defensive RX safety ceiling to avoid immediate incompatibility with future firmware, but outgoing commands should respect the targeted firmware's actual limit.

Minimum useful logical Companion frame size is one byte (for example `OK` or `MESSAGES_WAITING`).

## Asynchronous packets

The Companion can push packets without a request and can push them while a command transaction is active.

Current push range used by the baseline protocol includes:

| Code | Meaning |
|---:|---|
| `0x80` | Advertisement |
| `0x81` | Path updated |
| `0x82` | Send confirmed / ACK |
| `0x83` | Messages waiting |
| `0x84` | Raw data |
| `0x85` | Login success |
| `0x86` | Login fail |
| `0x87` | Status response |
| `0x88` | RX/log data |
| `0x89` | Trace data |
| `0x8A` | New advertisement |
| `0x8B` | Telemetry response |
| `0x8C` | Binary response |
| `0x8D` | Path discovery response |
| `0x8E` | Control data |
| `0x8F` | Contact deleted |
| `0x90` | Contacts full |

Transport-wise these are push packets. Semantically some are pure unsolicited events while others are final completions of a previously initiated Mesh operation.

Correct client assumption:

```text
send command
<- unrelated push
<- unrelated push
<- expected response
```

is valid.

## Immediate request/response commands

Normal local commands are effectively single-flight because response packets often have no request ID. The client should serialize them through one command queue/gate.

Typical pattern:

```text
register response matcher
send command
wait for matching packet / ERROR / timeout
unregister matcher
```

Do not send first and subscribe second.

Examples already implemented in MeshCoreSharp:

- `APP_START -> SELF_INFO`
- `GET_DEVICE_TIME -> CURRENT_TIME`
- `SET_DEVICE_TIME -> OK/ERROR`
- `DEVICE_QUERY -> DEVICE_INFO`
- `GET_BATT_AND_STORAGE -> BATT_AND_STORAGE`

## Multi-frame command: contacts

`GET_CONTACTS` is a stream transaction:

```text
GET_CONTACTS ->
<- CONTACT_START
<- CONTACT
<- CONTACT
<- ...
<- CONTACT_END
```

Unrelated push packets can appear between stream frames:

```text
<- CONTACT_START
<- CONTACT
<- ADVERTISEMENT
<- CONTACT
<- MESSAGES_WAITING
<- CONTACT
<- CONTACT_END
```

Only contact stream packets belong to the transaction. The command slot remains occupied until `CONTACT_END` or failure/timeout.

For timeout handling, prefer stream inactivity timeout semantics rather than a small fixed wall-clock timeout for the entire contact list.

## Incoming messages: `MESSAGES_WAITING`

`MESSAGES_WAITING (0x83)` is a notification/tickle, not an actual chat message.

The Companion queues incoming frames internally. The client drains them using `SYNC_NEXT_MESSAGE` repeatedly:

```text
<- MESSAGES_WAITING

SYNC_NEXT_MESSAGE ->
<- CONTACT_MSG_RECV / CHANNEL_MSG_RECV / V3 variants / CHANNEL_DATA_RECV / NO_MORE_MESSAGES

SYNC_NEXT_MESSAGE ->
<- ...

SYNC_NEXT_MESSAGE ->
<- NO_MORE_MESSAGES
```

High-level `MessageReceived` should eventually be emitted after decoding the actual returned message packet, not merely on `MESSAGES_WAITING`.

A message pump must use the same serialized command dispatcher as normal calls; it must not write commands directly to the transport in parallel.

## `SEND_TXT_MSG`: immediate send result vs delivery ACK

Private/direct text sending is two-phase:

```text
SEND_TXT_MSG ->
<- MSG_SENT

... radio / Mesh delay ...

<- ACK (0x82)
```

`MSG_SENT` means the Companion accepted/started radio transmission. It is not proof of remote delivery.

The `MSG_SENT` payload contains fields including an expected ACK/tag and a suggested timeout. The later `ACK` is an asynchronous push and should be tracked separately from the immediate command transaction.

The current firmware maintains a finite table of pending text ACKs (previous analysis found 8 entries in the examined baseline firmware). Therefore multiple ACK waits may coexist after their immediate commands have completed, but the table is not unlimited.

Do not keep the global immediate `CommandGate` locked while waiting for a remote text ACK.

## Remote Mesh requests and the future `MeshRequestGate`

Several remote operations follow:

```text
remote command ->
<- MSG_SENT

... Mesh network ...

<- final push response
```

Examples include login, status, telemetry, binary request, and path discovery.

Important current-firmware constraint: these operations share firmware pending-request state. Starting another such request can call/behave like `clearPendingReqs()` and overwrite correlation state for the previous one.

Therefore MeshCoreSharp should eventually add one shared `MeshRequestGate` spanning the whole remote operation, not merely the immediate `MSG_SENT` phase.

Immediate `CommandGate` and remote `MeshRequestGate` serve different purposes.

### Login

Pattern:

```text
SEND_LOGIN -> MSG_SENT
later -> LOGIN_SUCCESS or LOGIN_FAIL
```

Both success and failure are terminal completions and must finish the high-level login operation. Do not wait only for success and let failure become a timeout.

### Legacy status request

Status correlation in baseline firmware is effectively single-flight and has legacy matching behavior. The final `STATUS_RESPONSE` does not provide a universal client-visible request ID suitable for concurrent status requests.

### Telemetry

Remote telemetry may use internal firmware tagging, but final client-visible response formats do not make arbitrary parallel client requests safe. Keep it under the shared remote-request gate unless a targeted firmware revision is proven to support more.

### Binary request

Binary request is better designed for correlation: the final `BINARY_RESPONSE` includes a tag that can be matched to the tag from `MSG_SENT`.

Even so, examined current firmware still keeps only one shared pending remote request, so do not infer that multiple simultaneous binary requests are safe merely because the packet contains a tag.

### Path discovery

Immediate phase can contain a tag, but the final path-discovery response does not expose a universal request tag suitable for arbitrary parallel requests. Treat it as single-flight under the shared remote gate.

### Trace

Trace operations are distinct because application-provided tags can be present in returned trace data. They may support a different concurrency tracker. Verify the exact targeted firmware behavior before enabling concurrency.

## Terminal commands

Some commands are not normal request/response transactions.

### Reboot

A reboot may not return a useful response before the device resets/disconnects. Treat successful command write followed by disconnect/reboot as expected lifecycle behavior rather than requiring `OK`.

### Factory reset

In the baseline firmware examined for this project, factory reset command `51` expects the literal ASCII payload `reset` after the command byte. Firmware may disconnect/reset before a reliable `OK` reaches the application.

Do not model this as a normal `command -> OK` transaction without verifying the target firmware.

## Known source-version differences

Previously identified differences/gaps to keep in mind:

- Current firmware included command `62` (`SEND_CHANNEL_DATA`) while some `meshcore_py` revisions omitted it from `CommandType`.
- Some Python revisions expose command `66` (`RUN_CLI_COMMAND`) and packet `CLI_REPLY`, while the examined baseline firmware command table stopped at `65` and treated unknown commands as unsupported.
- Current firmware `HAS_CONNECTION` expects a public key argument; do not copy a helper that sends only command `28`.
- `IMPORT_PRIVATE_KEY` can return `DISABLED` when the feature is compile-time disabled.
- Login high-level completion must accept both success and failure.
- `SYNC_NEXT_MESSAGE` can return `CHANNEL_DATA_RECV` in addition to text-message variants and `NO_MORE_MESSAGES`.
- Documentation and firmware have differed for `SEND_CHANNEL_TXT_MSG`; the examined firmware behavior was `OK/ERROR`, so prefer actual target-firmware behavior.

## Command/packet enums in current repository

`src/MeshCoreSharp/Protocol/CommandType.cs` and `PacketType.cs` intentionally contain more of the known protocol surface than is currently implemented by public APIs.

Presence in an enum does **not** mean the operation is fully implemented or proven against the current target firmware. Add command encoders, packet parsers, transactions, and tests before exposing a high-level API.
