# M7 — BLE transport

[Оглавление](../../ROADMAP.md) · [Маршрутизация чтения](../../README.md)

## M7 — BLE transport

Implement BLE transport with logical Companion frames and BLE-specific connection/MTU concerns hidden from protocol/runtime layers.

Do not reuse TCP/Serial stream framing where BLE protocol does not use it.

## M8 — Robustness and diagnostics

Add:

- `Microsoft.Extensions.Logging` integration or another low-dependency logging abstraction decision;
- structured raw-frame diagnostics at trace level;
- better malformed-packet diagnostics;
- protocol/version capability profile where proven necessary;
- connection-loss tests;
- cancellation/gate-release regression tests;
- optional reconnect policy only after core protocol behavior is stable.

## M9 — Packaging/public API stabilization

Before first stable NuGet release:

- review public/internal visibility;
- XML documentation for public API;
- semantic versioning policy;
- NuGet metadata;
- sample application cleanup;
- compatibility matrix for tested firmware versions/transports.

## Future solution projects

The desktop messenger has a concrete design and its stage A1 scaffold is implemented:

- [Application architecture](../../MESSENGER_ARCHITECTURE.md): Avalonia, SQLite,
  connection profiles/reconnect, separate Chat/channel/device tabs, durable history.
- [Implementation stages](../../MESSENGER_PLAN.md): A–E for the first usable release,
  F for optional diagnostics and later improvements.

The solution now includes Core, Avalonia Desktop and two xUnit v3 projects, pinned
SDK/package versions, NuGet lock files, DI/logging bootstrap and a verified empty
desktop window. Database, connection and messaging application services remain pending.

Application reconnect belongs in its supervisor, not in the Companion library.
A small event-queue flush API is planned to let orderly shutdown persist callbacks
already queued by RX. It does not provide a device-side durable message acknowledgement.

Other future additions may include:

- CLI/diagnostic tool;
- additional test/integration harnesses.

Protocol and Companion communication logic should remain in `MeshCoreSharp`, not duplicated into UI projects.
