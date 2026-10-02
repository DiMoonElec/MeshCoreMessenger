# MeshCore Companion protocol notes for MeshCoreSharp

[Оглавление](../../COMPANION_PROTOCOL.md) · [Маршрутизация чтения](../../README.md)


This is an implementation-oriented companion to the upstream MeshCore documentation. It records protocol behavior already analyzed/tested for this project, including important concurrency implications and known source-version differences.

It is **not** intended to replace the upstream firmware source or protocol specification. When a behavior is uncertain, verify it against the targeted firmware revision.

## Source-of-truth priority

When sources disagree, use this order:

1. current MeshCore Companion firmware behavior/source;
2. current upstream `docs/companion_protocol.md`;
3. `meshcore_py` source/tests as a reference implementation/regression corpus.

Do not assume a Python enum/helper is supported by every firmware build.
