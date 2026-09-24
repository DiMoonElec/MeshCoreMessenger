# MeshCoreSharp

Начальная реализация C# библиотеки для MeshCore Companion Protocol.

## Структура

- `src/MeshCoreSharp` — единственный библиотечный проект; результат сборки `MeshCoreSharp.dll`.
- `samples/MeshCoreSharp.Console` — простой TCP-клиент для проверки на реальном companion.

Внутри DLL код разделён namespace-ами:

- `MeshCoreSharp` — публичный high-level клиент.
- `MeshCoreSharp.Models` — публичные модели.
- `MeshCoreSharp.Protocol` — типы протокола.
- `MeshCoreSharp.Protocol.Packets` — decoded companion packets.
- `MeshCoreSharp.Transport` — абстракция транспорта.
- `MeshCoreSharp.Transport.Tcp` — TCP transport.
- `MeshCoreSharp.Runtime` — internal command/router runtime.

## Реализовано в первой итерации

- TCP подключение.
- Stream framing, совместимый с `meshcore_py` TCP/Serial:
  - app -> companion: `0x3C + UInt16LE(length) + payload`;
  - companion -> app: `0x3E + UInt16LE(length) + payload`.
- Независимый постоянный RX loop.
- Packet decoder и typed packets для:
  - `OK`;
  - `ERROR`;
  - `SELF_INFO`;
  - `CURRENT_TIME`;
  - `BATT_AND_STORAGE`;
  - `DEVICE_INFO`.
- Raw packet fallback для пока не реализованных типов, включая push packets.
- Последовательный `CommandDispatcher`.
- Subscribe-before-send: transaction регистрируется в `PacketRouter` до записи команды.
- Push packets могут приходить между command и response и не завершают transaction, если их тип не соответствует ожидаемому ответу.
- Public API:
  - `ConnectAsync()`;
  - `StartAsync()`;
  - `GetDeviceTimeAsync()`;
  - `SetDeviceTimeAsync()`;
  - `GetDeviceInfoAsync()`;
  - `GetBatteryAndStorageAsync()`;
  - `DisconnectAsync()`.

## Пример

```csharp
using MeshCoreSharp;
using MeshCoreSharp.Transport.Tcp;

await using var transport = new TcpMeshCoreTransport("192.168.1.100", 5000);
await using var client = new MeshCoreClient(transport);

client.PushPacketReceived += (_, e) =>
    Console.WriteLine($"Push: 0x{e.Packet.RawType:X2}");

await client.ConnectAsync();

var self = await client.StartAsync();
var info = await client.GetDeviceInfoAsync();
var time = await client.GetDeviceTimeAsync();
var battery = await client.GetBatteryAndStorageAsync();
```

## Запуск sample

Локальный self-test специально вставляет `MESSAGES_WAITING` между каждым запросом и ответом:

```bash
dotnet run --project samples/MeshCoreSharp.Console -- --self-test
```

Реальная нода:

```bash
dotnet run --project samples/MeshCoreSharp.Console -- <host> <port> [observeSeconds]
```

Например:

```bash
dotnet run --project samples/MeshCoreSharp.Console -- 192.168.1.100 5000 30
```

## Следующий этап

Следующая вертикаль — `GET_CONTACTS` как multi-frame transaction (`CONTACT_START -> CONTACT* -> CONTACT_END`). После неё — `MESSAGES_WAITING` / `SYNC_NEXT_MESSAGE` и message pump.

## Контекст для Codex / VS Code

Для продолжения разработки в Codex контекст проекта хранится прямо в репозитории:

- [`AGENTS.md`](AGENTS.md) — обязательные архитектурные и инженерные правила для coding agents;
- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — runtime-архитектура, transaction/router/gate модель;
- [`docs/COMPANION_PROTOCOL.md`](docs/COMPANION_PROTOCOL.md) — особенности Companion Protocol и известные расхождения источников;
- [`docs/ROADMAP.md`](docs/ROADMAP.md) — текущий статус и порядок дальнейшей реализации;
- [`docs/CODEX_START.md`](docs/CODEX_START.md) — готовый стартовый prompt для новой Codex-сессии.

Открывайте в VS Code корень репозитория, содержащий `MeshCoreSharp.sln` и `AGENTS.md`.
