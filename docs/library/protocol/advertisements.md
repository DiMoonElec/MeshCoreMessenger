# Advertisements

[Оглавление](../../COMPANION_PROTOCOL.md) · [Маршрутизация чтения](../../README.md)

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
