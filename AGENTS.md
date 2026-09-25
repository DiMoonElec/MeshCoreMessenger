# MeshCoreSharp — instructions for coding agents

MeshCoreSharp is a C#/.NET library for communicating with a MeshCore Companion device.
The TCP implementation has already been tested successfully against a real Companion node.
Preserve working behavior while extending the protocol surface.

## Repository and assembly structure

- `src/MeshCoreSharp` is the **single Companion library project**.
- The library must compile to one project-owned assembly: `MeshCoreSharp.dll`.
- Do **not** split protocol, runtime, transports, or models into separate library projects.
- Use folders, namespaces, and `internal` visibility to maintain architectural boundaries.
- Additional solution projects such as GUI, CLI, tests, or samples may be added later and should reference `MeshCoreSharp`.
- `samples/MeshCoreSharp.Console` is a diagnostic/sample application, not part of the public library API.

## Namespace/layer boundaries

- `MeshCoreSharp` — public high-level client API and options.
- `MeshCoreSharp.Models` — public application-facing models.
- `MeshCoreSharp.Protocol` — protocol constants/enums and low-level protocol definitions.
- `MeshCoreSharp.Protocol.Commands` — command encoding; normally internal.
- `MeshCoreSharp.Protocol.Encoding` — byte readers/writers; internal implementation detail.
- `MeshCoreSharp.Protocol.Packets` — decoded wire packets.
- `MeshCoreSharp.Protocol.Parsing` — packet parsers/decoder.
- `MeshCoreSharp.Transport` — transport abstraction.
- `MeshCoreSharp.Transport.Tcp` — TCP transport.
- `MeshCoreSharp.Transport.Serial` — serial transport via `System.IO.Ports`, sharing stream framing with TCP.
- `MeshCoreSharp.Transport.Ble` — future BLE transport.
- `MeshCoreSharp.Runtime` — dispatcher/router/transaction machinery; keep internal unless there is a compelling API reason otherwise.

## Transport contract

`IMeshCoreTransport` exchanges **logical Companion frames**, not TCP/Serial framing bytes.

For TCP and Serial stream transports:

- TX wire format: `0x3C + UInt16LE(payload length) + payload`
- RX wire format: `0x3E + UInt16LE(payload length) + payload`

The `0x3C` / `0x3E` framing and the UInt16 length belong entirely inside the stream transport implementation.
Upper protocol/runtime layers must not depend on it.

BLE is expected to deliver logical Companion frames without this stream wrapper.

TCP is a byte stream. Never assume one `ReadAsync` equals one Companion frame. The decoder must handle fragmented headers/payloads and multiple frames in one read.

## Core Companion protocol rules

These rules are architectural invariants:

1. The Companion may send unsolicited/push packets at any time.
2. A push packet may arrive between a command and its response.
3. Never treat “the next received packet” as the response merely because it arrived after a command.
4. Normal command exchanges are single-flight because many responses contain no request/correlation ID.
5. Register the response transaction/matcher **before** sending the command (subscribe-before-send).
6. RX processing must run continuously and independently of command execution.
7. Push packets (`0x80+` in the current protocol) must continue to flow while a command is pending.
8. Some commands are multi-frame transactions, e.g. contacts: `CONTACT_START -> CONTACT* -> CONTACT_END`.
9. Some Mesh operations are two-phase: immediate `MSG_SENT`, then a later asynchronous completion such as `ACK`, `STATUS_RESPONSE`, `BINARY_RESPONSE`, etc.
10. Current firmware has a separate single-flight limitation for several remote Mesh requests because starting another request can clear firmware pending-request state.

See `docs/ARCHITECTURE.md` and `docs/COMPANION_PROTOCOL.md` before changing routing, transactions, concurrency, framing, or timeouts.

## Current implementation status

Implemented as of 2026-09-25; consult `PLAN.md`, source, and `docs/testing/` for details:

- TCP and Serial transports, continuous RX, serialized dispatcher, subscribe-before-send.
- Typed startup/device/time/battery information and local core/radio/packet statistics.
- Multi-frame contacts with inactivity/absolute timeouts and interleaved pushes.
- Automatic and explicit incoming-message draining, legacy/V3 text and channel data.
- Private/channel text sending, separate private ACK delivery tracking, no automatic retries.
- Advertisement sending and typed ADVERT/NEW_ADVERT events.
- Channel enumeration, configuration, hashtag key derivation and clearing.
- Contact add/update/remove with typed configuration and validation.
- Raw fallback and asynchronous diagnostic events.

Release build and 82 regression tests passed at the last implementation checkpoint.
Hardware reports cover USB without reset, channel exchange, and contact restoration
followed by a private message confirmed by both ACK and the recipient.

## Immediate next milestone

The desktop application architecture is designed and stage A1 scaffolding is implemented.
Read
`docs/MESSENGER_ARCHITECTURE.md` and `docs/MESSENGER_PLAN.md` before application work.
Do not recreate the Core/Desktop/test projects, package pins, lock files, or basic
DI/logging bootstrap. Continue only with the specific remaining stage requested by the user.
The rest of stage A starts with application data paths/single-instance behavior,
then SQLite foundations and a tested event-queue completion barrier in the library.
Application services use public Companion APIs; do not duplicate protocol/runtime code.
Auto-connect/reconnect is an application policy owned by its ConnectionSupervisor;
it must never implicitly replay outgoing messages or mutate device configuration.
Keep the Companion library free of UI, database and message-history responsibilities.

## Engineering rules

- Target modern .NET/C# and preserve `CancellationToken` through async APIs.
- Do not block the RX loop waiting for a command or user callback.
- Avoid magic protocol numbers outside protocol definitions.
- Prefer strongly typed packets/models over dictionaries.
- Unknown packet types must not tear down a healthy connection; preserve their raw bytes when feasible.
- Malformed packets should fail locally and surface diagnostics without corrupting framing state.
- Public API should remain small. Internal machinery should normally be `internal`.
- Do not add automatic reconnect until the core protocol behavior is stable.
- Do not blindly port Python implementation structure. Use Python as a reference/regression source.
- When protocol sources disagree, use this priority:
  1. current MeshCore Companion firmware behavior,
  2. current `docs/companion_protocol.md`,
  3. `meshcore_py` reference implementation/tests.
- When fixing a protocol edge case, add a focused regression test where practical.

## Known source differences to remember

Do not assume all `meshcore_py` enums/helpers match every current firmware build. Previously identified examples include:

- current firmware supports command `62` (`SEND_CHANNEL_DATA`), while some Python enum revisions omitted it;
- Python has exposed command `66` (`RUN_CLI_COMMAND`) in revisions where baseline firmware did not yet implement it;
- `FACTORY_RESET` firmware expects the literal `reset` payload after command `51` and typically reboots/disconnects before a reliable `OK` can be consumed;
- `HAS_CONNECTION` in current firmware expects a public key argument;
- `IMPORT_PRIVATE_KEY` may return `DISABLED`;
- login completion can be either `LOGIN_SUCCESS` or `LOGIN_FAIL`;
- `SYNC_NEXT_MESSAGE` may return channel-data frames in addition to text-message variants and `NO_MORE_MESSAGES`.

Verify behavior against the firmware version being targeted before expanding these commands.
