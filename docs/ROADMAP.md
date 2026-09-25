# MeshCoreSharp roadmap

The roadmap is ordered to validate architecture with progressively harder protocol patterns rather than by simply implementing command numbers in sequence.

## Completed foundation

### Library shape

- One library project: `src/MeshCoreSharp`.
- Output assembly: `MeshCoreSharp.dll`.
- Namespace/folder separation instead of many small library projects.
- Console sample is a separate application project.

### Transport/runtime baseline

- TCP transport implemented.
- TCP/Serial-style stream framing implemented.
- Continuous RX loop independent from command execution.
- Serialized immediate command dispatcher.
- Response matcher registration before command send.
- Push/unhandled packet events.
- Unknown packet raw fallback.

### Implemented immediate operations

- `APP_START -> SELF_INFO`.
- `GET_DEVICE_TIME -> CURRENT_TIME`.
- `SET_DEVICE_TIME -> OK/ERROR`.
- `DEVICE_QUERY -> DEVICE_INFO`.
- `GET_BATT_AND_STORAGE -> BATT_AND_STORAGE`.

TCP communication has been confirmed against a real Companion node by the project owner.

## M1 — Contacts multi-frame transaction

Implemented (automated regression tests, local TCP self-test, and real-device contacts retrieval confirmed by the project owner):

- contact wire model/parser;
- `CONTACT_START` parser;
- `CONTACT` parser;
- `CONTACT_END` parser;
- `ContactsTransaction` state machine;
- `MeshCoreClient.GetContactsAsync()`;
- public `Contact` model;
- stream inactivity timeout behavior;
- tests where push packets are interleaved between contact frames.

Definition of done:

```text
GET_CONTACTS
<- CONTACT_START
<- CONTACT
<- MESSAGES_WAITING
<- CONTACT
<- ADVERTISEMENT
<- CONTACT_END
```

returns the complete contact collection and publishes the push packets without breaking the transaction.

## M2 — Incoming messages and message pump

Implemented (automated regression tests and local TCP self-test). Real-device private
message reception verified on Heltec V3 after a Flood advertisement: the message
was delivered through MessageReceived on 2026-09-24. See [report](testing/serial-advert-2026-09-24.md).

Packet parsers for:

- legacy private/contact message;
- legacy channel message;
- V3 private/contact message;
- V3 channel message;
- channel data;
- `NO_MORE_MESSAGES`.

Coalescing internal `MessagePump`:

```text
MESSAGES_WAITING
  -> schedule drain
  -> SYNC_NEXT_MESSAGE repeatedly
  -> publish decoded message(s)
  -> stop on NO_MORE_MESSAGES
```

Public `MessageReceived` delivers `ContactMessage`, `ChannelMessage`, or `ChannelDataMessage`,
while low-level packet diagnostics remain available. The pump starts after successful APP_START,
reads the offline queue once, and responds to subsequent MESSAGES_WAITING notifications.
It stops with the connection and reports errors without automatic retry loops.
`AutoReceiveMessages` can disable automatic requests. Public `DrainMessagesAsync`
runs an explicit drain through the same pump, coalesces concurrent callers, isolates
caller cancellation and reports failures. A timed-out sync requires reconnect because
a late uncorrelated response cannot be assigned safely to a retry.

## M3 — Outgoing text and ACK tracking

Implemented (82 regression tests in total and TCP self-test pass):

- `SEND_TXT_MSG` encoder;
- `MSG_SENT` parser;
- `ACK` parser;
- `AckTracker` keyed by expected ACK;
- `SendTextAsync` returns immediate acceptance plus independent `Delivery` task;
- overloads accept either a typed `Contact` or a full 32-byte public key;
- timeout/cancellation behavior for ACK waits;
- race handling for an ACK that arrives very quickly after `MSG_SENT`.

CommandGate is released before the delivery wait. Registration happens synchronously
on RX when MSG_SENT is accepted, before processing a following ACK. A per-connection
eight-send window protects the firmware's circular table, including when newer sends
complete before an older one. No automatic retransmission. Text length is checked in
UTF-8 bytes; channel length accounts for the sender-name prefix.

