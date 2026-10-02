# Terminal commands

[Оглавление](../../COMPANION_PROTOCOL.md) · [Маршрутизация чтения](../../README.md)

## Terminal commands

Some commands are not normal request/response transactions.

### Reboot

A reboot may not return a useful response before the device resets/disconnects. Treat successful command write followed by disconnect/reboot as expected lifecycle behavior rather than requiring `OK`.

### Factory reset

In the baseline firmware examined for this project, factory reset command `51` expects the literal ASCII payload `reset` after the command byte. Firmware may disconnect/reset before a reliable `OK` reaches the application.

Do not model this as a normal `command -> OK` transaction without verifying the target firmware.

## Known source-version differences

Previously identified differences/gaps to keep in mind:

- Current firmware included command `62` (`SEND_CHANNEL_DATA`) while some `meshcore_py` revisions omitted it from `CommandType`.
- Some Python revisions expose command `66` (`RUN_CLI_COMMAND`) and packet `CLI_REPLY`, while the examined baseline firmware command table stopped at `65` and treated unknown commands as unsupported.
- Current firmware `HAS_CONNECTION` expects a public key argument; do not copy a helper that sends only command `28`.
- `IMPORT_PRIVATE_KEY` can return `DISABLED` when the feature is compile-time disabled.
- Login high-level completion must accept both success and failure.
- `SYNC_NEXT_MESSAGE` can return `CHANNEL_DATA_RECV` in addition to text-message variants and `NO_MORE_MESSAGES`.
- Documentation and firmware have differed for `SEND_CHANNEL_TXT_MSG`; the examined firmware behavior was `OK/ERROR`, so prefer actual target-firmware behavior.

## Command/packet enums in current repository

`src/MeshCoreSharp/Protocol/CommandType.cs` and `PacketType.cs` intentionally contain more of the known protocol surface than is currently implemented by public APIs.

Presence in an enum does **not** mean the operation is fully implemented or proven against the current target firmware. Add command encoders, packet parsers, transactions, and tests before exposing a high-level API.
