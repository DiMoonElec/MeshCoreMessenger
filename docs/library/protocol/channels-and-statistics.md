# Local channel reads, writes and statistics

[Оглавление](../../COMPANION_PROTOCOL.md) · [Маршрутизация чтения](../../README.md)

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
