# Starting a new Codex session

Open the repository root (the directory containing `MeshCoreSharp.sln` and `AGENTS.md`) in VS Code/Codex.

A good first prompt for the next implementation session is:

> Read `AGENTS.md`, `docs/ARCHITECTURE.md`, `docs/COMPANION_PROTOCOL.md`, `docs/ROADMAP.md`, and inspect the current code under `src/MeshCoreSharp`. The TCP implementation is already confirmed working against a real MeshCore Companion node. Preserve the existing architecture. Implement roadmap milestone M1: `GET_CONTACTS` as a multi-frame transaction `CONTACT_START -> CONTACT* -> CONTACT_END`, allowing unrelated push packets between contact frames. Add typed parsers/models and focused tests. Do not bypass `CommandDispatcher` or block the continuous RX loop.

For later sessions, reference the relevant roadmap milestone and ask Codex to read `AGENTS.md` first. Keep durable architectural/protocol discoveries in `docs/` instead of relying on chat history.
