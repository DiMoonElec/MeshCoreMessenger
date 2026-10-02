# Multi-frame command: contacts

[Оглавление](../../COMPANION_PROTOCOL.md) · [Маршрутизация чтения](../../README.md)

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
