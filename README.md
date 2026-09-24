# MeshCoreSharp

Начальная реализация C# библиотеки для MeshCore Companion Protocol.

## Структура

- `src/MeshCoreSharp` — единственный библиотечный проект; результат сборки `MeshCoreSharp.dll`.
- `samples/MeshCoreSharp.Console` — простой TCP-клиент для проверки на реальном companion.
- `tests/MeshCoreSharp.Tests` — исполняемые регрессионные тесты без сторонних зависимостей.

Внутри DLL код разделён namespace-ами:

- `MeshCoreSharp` — публичный high-level клиент.
- `MeshCoreSharp.Models` — публичные модели.
- `MeshCoreSharp.Protocol` — типы протокола.
- `MeshCoreSharp.Protocol.Packets` — decoded companion packets.
- `MeshCoreSharp.Transport` — абстракция транспорта.
- `MeshCoreSharp.Transport.Tcp` — TCP transport.
- `MeshCoreSharp.Runtime` — internal command/router runtime.

## Реализовано

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
  - `CONTACT_START`, `CONTACT`, `CONTACT_END`.
  - Личные и канальные сообщения legacy/V3, канальные данные и `NO_MORE_MESSAGES`.
- Raw packet fallback для пока не реализованных типов, включая push packets.
- Последовательный `CommandDispatcher`.
- Subscribe-before-send: transaction регистрируется в `PacketRouter` до записи команды.
- Push packets могут приходить между command и response и не завершают transaction, если их тип не соответствует ожидаемому ответу.
- Автоматическое чтение входящей очереди через `SYNC_NEXT_MESSAGE` и событие `MessageReceived`.
- Public API:
  - `ConnectAsync()`;
  - `StartAsync()`;
  - `GetDeviceTimeAsync()`;
  - `SetDeviceTimeAsync()`;
  - `GetDeviceInfoAsync()`;
  - `GetBatteryAndStorageAsync()`;
  - `GetContactsAsync()`;
  - `DisconnectAsync()`.

## Пример

```csharp
using MeshCoreSharp;
using MeshCoreSharp.Models;
using MeshCoreSharp.Transport.Tcp;

await using var transport = new TcpMeshCoreTransport("192.168.1.100", 5000);
await using var client = new MeshCoreClient(transport);

client.PushPacketReceived += (_, e) =>
    Console.WriteLine($"Push: 0x{e.Packet.RawType:X2}");

// Подписывайтесь до StartAsync: на устройстве уже могут быть накопленные сообщения.
client.MessageReceived += (_, e) =>
{
    switch (e.Message)
    {
        case ContactMessage message:
            Console.WriteLine($"Личное от {message.ContactPublicKeyPrefixHex}: {message.Text}");
            break;
        case ChannelMessage message:
            Console.WriteLine($"Канал {message.ChannelIndex}: {message.Text}");
            break;
        case ChannelDataMessage message:
            Console.WriteLine($"Данные канала {message.ChannelIndex}: {Convert.ToHexString(message.Data.Span)}");
            break;
    }
};

await client.ConnectAsync();

var self = await client.StartAsync();
var info = await client.GetDeviceInfoAsync();
var time = await client.GetDeviceTimeAsync();
var battery = await client.GetBatteryAndStorageAsync();
var contacts = await client.GetContactsAsync();
foreach (var contact in contacts)
    Console.WriteLine($"{contact.Name}: {contact.PublicKeyHex}");
```

`GetContactsAsync(CancellationToken)` возвращает `IReadOnlyList<Contact>` после
`CONTACT_END`. Во время получения списка остальные команды ожидают своей очереди,
а push-пакеты продолжают поступать в события. Пустой список также завершается
только по `CONTACT_END`; счётчик в `CONTACT_START` не используется как условие завершения.

В `MeshCoreClientOptions` доступны `ContactsInactivityTimeout` (по умолчанию 5 секунд
между корректными кадрами списка) и `ContactsAbsoluteTimeout` (2 минуты на обмен,
без времени ожидания в очереди). Посторонние push-пакеты не продлевают тайм-аут.
При отмене, тайм-ауте, ошибке протокола или разрыве соединения частичный список
не возвращается; очередь команд освобождается.

`Contact` содержит публичный ключ, тип узла, исходные флаги, имя, координаты и
две временные метки Unix. `OutPathLength` сохраняет кодированный байт длины пути
(`0xFF` — путь неизвестен), `OutPath` — все 64 байта поля пути, включая заполнение.

