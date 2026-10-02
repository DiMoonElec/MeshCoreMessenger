# Logical Companion frame

[Оглавление](../../COMPANION_PROTOCOL.md) · [Маршрутизация чтения](../../README.md)

## Logical Companion frame

At the protocol layer, a Companion frame is:

```text
+------------+-----------------------------+
| Type byte  | packet-specific payload ... |
+------------+-----------------------------+
```

For a command, the first byte is a command code. For received data, the first byte is a packet/response/push type.

Most multi-byte integer fields are little-endian. Individual payload formats may define exceptions; parse according to that packet's specification.

There is no universal request ID/correlation ID in every command/response.

## TCP/Serial stream framing

For the stream transports used by current `meshcore_py` TCP/Serial implementations:

### App -> Companion

```text
0x3C | length UInt16 LE | logical Companion payload
 '<'
```

### Companion -> App

```text
0x3E | length UInt16 LE | logical Companion payload
 '>'
```

The length counts only the logical Companion payload, not the 3-byte stream header.

Example logical command:

```text
05
```

TCP/Serial wire representation:

```text
3C 01 00 05
```

Example logical response:

```text
09 78 56 34 12
```

wire representation:

```text
3E 05 00 09 78 56 34 12
```

The stream framing has no separate CRC/checksum field.

TCP is a byte stream: a read may contain half a frame, exactly one frame, multiple frames, or the end of one frame plus part of another.

The implemented Serial transport uses this same framing and handles arbitrary read
boundaries. Defaults are 115200 baud, 8N1, no flow control, DTR on, RTS on, with a
200 ms opening delay. `System.IO.Ports` applies DTR and then RTS after opening; on
ESP32 boards, requesting false/false can pass through a reset-producing intermediate
state. The default true/true pair is a non-reset state for the standard two-transistor
auto-reset circuit. Speed, DTR/RTS and delay remain configurable for boards with
different wiring. See Espressif's [automatic bootloader documentation](https://docs.espressif.com/projects/esptool/en/latest/esp32/advanced-topics/boot-mode-selection.html)
and the [.NET Unix SerialStream initialization](https://github.com/dotnet/runtime/blob/main/src/libraries/System.IO.Ports/src/System/IO/Ports/SerialStream.Unix.cs).

## BLE framing

BLE should be modeled as delivering logical Companion frames without the `0x3C/0x3E + UInt16` stream wrapper. Keep BLE-specific details inside the transport implementation.

## Frame size

Current baseline firmware examined for this project uses a Companion frame limit of 176 bytes (`MAX_FRAME_SIZE`).

Therefore for TCP/Serial:

```text
logical max: 176 bytes
wire max:    179 bytes (3-byte framing header + logical frame)
```

The UInt16 framing length is wider than the actual current firmware payload limit; do not infer a 65535-byte supported frame from the header type.

The current implementation may keep a somewhat larger defensive RX safety ceiling to avoid immediate incompatibility with future firmware, but outgoing commands should respect the targeted firmware's actual limit.

Minimum useful logical Companion frame size is one byte (for example `OK` or `MESSAGES_WAITING`).
