# Starting a new Codex session

Open the repository root (the directory containing `MeshCoreSharp.sln` and `AGENTS.md`) in VS Code/Codex.

A good first prompt for the desktop application implementation is:

> Read `AGENTS.md`, `docs/MESSENGER_ARCHITECTURE.md`, `docs/MESSENGER_PLAN.md`, `docs/ARCHITECTURE.md`, and `docs/COMPANION_PROTOCOL.md`. Inspect the existing code before making changes. Stage A1 scaffolding is complete; do not recreate projects, package pins, lock files, or bootstrap. Implement only the next substage explicitly requested by the user. Preserve the single Companion assembly and continuous RX architecture. Update the checklist with actual verification results. No hardware transmissions are implied by application-stage work.

For library work, use [PLAN.md](../PLAN.md) and [ROADMAP.md](ROADMAP.md).
Contacts, message draining, sends/ACK, advertisements, and channel/contact mutation
are already implemented; do not restart those milestones from scratch.

For application work, use [MESSENGER_ARCHITECTURE.md](MESSENGER_ARCHITECTURE.md)
and [MESSENGER_PLAN.md](MESSENGER_PLAN.md). Later sessions should name the relevant
stage and read `AGENTS.md` first. Keep durable architectural/protocol discoveries
in `docs/` instead of relying on chat history.
