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

The implemented Serial transport uses this same framing and handles arbitrary read
boundaries. Defaults are 115200 baud, 8N1, no flow control, DTR on, RTS on, with a
200 ms opening delay. `System.IO.Ports` applies DTR and then RTS after opening; on
ESP32 boards, requesting false/false can pass through a reset-producing intermediate
state. The default true/true pair is a non-reset state for the standard two-transistor
auto-reset circuit. Speed, DTR/RTS and delay remain configurable for boards with
different wiring. See Espressif's [automatic bootloader documentation](https://docs.espressif.com/projects/esptool/en/latest/esp32/advanced-topics/boot-mode-selection.html)
and the [.NET Unix SerialStream initialization](https://github.com/dotnet/runtime/blob/main/src/libraries/System.IO.Ports/src/System/IO/Ports/SerialStream.Unix.cs).

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

## Local channel reads, writes and statistics

Verified against firmware handlers `CMD_GET_CHANNEL`, `CMD_GET_STATS` in
[MyMesh.cpp](https://github.com/meshcore-dev/MeshCore/blob/main/examples/companion_radio/MyMesh.cpp)
and slot lookup in [BaseChatMesh.cpp](https://github.com/meshcore-dev/MeshCore/blob/main/src/helpers/BaseChatMesh.cpp).

`GET_CHANNEL (31)` takes one index byte. `CHANNEL_INFO (0x12)` contains index (1),
NUL-terminated/padded UTF-8 name (32), shared secret (16): 50 bytes total.
Names retain whitespace. Raw frames contain key material. An empty slot still
returns CHANNEL_INFO; an out-of-range index returns NOT_FOUND. Enumeration reads
all slots up to DEVICE_INFO.MaxChannels, including holes, without assuming a fixed
capacity or probing until an error. Legacy devices without capacity require explicit
single-slot reads. Each request holds CommandGate only until its own response;
enumeration is not an atomic snapshot. Responses must match the requested index.

`SET_CHANNEL (32)` is exactly 50 bytes: command (1), slot index (1), UTF-8 name
in a 32-byte NUL-padded field, then a 16-byte secret. Current firmware copies the
name into a 32-byte C string, so the library accepts at most 31 UTF-8 bytes and
rejects invalid UTF-8/NUL rather than allowing silent truncation. The response is
`OK/ERROR`. Clearing a slot uses the same command with an empty name and 16 zero bytes.

The standard hashtag convention derives the secret as the first 16 bytes of
SHA-256 over the exact UTF-8 channel name including `#`. It is case- and
whitespace-sensitive; no Unicode normalization is applied. For example, `#test`
maps to `9cd8fcf22a47333b591d96a2b848b73f`. This only separates topic traffic:
anyone who guesses the name can derive the key. The default `Public` channel uses
the firmware's well-known nonzero key; an all-zero key with an empty name means an
empty/deleted slot despite a contradictory older paragraph in upstream documentation.

`GET_STATS (56)` takes subtype 0/1/2 (protocol v8+). `STATS (0x18)` echoes subtype:

| Subtype | Fields after type and subtype | Minimum total bytes |
| --- | --- | --- |
| Core (0) | battery u16 mV, uptime u32 seconds, error flags u16, outbound queue u8 | 11 |
| Radio (1) | noise i16 dBm, last RSSI i8 dBm, last SNR i8 / 4 dB, TX/RX airtime u32 seconds each | 14 |
| Packets (2) | RX, TX, TX flood, TX direct, RX flood, RX direct, RX errors: seven u32 counters | 30 |

Multi-byte values are little-endian. Typed packet classes distinguish the three
responses so a different subtype cannot complete the active request. Unknown
subtypes preserve the raw frame. Known truncated responses raise protocol errors;
malformed CHANNEL_INFO/STATS fail an active operation expecting that outer packet
type. Trailing extension bytes are accepted. No configuration writes or RF sends
are part of these operations.

## Advertisements

Verified against `CMD_SEND_SELF_ADVERT` and `onDiscoveredContact` in
[MyMesh.cpp](https://github.com/meshcore-dev/MeshCore/blob/main/examples/companion_radio/MyMesh.cpp).

`SEND_SELF_ADVERT (7)` takes mode byte 0 (zero hop) or 1 (flood); the library sends
the explicit mode. Firmware uses its existing name, location policy and default
scope. Response is local OK/ERROR, with no delivery confirmation. No retry is safe
to infer from a timeout. Queued sends are cancelled with the connection.

`ADVERT (0x80)` is type + 32-byte public key (33 bytes total).
`NEW_ADVERT (0x8A)` has the same body as CONTACT (148 bytes total), parsed by the
shared contact-body reader but represented as a separate AdvertisementPacket.
It cannot be consumed as a GET_CONTACTS response. Known truncated pushes raise
diagnostics without failing unrelated commands; extension bytes are retained.
Neither push is an acknowledgement of sending our own advert.

High-level AdvertisementReceived is dispatched through the usual event queue,
without automatic contact lookups or configuration writes. A new discovery does
not necessarily mean the firmware saved the contact (manual-add policy applies).

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

### Implemented wire layout

Verified against upstream firmware on 2026-09-24:
[MyMesh.cpp](https://github.com/meshcore-dev/MeshCore/blob/main/examples/companion_radio/MyMesh.cpp)
(`writeContactRespFrame`, `CMD_GET_CONTACTS`, `checkSerialInterface`) and
[ContactInfo.h](https://github.com/meshcore-dev/MeshCore/blob/main/src/helpers/ContactInfo.h).

`GET_CONTACTS` sends command byte `0x04` without the optional `since` filter.
`CONTACT_START` is type `0x02` followed by a UInt32 LE total count.
`CONTACT_END` is type `0x04` followed by a UInt32 LE most-recent modification timestamp.
The start count describes the device's table, not a guaranteed number of stream frames;
the firmware filters contacts by `lastmod > since` and may change its table during iteration.
Completion therefore requires END, including for an empty list.

`CONTACT` is 148 bytes in the verified firmware:

| Offset | Field |
|---:|---|
| 0 | Type `0x03` |
| 1–32 | Public key |
| 33 | Advertisement type |
| 34 | Raw flags |
| 35 | Encoded outgoing path length (`0xFF` = unknown) |
| 36–99 | 64-byte outgoing path storage |
| 100–131 | 32-byte UTF-8 name, NUL terminated/padded |
| 132–135 | Last advert timestamp, UInt32 LE, remote clock |
| 136–139 | Latitude, Int32 LE / 1e6 |
| 140–143 | Longitude, Int32 LE / 1e6 |
| 144–147 | Last modification timestamp, UInt32 LE, Companion clock |

The implementation requires these fields and accepts trailing extension bytes.
It preserves the encoded path byte and all path storage without assuming one byte per hop.
Malformed contact frames fail the active contacts operation and raise diagnostics, while
the connection and framing remain usable. Unknown packet types still use the raw fallback.

Cancellation/timeout releases the local command slot; it does not stop the firmware iterator.
Late stream frames remain visible as unhandled packets. A new contacts request may receive
`BAD_STATE` until the firmware finishes its previous stream. No automatic retry is performed.

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

High-level `MessageReceived` is emitted after decoding the actual returned message packet, not merely on `MESSAGES_WAITING`.

A message pump must use the same serialized command dispatcher as normal calls; it must not write commands directly to the transport in parallel.

Implemented layouts were checked against
[MyMesh.cpp](https://github.com/meshcore-dev/MeshCore/blob/main/examples/companion_radio/MyMesh.cpp)
(`queueMessage`, `onChannelMessageRecv`, `onChannelDataRecv`, `CMD_SYNC_NEXT_MESSAGE`)
and [TxtDataHelpers.h](https://github.com/meshcore-dev/MeshCore/blob/main/src/helpers/TxtDataHelpers.h).

| Packet | Fields after the type byte |
|---|---|
| Contact legacy `0x07` | contact key prefix (6), path (1), text type (1), timestamp (4 LE), body |
| Channel legacy `0x08` | channel (1), path (1), text type (1), timestamp (4 LE), UTF-8 text |
| Contact V3 `0x10` | signed SNR (1, divide by 4), reserved (2), then legacy contact fields |
| Channel V3 `0x11` | signed SNR (1, divide by 4), reserved (2), then legacy channel fields |
| Channel data `0x1B` | signed SNR (1, divide by 4), reserved (2), channel (1), path (1), data type (2 LE), length (1), bytes |
| No more messages `0x0A` | no payload required |

Text types are plain (0), CLI data (1), and signed plain (2). Signed contact bodies
begin with a four-byte author prefix before UTF-8 text. Unknown text-type values
are preserved. Path bytes remain encoded; `0xFF` indicates direct routing.
The parser retains whitespace, allows empty text/data, rejects incomplete headers
and binary lengths exceeding the available payload, and preserves raw frames.
Channel data has no sender timestamp. Channel text includes the original sender-name
prefix; it is not split heuristically. V3 reserved bytes are skipped without requiring zero.

Reading a queue item removes it on the firmware side before the application has
persisted it. The client does not provide durable/exactly-once delivery or retry
timed-out reads automatically. Packet/model events remain available for late replies.

`DrainMessagesAsync` explicitly runs this sequence to `NO_MORE_MESSAGES`, also when
automatic reception is disabled. Concurrent calls share a single drain; canceling
one caller only cancels its wait. Recoverable command/protocol errors may be retried
explicitly. After a `SYNC_NEXT_MESSAGE` timeout the connection must be restarted
before another drain, because a late response has no request ID and could be mistaken
for the result of a retry.

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

Implemented plain-text wire format, verified against
[MyMesh.cpp](https://github.com/meshcore-dev/MeshCore/blob/main/examples/companion_radio/MyMesh.cpp)
and [BaseChatMesh.cpp](https://github.com/meshcore-dev/MeshCore/blob/main/src/helpers/BaseChatMesh.cpp):

- `SEND_TXT_MSG`: `02 | type=0 | attempt=0 | timestamp u32 LE | key prefix (6) | UTF-8 text`.
- `MSG_SENT`: `06 | flood (0/1) | expected_ack u32 LE | suggested_timeout_ms u32 LE` (10 bytes).
- `ACK`: `82 | expected_ack u32 LE | round_trip_ms u32 LE` (9 bytes).
- `SEND_CHANNEL_TXT_MSG`: `03 | type=0 | channel u8 | timestamp u32 LE | UTF-8 text`.
  Response is `OK/ERROR`, with no delivery ACK.

Private text is limited to 160 UTF-8 bytes. Channel payload additionally includes
the firmware-generated `sender_name + ": "` prefix, which shares that limit. Library
validation rejects oversize, empty, NUL-containing or invalid UTF-16 input rather
than relying on firmware truncation. Commands contain no trailing NUL. Received
MSG_SENT/ACK require their full minimum layouts and tolerate extension bytes.

The ACK digest is a four-byte SHA-256 prefix over timestamp/text/attempt and sender
public key, with no recipient in the digest. Client-generated monotonically increasing
timestamps prevent same-second identical-text collisions within the client instance.
It does not compute the expected tag locally; the authoritative value comes from
MSG_SENT. The firmware only registers nonzero ACKs. Its table is circular, not a pool
of reusable free slots; the client admission window respects that distinction.

Do not keep the global immediate `CommandGate` locked while waiting for a remote text ACK.

Physical validation on 2026-09-25 used one direct message from Heltec V3 to a
contact selected by its unique six-byte prefix. Firmware returned MSG_SENT with
tag `0x8E451AF8` and suggested timeout 14178 ms; the matching ACK arrived with
reported RTT 2775 ms. Packet counters changed TX/direct 0 → 1 and RX 0 → 1.
See [hardware report](testing/serial-private-send-2026-09-25.md).

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
