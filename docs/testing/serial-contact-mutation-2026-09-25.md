# Serial contact mutation test — 2026-09-25

Device and connection:

- port: `/dev/cu.usbserial-0001`;
- node: `RnD CatCore🐈`, Heltec V3 USB Companion;
- Serial control lines: `DTR=true`, `RTS=true`;
- automatic message draining disabled;
- allowed commands: APP_START, GET_CONTACTS, GET_STATS(core), and exact mutations
  for one deterministic temporary key;
- no radio commands were permitted.

The initial list contained 103 contacts and did not contain the temporary key. The
test then performed:

1. Add `MeshCoreSharp temp` as a Chat contact with unknown path; readback returned
   104 contacts and matching fields.
2. Update the same key to `MeshCoreSharp temp updated` with flags `1`; readback
   remained at 104 contacts and contained the new values.
3. Remove the temporary key, wait for the firmware's lazy store write, and read the
   list again; it returned to the original 103 contacts with no temporary entry.

Device uptime increased from 3140 to 3151 seconds, so no reset was observed. Existing
contacts were not modified, and the transport guard would reject any unrelated
configuration or RF command.
