# Incoming message pump

[Оглавление](../../ARCHITECTURE.md) · [Маршрутизация чтения](../../README.md)

## Incoming message pump

`MESSAGES_WAITING (0x83)` is a notification, not the message body.

Implemented high-level message delivery is:

```text
MESSAGES_WAITING
      |
      v
enqueue/drain operation through normal command dispatcher
      |
SYNC_NEXT_MESSAGE
      |
message packet or NO_MORE_MESSAGES
      |
repeat until NO_MORE_MESSAGES
```

Important constraints:

- the pump must not bypass `CommandDispatcher`;
- repeated `MESSAGES_WAITING` notifications should coalesce rather than start parallel drains;
- actual messages should be published as high-level events/models;
- low-level packet events may still be retained for diagnostics.

One pump is created per connection, gated on successful APP_START. With automatic
reception enabled it performs an initial drain for the offline backlog. A bounded one-item wake channel coalesces
notifications; each query consumes notifications received before it, retaining
any tickle arriving during the final query. Every query uses CommandDispatcher
and releases CommandGate after its single message/NO_MORE_MESSAGES response.

`DrainMessagesAsync` exposes the same pump for an explicit drain, including when
automatic reception is disabled. Concurrent callers share the active drain and
complete when it reaches `NO_MORE_MESSAGES`; canceling one waiter does not cancel
the shared protocol work. Disconnect cancels all remaining waiters.

The receive loop publishes decoded message models through the event queue even
if a response arrives after its request timed out. This avoids dropping already
dequeued firmware messages while ensuring a late message cannot complete an
unrelated command. Delivery is once per received frame, not durable or deduplicated.
Malformed known message responses fail the matching transaction and surface RX
diagnostics. Other command failures are reported by the pump. The current drain
stops on failure and waits for a later notification or explicit drain. A timed-out
`SYNC_NEXT_MESSAGE` makes the pump unusable until reconnect: without correlation IDs,
a late response could otherwise complete a retry incorrectly. Disconnect/fault cancels the
pump, including queries still waiting for CommandGate; explicit reconnection
creates a fresh pump without retained notifications.
