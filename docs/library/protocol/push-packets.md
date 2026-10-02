# Asynchronous packets

[Оглавление](../../COMPANION_PROTOCOL.md) · [Маршрутизация чтения](../../README.md)

## Asynchronous packets

The Companion can push packets without a request and can push them while a command transaction is active.

Current push range used by the baseline protocol includes:

| Code | Meaning |
|---:|---|
| `0x80` | Advertisement |
| `0x81` | Path updated |
| `0x82` | Send confirmed / ACK |
| `0x83` | Messages waiting |
| `0x84` | Raw data |
| `0x85` | Login success |
| `0x86` | Login fail |
| `0x87` | Status response |
| `0x88` | RX/log data |
| `0x89` | Trace data |
| `0x8A` | New advertisement |
| `0x8B` | Telemetry response |
| `0x8C` | Binary response |
| `0x8D` | Path discovery response |
| `0x8E` | Control data |
| `0x8F` | Contact deleted |
| `0x90` | Contacts full |

Transport-wise these are push packets. Semantically some are pure unsolicited events while others are final completions of a previously initiated Mesh operation.

Correct client assumption:

```text
send command
<- unrelated push
<- unrelated push
<- expected response
```

is valid.
