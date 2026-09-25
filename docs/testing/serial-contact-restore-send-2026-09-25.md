# Serial contact remove/restore/send test — 2026-09-25

Device and connection:

- port: `/dev/cu.usbserial-0001`;
- node: `RnD CatCore🐈`, Heltec V3 USB Companion;
- target: the unique `RnD Mesh01` contact with key prefix `DF73015A6BB9`;
- Serial control lines: `DTR=true`, `RTS=true`;
- automatic message draining disabled;
- the command guard allowed only startup, contact reads, core/packet statistics,
  the exact target removal/restoration, and one exact private test message.

The test saved the complete typed contact record before making any change. It then:

1. Removed `RnD Mesh01`; readback changed the contact count from 107 to 106 and
   confirmed that the target was absent.
2. Restored the contact from the saved record; readback returned 107 contacts and
   all persisted fields matched the snapshot.
3. Sent exactly one 125-byte UTF-8 private message:
   `Тестовая личная отправка при разработке MeshCoreSharp. Ответ не требуется.`
4. Received `MSG_SENT` for a direct route with expected ACK `F884C555`, followed by
   the matching ACK after 2775 ms. Delivery status was `Confirmed`.

The packet TX counter increased from 5 to 6. Device uptime increased from 3887 to
3900 seconds, so no reset was observed. The target contact remained restored after
the test. The operator of the recipient node subsequently confirmed that the test
message was received, completing the end-to-end check beyond the protocol ACK.

An earlier attempt used a 194-byte Russian message. The library rejected it locally
against the 160-byte protocol limit before sending a command; that attempt caused no
radio transmission. Its remove/restore portion also completed successfully.
