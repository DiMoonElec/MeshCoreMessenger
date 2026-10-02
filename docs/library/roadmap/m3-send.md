# M3 — Outgoing text and ACK tracking

[Оглавление](../../ROADMAP.md) · [Маршрутизация чтения](../../README.md)

## M3 — Outgoing text and ACK tracking

Implemented (82 regression tests in total and TCP self-test pass):

- `SEND_TXT_MSG` encoder;
- `MSG_SENT` parser;
- `ACK` parser;
- `AckTracker` keyed by expected ACK;
- `SendTextAsync` returns immediate acceptance plus independent `Delivery` task;
- overloads accept either a typed `Contact` or a full 32-byte public key;
- timeout/cancellation behavior for ACK waits;
- race handling for an ACK that arrives very quickly after `MSG_SENT`.

CommandGate is released before the delivery wait. Registration happens synchronously
on RX when MSG_SENT is accepted, before processing a following ACK. A per-connection
eight-send window protects the firmware's circular table, including when newer sends
complete before an older one. No automatic retransmission. Text length is checked in
UTF-8 bytes; channel length accounts for the sender-name prefix.

Channel text (`SendChannelTextAsync`, OK/ERROR) is also implemented and physically
tested on Heltec V3: one message in #test, TX/flood counters 0 -> 1. Private text/ACK
was then verified against contact `RnD Mesh01`: a single direct send, matching ACK,
RTT 2775 ms, TX/direct 0 -> 1. See [report](../../testing/serial-private-send-2026-09-25.md).
