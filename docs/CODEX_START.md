# Starting a new Codex session

Open the repository root (the directory containing `MeshCoreSharp.sln` and `AGENTS.md`) in VS Code/Codex.

A good first prompt for the desktop application implementation is:

> Read `AGENTS.md` and `docs/README.md`. For application work read the current-state and mandatory invariant sections listed there, then only the requested stage and its relevant architecture dependencies. The former large documents are indexes: do not load every linked file. Inspect git status and existing code before changing it; historical milestones are not unfinished tasks. Do not recreate projects, package pins, lock files, or bootstrap. Implement only the substage explicitly requested by the user. Preserve the single Companion assembly and continuous RX architecture. Update the stage document with actual verification results. No hardware transmissions are implied by application-stage work.

For library work, use [PLAN.md](../PLAN.md) and [ROADMAP.md](ROADMAP.md).
Contacts, message draining, sends/ACK, advertisements, and channel/contact mutation
are already implemented; do not restart those milestones from scratch.

For application work, use [MESSENGER_ARCHITECTURE.md](MESSENGER_ARCHITECTURE.md)
and [MESSENGER_PLAN.md](MESSENGER_PLAN.md). Later sessions should name the relevant
stage and read `AGENTS.md` first. Keep durable architectural/protocol discoveries
in `docs/` instead of relying on chat history.

Use the [reading routes](README.md) to select sections instead of loading the entire
plan, architecture or protocol. Read [current-state](messenger/plan/current-state.md)
and the [current UI plan](messenger/plan/ui-components.md) for the application checkpoint.
