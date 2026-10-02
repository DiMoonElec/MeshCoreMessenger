# M5 — Remote Mesh requests

[Оглавление](../../ROADMAP.md) · [Маршрутизация чтения](../../README.md)

## M5 — Remote Mesh requests

Add shared `MeshRequestGate` and deferred-operation tracking.

Implement in this order:

1. login (accept both success/failure completion);
2. status request;
3. telemetry request;
4. binary request;
5. path discovery;
6. trace requests (separate tag tracker if verified safe).

Use `MSG_SENT.suggested_timeout` plus client safety policy for final Mesh response waits.
