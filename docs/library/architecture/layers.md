# Layer responsibilities

[Оглавление](../../ARCHITECTURE.md) · [Маршрутизация чтения](../../README.md)

## Layer responsibilities

### `MeshCoreSharp`

Public orchestration layer. `MeshCoreClient` owns:

- lifecycle (`ConnectAsync`, `DisconnectAsync`);
- APP_START readiness state;
- public high-level operations;
- receive loop;
- public events;
- translation from typed wire packets to application models.

It should not contain TCP framing logic or giant protocol parsing switches.

### `MeshCoreSharp.Transport`

`IMeshCoreTransport` exposes logical Companion frames.

The current transport abstraction must hide all transport-specific framing. TCP/Serial implementations frame outgoing bytes and deframe incoming stream bytes. A future BLE implementation should expose the same logical-frame contract even though BLE framing differs.

`SerialMeshCoreTransport` uses the same frame encoder/decoder as TCP, backed by
`System.IO.Ports.SerialPort` (8N1, no flow control). Each connection owns a decoder,
receive channel, cancellation source, write gate, and port. The dedicated RX thread
uses synchronous byte reads with a finite timeout; idle timeouts are normal.
Writes are serialized and bounded by the driver's write timeout. Synchronous driver
calls finish before cancellation is reported or the port is disposed, avoiding
abandoned I/O tasks and disposal under an active write. A failed write faults the
session because a partial frame may have reached the device. Session shutdown
completes RX, waits for I/O, and disposes the port; reconnect creates fresh state.
Upper layers and TCP implementation do not depend on the serial backend.

### `MeshCoreSharp.Protocol`

Pure protocol representation:

- command/packet enums;
- protocol limits;
- command encoders;
- endian-safe readers/writers;
- typed packet parsers;
- decoder and raw fallback packet.

Protocol parsing should avoid network/lifecycle concerns.

### `MeshCoreSharp.Runtime`

Internal concurrency and transaction machinery:

- `CommandDispatcher`;
- `PacketRouter`;
- transaction state machines;
- ACK tracker and future deferred Mesh trackers;
- incoming-message pump.

Keep this namespace internal unless a type is clearly required in the public API.
