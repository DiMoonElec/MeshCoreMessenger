# Current immediate-command transaction

[Оглавление](../../ARCHITECTURE.md) · [Маршрутизация чтения](../../README.md)

## Current immediate-command transaction

The existing sequence is intentionally:

```text
acquire CommandGate
       |
create transaction
       |
register transaction in PacketRouter
       |
transport.SendAsync(command)
       |
wait for matching packet / ERROR / timeout / cancellation
       |
unregister transaction
       |
release CommandGate
```

The registration-before-send ordering is a protocol requirement, not a style preference. A local Companion can respond immediately.
