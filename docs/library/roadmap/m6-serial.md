# M6 — Serial transport

[Оглавление](../../ROADMAP.md) · [Маршрутизация чтения](../../README.md)

## M6 — Serial transport

Implemented ahead of M3–M5 at the project owner's request:

- `SerialMeshCoreTransport` / options, backed by `System.IO.Ports`.
- Shared stream framing, serialized writes, finite read/write timeouts, clean disconnect/reconnect.
- Console `--serial` and `--list-ports` modes.
- Regression tests with a fake byte port and native macOS arm64 pseudo-terminal smoke test.

Physical USB read-only validation passed on macOS arm64 with Heltec V3, firmware
v1.17.1-d929643: both empty lists and 75-contact lists in three consecutive cycles,
radio TX counters unchanged at zero. See [hardware report](../../testing/serial-usb-2026-09-24.md).
Windows/Linux validation remains pending.

Serial control defaults and hardware harnesses now use DTR=true/RTS=true to avoid
pulsing the standard ESP32 auto-reset circuit. Two consecutive Heltec V3 connections
preserved uptime (481 -> 484 seconds); see the
[no-reset report](../../testing/serial-no-reset-2026-09-25.md). Earlier false/false harness
settings likely caused the observed reboots and loss of queued test messages.

No changes to `MeshCoreClient`, packet parsing, transactions, or public operation APIs
were required to switch from TCP to Serial.
