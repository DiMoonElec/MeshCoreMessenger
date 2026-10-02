# Incoming messages: `MESSAGES_WAITING`

[Оглавление](../../COMPANION_PROTOCOL.md) · [Маршрутизация чтения](../../README.md)

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
