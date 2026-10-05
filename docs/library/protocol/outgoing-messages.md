# `SEND_TXT_MSG`: immediate send result vs delivery ACK

[Оглавление](../../COMPANION_PROTOCOL.md) · [Маршрутизация чтения](../../README.md)

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

The ACK digest is a four-byte SHA-256 prefix over timestamp/text/(attempt & 3) and sender
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
See [hardware report](../../testing/serial-private-send-2026-09-25.md).

## Explicit private send API — P1 implemented

The ordinary SendTextAsync overload still sends once with attempt 0 and an automatic
timestamp. Additional overloads accept a full recipient key or Contact, text, an explicit
UInt32 timestamp, a byte attempt, and CancellationToken. Both timestamp and attempt
are required, avoiding ambiguity with existing cancellation-token calls.

Explicit sends accept basic attempts 0–3 with the unchanged 160-byte UTF-8 budget;
extended attempts are rejected before TX. They preserve the supplied timestamp
even when it is older than the client's timestamp floor, and advance that floor
when newer. A later automatic private or channel send gets a newer timestamp.
No retry, route mutation or durable storage is performed by these overloads.
MSG_SENT/ACK tracking, subscribe-before-send, cancellation and ACK admission are
shared with the ordinary send. [P1 verification](../../testing/private-retry-api.md).

## Application private retry cycle — planned

For packet-hash
inputs, extended attempt, repeated ACK tags and the 158-byte limit see
[private retry research](private-retry-hashes.md). The application retry policy
3 flood / 3 known + 2 flood has [completed P1; execution is not yet implemented](../../messenger/plan/d7-private-auto-retry.md).
The final plan starts a new timestamp/attempt 0 after resetting the known route;
within each phase it supports preserved timestamp/incremented attempt or a new
timestamp/attempt 0 per send. Extended attempt 4 is not needed; the policy's text budget
remains 160 bytes. A new timestamp can create another received message if the
original reached the peer but its ACK was lost.
