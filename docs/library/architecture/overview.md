# MeshCoreSharp architecture

[Оглавление](../../ARCHITECTURE.md) · [Маршрутизация чтения](../../README.md)


This document records the intended runtime architecture and concurrency model. It exists so future Codex/agent sessions do not have to reconstruct the design from source history.

## Goals

MeshCoreSharp should provide a small, typed, transport-independent C# API for a MeshCore Companion while remaining one library project/assembly.

Primary goals:

- one `MeshCoreSharp.dll` for all Companion functionality;
- transport independence above `IMeshCoreTransport`;
- continuous asynchronous receive processing;
- correct coexistence of request/response traffic and unsolicited packets;
- strongly typed protocol decoding;
- explicit handling of multi-frame and deferred operations;
- predictable cancellation/timeout behavior.
