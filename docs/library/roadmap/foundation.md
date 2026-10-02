# MeshCoreSharp roadmap

[Оглавление](../../ROADMAP.md) · [Маршрутизация чтения](../../README.md)


The roadmap is ordered to validate architecture with progressively harder protocol patterns rather than by simply implementing command numbers in sequence.

## Completed foundation

### Library shape

- One library project: `src/MeshCoreSharp`.
- Output assembly: `MeshCoreSharp.dll`.
- Namespace/folder separation instead of many small library projects.
- Console sample is a separate application project.

### Transport/runtime baseline

- TCP transport implemented.
- TCP/Serial-style stream framing implemented.
- Continuous RX loop independent from command execution.
- Serialized immediate command dispatcher.
- Response matcher registration before command send.
- Push/unhandled packet events.
- Unknown packet raw fallback.

### Implemented immediate operations

- `APP_START -> SELF_INFO`.
- `GET_DEVICE_TIME -> CURRENT_TIME`.
- `SET_DEVICE_TIME -> OK/ERROR`.
- `DEVICE_QUERY -> DEVICE_INFO`.
- `GET_BATT_AND_STORAGE -> BATT_AND_STORAGE`.

TCP communication has been confirmed against a real Companion node by the project owner.
