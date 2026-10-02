# Runtime pipeline

[Оглавление](../../ARCHITECTURE.md) · [Маршрутизация чтения](../../README.md)

## Runtime pipeline

The intended receive path is:

```text
TCP / Serial / BLE
        |
        v
IMeshCoreTransport
(logical Companion frame)
        |
        v
CompanionPacketDecoder
        |
        v
PacketRouter
   /       |        \
  /        |         \
Current   Deferred   Push / diagnostic
Command   Operations Events
Txn
```

The send path is:

```text
public MeshCoreClient API
        |
        v
CommandDispatcher
        |
    CommandGate (max 1 immediate command transaction)
        |
        v
IMeshCoreTransport.SendAsync(logical frame)
```

The RX loop is never owned by a command. It is started with the connection and runs continuously until disconnect/fault.

Public event callbacks are queued in order on a separate worker, so a consumer waiting
for a command from a push handler cannot block RX progress. Command completion does not
wait for event delivery. Disposal closes the event queue without waiting for consumer code;
already queued notifications may still be delivered.
