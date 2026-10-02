# Packet matching rule

[Оглавление](../../ARCHITECTURE.md) · [Маршрутизация чтения](../../README.md)

## Packet matching rule

A packet is a response only when the **active transaction accepts it**.
Arrival order alone never establishes ownership.

Example:

```text
GET_DEVICE_TIME ->

<- MESSAGES_WAITING
<- ADVERTISEMENT
<- ACK
<- CURRENT_TIME
```

Only `CURRENT_TIME` completes the `GET_DEVICE_TIME` transaction. The other packets must remain available for push/deferred processing.
