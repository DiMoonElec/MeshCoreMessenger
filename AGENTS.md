# MeshCoreSharp

MeshCoreSharp is a C#/.NET library for communicating with a MeshCore
Companion device.

## Project structure

- `src/MeshCoreSharp` is the main library.
- The entire Companion implementation must compile into one `MeshCoreSharp.dll`.
- Do not split protocol, transports, runtime, or models into separate projects.
- Separate internal layers using namespaces and folders.
- Additional projects such as GUI applications may be added to the solution later.

## Architecture

The main layers are:

- `MeshCoreSharp` — public high-level client API.
- `MeshCoreSharp.Models` — public models.
- `MeshCoreSharp.Protocol` — Companion protocol encoding/decoding.
- `MeshCoreSharp.Transport` — transport abstraction.
- `MeshCoreSharp.Transport.Tcp` — TCP implementation.
- `MeshCoreSharp.Transport.Serial` — future serial implementation.
- `MeshCoreSharp.Transport.Ble` — future BLE implementation.
- `MeshCoreSharp.Runtime` — internal dispatcher/router/transactions.

Runtime implementation details should normally be `internal`.

## Transport contract

Transport works with logical Companion frames.

For TCP and Serial:

TX wire format:
`0x3C + UInt16LE(payload length) + payload`

RX wire format:
`0x3E + UInt16LE(payload length) + payload`

The framing must remain inside the transport layer.

Upper protocol/runtime layers must never depend on TCP/Serial framing.

BLE does not use this stream framing.

## Companion protocol rules

- A Companion may send unsolicited packets at any time.
- A push packet may arrive between a command and its response.
- Never assume the next received packet is the response to the last command.
- Normal commands are single-flight because many responses contain no request ID.
- Register response transactions before sending the command.
- RX processing must run continuously and independently of command execution.
- `0x80+` packet types are generally asynchronous push packets.
- Some commands have multi-frame responses.
- Some Mesh operations return `MSG_SENT` immediately and complete later through a push packet.
- Remote Mesh requests such as status/telemetry/binary/path discovery are effectively single-flight in current firmware.

## Current implementation status

Working:

- TCP connection
- Companion stream framing
- continuous RX loop
- PacketRouter
- CommandDispatcher
- APP_START / SELF_INFO
- GET_DEVICE_TIME / SET_DEVICE_TIME
- DEVICE_QUERY
- GET_BATT_AND_STORAGE
- unsolicited push routing

Next major feature:

Implement GET_CONTACTS as a multi-frame transaction:

`CONTACTS_START -> CONTACT* -> CONTACTS_END`

Push packets must be allowed to occur between CONTACT frames.

## Source of truth

When protocol sources disagree, use this priority:

1. Current MeshCore companion firmware
2. Current `docs/companion_protocol.md`
3. `meshcore_py` as reference implementation/regression source

Do not blindly port Python implementation details.

## Engineering rules

- Target modern .NET/C#.
- Prefer strongly typed packet and model types over dictionaries.
- Avoid magic protocol numbers outside protocol definitions.
- Preserve CancellationToken throughout async APIs.
- Never block the RX loop waiting for a command.
- Unknown packet types should not tear down the connection.
- Add protocol regression tests when fixing protocol edge cases.