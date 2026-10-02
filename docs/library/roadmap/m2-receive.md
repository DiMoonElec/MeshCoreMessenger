# M2 — Incoming messages and message pump

[Оглавление](../../ROADMAP.md) · [Маршрутизация чтения](../../README.md)

## M2 — Incoming messages and message pump

Implemented (automated regression tests and local TCP self-test). Real-device private
message reception verified on Heltec V3 after a Flood advertisement: the message
was delivered through MessageReceived on 2026-09-24. See [report](../../testing/serial-advert-2026-09-24.md).

Packet parsers for:

- legacy private/contact message;
- legacy channel message;
- V3 private/contact message;
- V3 channel message;
- channel data;
- `NO_MORE_MESSAGES`.

Coalescing internal `MessagePump`:

```text
MESSAGES_WAITING
  -> schedule drain
  -> SYNC_NEXT_MESSAGE repeatedly
  -> publish decoded message(s)
  -> stop on NO_MORE_MESSAGES
```

Public `MessageReceived` delivers `ContactMessage`, `ChannelMessage`, or `ChannelDataMessage`,
while low-level packet diagnostics remain available. The pump starts after successful APP_START,
reads the offline queue once, and responds to subsequent MESSAGES_WAITING notifications.
It stops with the connection and reports errors without automatic retry loops.
`AutoReceiveMessages` can disable automatic requests. Public `DrainMessagesAsync`
runs an explicit drain through the same pump, coalesces concurrent callers, isolates
caller cancellation and reports failures. A timed-out sync requires reconnect because
a late uncorrelated response cannot be assigned safely to a retry.