События клиента вызываются последовательно в фоновой очереди. Их обработчики
не блокируют RX loop; завершение команды может опередить соответствующее событие.
Обработчики должны завершаться своевременно, чтобы не задерживать следующие события.
`DisposeAsync` закрывает очередь событий, но не ждёт выполнения пользовательского кода;
уже поставленные в очередь события могут быть доставлены после его завершения.

## Приём сообщений

По умолчанию `AutoReceiveMessages = true`. После успешного `StartAsync` клиент
читает накопленные сообщения, затем запускает чтение по `MESSAGES_WAITING`.
Каждый `SYNC_NEXT_MESSAGE` возвращает одно сообщение; чтение продолжается до
`NO_MORE_MESSAGES`. Повторные уведомления объединяются. Обычные команды получают
доступ к диспетчеру между запросами сообщений, а получение контактов удерживает
его до конца списка. RX loop и обработчики событий работают независимо.

`MessageReceived` передаёт один из трёх типов:

- `ContactMessage`: шестибайтный префикс ключа контакта, тип текста, время и текст.
  Для room-постов `SignedPlain` дополнительно сохраняется четырёхбайтный `SenderPrefix` автора.
- `ChannelMessage`: индекс канала, время, тип и текст, включая префикс имени отправителя.
- `ChannelDataMessage`: индекс канала, `DataType` и исходные байты `Data`; времени в этом пакете нет.

`SnrDb` заполнен для V3 и канальных данных, для legacy равен `null`.
`PathLength` сохраняет кодированный байт протокола; `0xFF` означает прямую доставку.
Текст сохраняет пробелы и переводы строк. Полного публичного ключа отправителя
в сообщении нет: сопоставление префикса со списком контактов выполняет приложение.

Ошибки команды и тайм-ауты (`CommandTimeout`) поступают в `BackgroundError`.
Ошибочный кадр также вызывает диагностику и останавливает текущий проход.
Следующее уведомление запускает новый проход; бесконечных автоматических повторов нет.
Запоздалый корректный пакет сообщения всё равно вызывает `MessageReceived`.
При отключении фоновое чтение отменяется, при следующем подключении и `StartAsync`
создаётся новый приёмник. Автоматического переподключения нет.

Для диагностических приложений автоматические запросы можно отключить через
`new MeshCoreClientOptions { AutoReceiveMessages = false }`.
Сообщения из очереди удаляются прошивкой при чтении; библиотека не сохраняет их
на диск и не выполняет дедупликацию. Подпишитесь на событие до запуска приёма.

## Запуск sample

Локальный self-test вставляет `MESSAGES_WAITING` в обычные ответы и между контактами,
а также проверяет получение личного сообщения, канального текста и бинарных данных:

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

## Проверки

```bash
dotnet build MeshCoreSharp.sln -c Release
dotnet run --project tests/MeshCoreSharp.Tests -c Release
dotnet run --project samples/MeshCoreSharp.Console -c Release -- --self-test
```

Тестовый проект запускается через `dotnet run` и возвращает ненулевой код при ошибке.
Он проверяет разбор полей и усечённых кадров, push-пакеты между контактами, порядок
кадров, оба тайм-аута, отмену, ошибки и освобождение очереди, все форматы сообщений,
объединение уведомлений и отключение/перезапуск приёмника. TCP self-test получает
два контакта и три сообщения.

## Следующий этап

Следующая вертикаль — отправка текста (`SEND_TXT_MSG` / `MSG_SENT`) и отслеживание `ACK`.

## Контекст для Codex / VS Code

Для продолжения разработки в Codex контекст проекта хранится прямо в репозитории:

- [`AGENTS.md`](AGENTS.md) — обязательные архитектурные и инженерные правила для coding agents;
- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — runtime-архитектура, transaction/router/gate модель;
- [`docs/COMPANION_PROTOCOL.md`](docs/COMPANION_PROTOCOL.md) — особенности Companion Protocol и известные расхождения источников;
- [`docs/ROADMAP.md`](docs/ROADMAP.md) — текущий статус и порядок дальнейшей реализации;
- [`docs/CODEX_START.md`](docs/CODEX_START.md) — готовый стартовый prompt для новой Codex-сессии.

Открывайте в VS Code корень репозитория, содержащий `MeshCoreSharp.sln` и `AGENTS.md`.
