# M4 — Channels and contact mutation APIs

[Оглавление](../../ROADMAP.md) · [Маршрутизация чтения](../../README.md)

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
groups and 76 contacts. See the [hardware report](../../testing/serial-usb-2026-09-24.md),
including a first-attempt APP_START timeout followed by a successful explicit retry.

Channel mutation is implemented: exact-key `SetChannelAsync`, standard hashtag
derivation, `SetHashtagChannelAsync`, and `ClearChannelAsync`. Encoding, validation,
interleaved push, ERROR recovery and command serialization have regression coverage.
On the physical Heltec V3, slot 39 was set/read, cleared/read and finally configured
as `#mcs-dev-test`; one labeled message increased TX 4 -> 5 without resetting the node.
See the [channel configuration report](../../testing/serial-channel-config-2026-09-25.md).

Contact mutation is implemented with `ContactConfiguration`, overloads for decoded
Contact/AdvertisementInfo, `AddOrUpdateContactAsync` and `RemoveContactAsync`.
Validation covers key/path sizes, packed path descriptors, UTF-8 names, coordinates,
errors, cancellation and interleaved pushes. Physical add/read/update/read/remove
returned the list from 103 to 103 contacts without RF or reset. See the
[contact mutation report](../../testing/serial-contact-mutation-2026-09-25.md).
Removal and restoration of the existing `RnD Mesh01` record was also verified:
107 -> 106 -> 107 contacts with matching persisted fields, followed by one direct
message, matching ACK in 2775 ms, and recipient confirmation. See the
[remove/restore/send report](../../testing/serial-contact-restore-send-2026-09-25.md).

Remaining operations:

- send channel data;
- get contact by key;
- share/export/import contact;
- radio/tuning/basic configuration calls.

Each operation should have typed command encoding, expected packet set, and regression tests.

04.10.2026: ручной `ResetPathAsync(fullPublicKey)` реализован для Messenger.
97 library regression tests и loopback TCP application tests passed; отдельная
аппаратная проверка этой команды ожидается. [Отчёт](../../testing/private-route-reset.md).
