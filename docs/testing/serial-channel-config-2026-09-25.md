# Serial channel configuration test — 2026-09-25

Device and connection:

- port: `/dev/cu.usbserial-0001`;
- node: `RnD CatCore🐈`, Heltec V3 USB Companion;
- Serial control lines: `DTR=true`, `RTS=true`;
- TX power reported by `SELF_INFO`: 1 dBm;
- automatic message draining disabled.

The test used the exact channel name `#mcs-dev-test`, with no trailing whitespace.
Its 16-byte secret was derived as the first 16 bytes of SHA-256 over the UTF-8 name;
the secret was deliberately not printed.

The guard first read all 40 channel slots and allowed mutation only after slot 39
was confirmed to have an empty name and an all-zero secret. It then performed:

1. `SET_CHANNEL`, followed by `GET_CHANNEL`: name and derived secret matched.
2. `SET_CHANNEL` with an empty name/all-zero secret, followed by `GET_CHANNEL`:
   the slot was completely clear.
3. A final `SET_CHANNEL` and readback: `#mcs-dev-test` remains configured in slot 39.
4. Exactly one `SEND_CHANNEL_TXT_MSG` containing
   `Тестовая отправка при разработке MeshCoreSharp в #mcs-dev-test. Ответ не требуется`.

Firmware returned local `OK`. The packet TX counter increased from 4 to 5. Device
uptime increased from 1466 to 1472 seconds, so no reset was observed during the
connection. The transport guard rejected any configuration payload outside the
precomputed lifecycle and would reject a second message-send command.

The participant confirmed reception on another node and sent `Test` back to the
same channel. A subsequent guarded manual drain (APP_START and SYNC_NEXT_MESSAGE
only) received a 16-byte channel-message body from slot 39 and reached
`NO_MORE_MESSAGES`. This confirms both directions and maps the inbound message to
the configured `#mcs-dev-test` slot. As expected, neither direction uses a channel ACK.
