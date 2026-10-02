# Immediate request/response commands

[Оглавление](../../COMPANION_PROTOCOL.md) · [Маршрутизация чтения](../../README.md)

## Immediate request/response commands

Normal local commands are effectively single-flight because response packets often have no request ID. The client should serialize them through one command queue/gate.

Typical pattern:

```text
register response matcher
send command
wait for matching packet / ERROR / timeout
unregister matcher
```

Do not send first and subscribe second.

Examples already implemented in MeshCoreSharp:

- `APP_START -> SELF_INFO`
- `GET_DEVICE_TIME -> CURRENT_TIME`
- `SET_DEVICE_TIME -> OK/ERROR`
- `DEVICE_QUERY -> DEVICE_INFO`
- `GET_BATT_AND_STORAGE -> BATT_AND_STORAGE`