Channel text (`SendChannelTextAsync`, OK/ERROR) is also implemented and physically
tested on Heltec V3: one message in #test, TX/flood counters 0 -> 1. Private text/ACK
was then verified against contact `RnD Mesh01`: a single direct send, matching ACK,
RTT 2775 ms, TX/direct 0 -> 1. See [report](testing/serial-private-send-2026-09-25.md).

## M4 — Channels and contact mutation APIs

Advertisements implemented: `SendAdvertisementAsync(ZeroHop/Flood)`, typed ADVERT /
NEW_ADVERT pushes and `AdvertisementReceived`. New discoveries remain separate from
contacts transactions. One Flood advertisement verified on Heltec V3 (TX 0 -> 1);
the subsequent private message was received successfully through MessageReceived.
82 regression tests pass.

Implemented ahead of M3: local channel reads (`GetChannelAsync`, `GetChannelsAsync`)
and core/radio/packet statistics. Channel enumeration includes empty slots and uses
DEVICE_INFO capacity. Regression tests cover parsing, index/subtype matching,
errors, cancellation and timeout recovery.
Verified on the physical Heltec V3: 40 slots, 3 named channels, all three statistics
groups and 76 contacts. See the [hardware report](testing/serial-usb-2026-09-24.md),
including a first-attempt APP_START timeout followed by a successful explicit retry.

Channel mutation is implemented: exact-key `SetChannelAsync`, standard hashtag
derivation, `SetHashtagChannelAsync`, and `ClearChannelAsync`. Encoding, validation,
interleaved push, ERROR recovery and command serialization have regression coverage.
On the physical Heltec V3, slot 39 was set/read, cleared/read and finally configured
as `#mcs-dev-test`; one labeled message increased TX 4 -> 5 without resetting the node.
See the [channel configuration report](testing/serial-channel-config-2026-09-25.md).

Contact mutation is implemented with `ContactConfiguration`, overloads for decoded
Contact/AdvertisementInfo, `AddOrUpdateContactAsync` and `RemoveContactAsync`.
Validation covers key/path sizes, packed path descriptors, UTF-8 names, coordinates,
errors, cancellation and interleaved pushes. Physical add/read/update/read/remove
returned the list from 103 to 103 contacts without RF or reset. See the
[contact mutation report](testing/serial-contact-mutation-2026-09-25.md).
Removal and restoration of the existing `RnD Mesh01` record was also verified:
107 -> 106 -> 107 contacts with matching persisted fields, followed by one direct
message, matching ACK in 2775 ms, and recipient confirmation. See the
[remove/restore/send report](testing/serial-contact-restore-send-2026-09-25.md).

Remaining operations:

- send channel data;
- get contact by key;
- share/export/import contact;
- reset path;
- radio/tuning/basic configuration calls.

Each operation should have typed command encoding, expected packet set, and regression tests.

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

## M6 — Serial transport

Implemented ahead of M3–M5 at the project owner's request:

- `SerialMeshCoreTransport` / options, backed by `System.IO.Ports`.
- Shared stream framing, serialized writes, finite read/write timeouts, clean disconnect/reconnect.
- Console `--serial` and `--list-ports` modes.
- Regression tests with a fake byte port and native macOS arm64 pseudo-terminal smoke test.

Physical USB read-only validation passed on macOS arm64 with Heltec V3, firmware
v1.17.1-d929643: both empty lists and 75-contact lists in three consecutive cycles,
radio TX counters unchanged at zero. See [hardware report](testing/serial-usb-2026-09-24.md).
Windows/Linux validation remains pending.

Serial control defaults and hardware harnesses now use DTR=true/RTS=true to avoid
pulsing the standard ESP32 auto-reset circuit. Two consecutive Heltec V3 connections
preserved uptime (481 -> 484 seconds); see the
[no-reset report](testing/serial-no-reset-2026-09-25.md). Earlier false/false harness
settings likely caused the observed reboots and loss of queued test messages.

No changes to `MeshCoreClient`, packet parsing, transactions, or public operation APIs
were required to switch from TCP to Serial.

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

- [Application architecture](MESSENGER_ARCHITECTURE.md): Avalonia, SQLite,
  connection profiles/reconnect, separate Chat/channel/device tabs, durable history.
- [Implementation stages](MESSENGER_PLAN.md): A–E for the first usable release,
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
