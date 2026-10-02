# Multi-frame transactions

[Оглавление](../../ARCHITECTURE.md) · [Маршрутизация чтения](../../README.md)

## Multi-frame transactions

Single-packet transactions are insufficient for commands such as `GET_CONTACTS`.

The contacts transaction should behave conceptually like:

```text
State: WaitingStart
  CONTACT_START -> Receiving
  ERROR         -> Failed
  other         -> NotMine

State: Receiving
  CONTACT       -> append item; remain Receiving
  CONTACT_END   -> Completed
  ERROR         -> Failed
  push/other    -> NotMine
```

The `CommandGate` remains held for the complete multi-frame transaction.

Do not treat every packet between START and END as belonging to the transaction. Push packets may be interleaved.
