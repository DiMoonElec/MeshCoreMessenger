# MeshCoreSharp

Начальная реализация C# библиотеки для MeshCore Companion Protocol.

## Структура

- `src/MeshCoreSharp` — единственный библиотечный проект; результат сборки `MeshCoreSharp.dll`.
- `samples/MeshCoreSharp.Console` — TCP/Serial-клиент для проверки на реальном companion.
- `tests/MeshCoreSharp.Tests` — исполняемые регрессионные тесты без сторонних тестовых библиотек.

Внутри DLL код разделён namespace-ами:

- `MeshCoreSharp` — публичный high-level клиент.
- `MeshCoreSharp.Models` — публичные модели.
- `MeshCoreSharp.Protocol` — типы протокола.
- `MeshCoreSharp.Protocol.Packets` — decoded companion packets.
- `MeshCoreSharp.Transport` — абстракция транспорта.
- `MeshCoreSharp.Transport.Tcp` — TCP transport.
- `MeshCoreSharp.Transport.Serial` — Serial transport через `System.IO.Ports`.
- `MeshCoreSharp.Runtime` — internal command/router runtime.

## Реализовано

- TCP и Serial подключение.
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
  - `CHANNEL_INFO`, `STATS` (core/radio/packets).
  - `MSG_SENT`, `ACK`.
  - `ADVERT`, `NEW_ADVERT`.
  - `CONTACT_START`, `CONTACT`, `CONTACT_END`.
  - Личные и канальные сообщения legacy/V3, канальные данные и `NO_MORE_MESSAGES`.
- Raw packet fallback для пока не реализованных типов, включая push packets.
- Последовательный `CommandDispatcher`.
- Subscribe-before-send: transaction регистрируется в `PacketRouter` до записи команды.
- Push packets могут приходить между command и response и не завершают transaction, если их тип не соответствует ожидаемому ответу.
- Автоматическое и явное чтение входящей очереди через `SYNC_NEXT_MESSAGE` и событие `MessageReceived`.
- Public API:
  - `ConnectAsync()`;
  - `StartAsync()`;
  - `GetDeviceTimeAsync()`;
  - `SetDeviceTimeAsync()`;
  - `GetDeviceInfoAsync()`;
  - `GetBatteryAndStorageAsync()`;
  - `GetContactsAsync()`;
  - `AddOrUpdateContactAsync(...)`, `RemoveContactAsync(...)`;
  - `DrainMessagesAsync()`;
  - `GetChannelAsync(index)`, `GetChannelsAsync()`;
  - `SetChannelAsync(index, name, secret)`, `SetHashtagChannelAsync(index, name)`, `ClearChannelAsync(index)`;
  - `GetCoreStatsAsync()`, `GetRadioStatsAsync()`, `GetPacketStatsAsync()`;
  - `SendTextAsync(publicKey, text)`, `SendChannelTextAsync(channelIndex, text)`;
  - `SendAdvertisementAsync(mode)` и событие `AdvertisementReceived`;
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

Контакт из `NEW_ADVERT` можно сохранить без ручной сборки полей протокола:

```csharp
// Полученную в обработчике события модель передайте в очередь приложения,
// затем сохраните вне callback:
if (advertisement.DiscoveredContact is not null)
    await client.AddOrUpdateContactAsync(advertisement, ct);

await client.RemoveContactAsync(contact.PublicKey, ct);
```

`AddOrUpdateContactAsync` также принимает `Contact` или отдельный
`ContactConfiguration` для явного изменения имени, флагов, пути и данных адверта.
Операции завершаются по локальному `OK/ERROR`; после изменения приложение может
перечитать `GetContactsAsync`. Удаление требует полный 32-байтовый публичный ключ.

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

## Каналы и статистика

После `ConnectAsync` и `StartAsync`:

```csharp
var channels = await client.GetChannelsAsync(ct);
foreach (var channel in channels.Where(channel => !channel.IsEmpty))
    Console.WriteLine($"{channel.Index}: {channel.Name}");

var core = await client.GetCoreStatsAsync(ct);
var radio = await client.GetRadioStatsAsync(ct);
var packets = await client.GetPacketStatsAsync(ct);
Console.WriteLine($"Uptime: {core.UptimeSeconds} s; RX: {packets.Received}; TX: {packets.Sent}");
```

`GetChannelAsync(byte index, CancellationToken)` возвращает один слот.
`GetChannelsAsync(CancellationToken)` запрашивает ёмкость через `DEVICE_QUERY`
и читает все слоты по порядку, включая пустые. Пустое имя означает `IsEmpty`;
наличие пустого слота не прекращает обход. Это последовательность чтений,
не атомарный снимок. Для старой прошивки без `MaxChannels` метод выбрасывает
`NotSupportedException`; отдельные слоты можно запрашивать явно.

`ChannelInfo.Secret` содержит 16 байт ключа. `ToString()` не выводит ключ,
но низкоуровневый `ChannelInfoPacket.RawFrame` его содержит — не журналируйте
такой кадр целиком.

Настройка канала использует тот же последовательный dispatcher и завершается по
`OK/ERROR`. Базовый метод принимает готовый 16-байтовый ключ. Для открытого
hashtag-канала достаточно передать точное имя с `#`:

```csharp
var empty = (await client.GetChannelsAsync(ct)).First(channel => channel.IsEmpty);
await client.SetHashtagChannelAsync(empty.Index, "#mcs-dev-test", ct);

// При необходимости слот освобождается отдельной операцией:
await client.ClearChannelAsync(empty.Index, ct);
```

`ChannelSecrets.DeriveHashtag(name)` вычисляет первые 16 байт SHA-256 от UTF-8
имени вместе с `#`, как штатные клиенты MeshCore. Имя хешируется буквально:
регистр и пробелы значимы. Такой канал доступен каждому, кто знает или угадает имя,
поэтому секрет hashtag-канала не обеспечивает приватность. Для приватного канала
приложение должно сгенерировать случайный 16-байтовый ключ и передать его в
`SetChannelAsync`. Имя ограничено 31 байтом UTF-8, чтобы оно вместе с NUL помещалось
в 32-байтовую строку прошивки. Очистка записывает пустое имя и нулевой ключ.

Статистика доступна в прошивках с протоколом v8+ и возвращается тремя моделями:
`CoreStats` (батарея, uptime, флаги ошибок, очередь), `RadioStats` (шум, RSSI, SNR,
эфирное время в целых секундах), `PacketStats` (TX/RX, flood/direct, ошибки RX).
Счётчики могут переполниться или сброситься после перезагрузки. Неизвестные подтипы
статистики сохраняются как raw-пакеты. Команды используют общую очередь клиента;
ошибки устройства, отмена и тайм-ауты передаются вызывающему коду.

## Отправка текста

Личное сообщение отправляется один раз. `SendTextAsync` принимает модель `Contact`
или полный публичный ключ контакта (32 байта) и возвращает результат после
`MSG_SENT` от Companion.
Подтверждение доставки ожидается отдельно и не занимает очередь команд:

```csharp
var sent = await client.SendTextAsync(contact, "Привет!", ct);
Console.WriteLine($"Принято нодой, flood={sent.Accepted.IsFlood}");

var delivery = await sent.Delivery;
if (delivery.Status == MessageDeliveryStatus.Confirmed)
    Console.WriteLine($"Доставлено, RTT={delivery.Acknowledgement!.RoundTripTimeMilliseconds} мс");
else
    Console.WriteLine($"Подтверждения нет: {delivery.Status}");
```

Статус `TimedOut` означает отсутствие ACK за отведённое время, а не доказанную
потерю сообщения. `NotExpected` означает, что прошивка вернула нулевой ACK.
Токен отправки действует и на `Delivery`: отмена завершает ожидание с
`OperationCanceledException`, потеря соединения — с `MeshCoreTransportException`.
Отмена после записи команды не отменяет радиопередачу. Ошибка/тайм-аут непосредственного
ответа также не гарантирует, что передача не состоялась. Автоматических повторов нет.

Время ожидания ACK: предложение прошивки плюс `AckTimeoutMargin` (2 с), ограниченное
`MinimumAckTimeout` (1 с) и `MaximumAckTimeout` (2 мин). Ожидание начинается от
`MSG_SENT`. Новые личные отправки могут ждать освобождения кольцевой таблицы ACK
прошивки (8 записей); чтение состояния и другие локальные команды продолжают работать.
Трекер создаётся заново при подключении. ACK остаются доступны в событиях пакетов.
Подтверждение сопоставляется по четырёхбайтовому тегу из `MSG_SENT`; его RTT берётся
из пакета ACK. Коллизия одновременно активных тегов завершает обе неоднозначные
операции ошибкой протокола вместо ложного подтверждения.

Отправка в канал завершается по локальному `OK`; подтверждения доставки у неё нет:

```csharp
var channel = (await client.GetChannelsAsync(ct)).Single(c => c.Name == "#test");
var accepted = await client.SendChannelTextAsync(channel.Index, "Тест разработки", ct);
```

Текст должен быть непустым, корректным UTF-8, без NUL. Лимит личного сообщения —
160 байт UTF-8; у канального сообщения из этого лимита вычитается имя отправителя
и `": "`. Превышение лимита отклоняется до отправки, без обрезания текста.
Имя берётся из `SELF_INFO`; после изменения имени другим приложением повторите
`StartAsync`. Метки времени генерируются по часам ПК и возрастают для каждого
вызова на данном клиенте, чтобы одинаковые тексты не получали одинаковый ACK.
Прошивке передаются первые 6 байт ключа получателя; неоднозначность такого префикса
в её таблице контактов библиотека устранить не может.

## Адверты

```csharp
client.AdvertisementReceived += (_, e) =>
    Console.WriteLine($"Advert: {e.Advertisement.PublicKeyHex}, {e.Advertisement.DiscoveredContact?.Name}");

await client.SendAdvertisementAsync(AdvertisementMode.Flood, ct);
```

`ZeroHop` (по умолчанию) отправляет адверт соседним нодам без пересылки;
`Flood` разрешает пересылку с учётом настроенного на ноде flood scope.
Метод завершается по локальному `OK`, который не подтверждает приём другими нодами.
Используются существующие имя и политика публикации координат, настройки не меняются.
Автоматической периодической отправки и повторов нет.

`AdvertisementReceived` приходит для обоих уведомлений прошивки: `ADVERT` содержит
только публичный ключ, `NEW_ADVERT` — полные данные в `DiscoveredContact`.
`IsNew` не гарантирует сохранение контакта в таблицу: это зависит от настроек ноды.
Библиотека не добавляет контакт и не запрашивает его данные автоматически.
Адверты остаются доступны и через события пакетов; во время `GetContactsAsync`
они не включаются в возвращаемый список и не продлевают тайм-аут его чтения.

## Подключение через Serial

Тот же `MeshCoreClient` работает с последовательным портом:

```csharp
using MeshCoreSharp;
using MeshCoreSharp.Transport.Serial;

await using var client = new MeshCoreClient(
    new SerialMeshCoreTransport("/dev/cu.usbmodemXXXX", 115200));

client.MessageReceived += (_, e) => Console.WriteLine(e.Message);
await client.ConnectAsync();
var self = await client.StartAsync();
var contacts = await client.GetContactsAsync();
```

В Windows имя порта обычно имеет вид `COM3`, в Linux — `/dev/ttyACM0` или
`/dev/ttyUSB0`, в macOS — `/dev/cu.usbmodem…` или `/dev/cu.usbserial…`.
`SerialMeshCoreTransport.GetPortNames()` возвращает доступные имена без открытия устройств.

Параметры по умолчанию: 115200 бод, 8N1, без flow control, DTR и RTS включены,
задержка после открытия 200 мс. На ESP32 обе активные линии являются безопасной
парой для стандартной двухтранзисторной схемы автосброса. `System.IO.Ports`
применяет линии последовательно после открытия; пара `false/false` может создать
краткий импульс сброса. Для платы с другой разводкой задайте её проверенные уровни.
Скорость, сигнальные линии и задержку можно задать:

```csharp
var transport = new SerialMeshCoreTransport(new SerialMeshCoreTransportOptions
{
    PortName = "COM3",
    BaudRate = 115200,
    DtrEnable = true,
    RtsEnable = true,
    OpenDelay = TimeSpan.FromSeconds(1),
});
```

DTR/RTS и задержка зависят от платы: некоторые USB-UART адаптеры используют эти
линии для сброса. Значение `true` означает активный сигнал, физически это обычно
низкий уровень. `ReadTimeout` (100 мс) задаёт интервал ожидания данных в RX-потоке,
а не время ожидания ответа команды. Истечение этого тайм-аута не разрывает соединение.
`WriteTimeout` (1000 мс) ограничивает запись. Ошибка записи, включая тайм-аут,
закрывает соединение, поскольку кадр мог быть отправлен частично.

Отмена ожидающей записи срабатывает сразу. Уже начавшийся синхронный вызов драйвера
завершается до обработки отмены; `Open` также не прерывается внутри ОС. Отключение
дожидается завершения текущего чтения/записи и освобождает порт. После него можно
явно подключиться снова; автоматического переподключения нет.

Весь код библиотеки остаётся в `MeshCoreSharp.dll`. Serial использует официальный
NuGet-пакет `System.IO.Ports` 10.0.12 с нативными компонентами для ОС; зависимости
попадают в приложение при обычной сборке/публикации .NET. Проверено на macOS arm64
через псевдотерминал и на физической USB-ноде Heltec V3 с прошивкой v1.17.1
(локальные команды чтения, без радиопередач). Другие ОС пока не проверялись.

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
Следующее уведомление запускает новый проход после восстанавливаемой ошибки;
бесконечных автоматических повторов нет. После тайм-аута требуется переподключение:
у `SYNC_NEXT_MESSAGE` нет ID запроса, поэтому его поздний ответ нельзя безопасно
отличить от ответа повторной команды.
Запоздалый корректный пакет сообщения всё равно вызывает `MessageReceived`.
При отключении фоновое чтение отменяется, при следующем подключении и `StartAsync`
создаётся новый приёмник. Автоматического переподключения нет.

Для диагностических приложений автоматические запросы можно отключить через
`new MeshCoreClientOptions { AutoReceiveMessages = false }`. Очередь при этом можно
прочитать явно; параллельные вызовы присоединяются к одному проходу:

```csharp
await client.DrainMessagesAsync(ct); // сообщения приходят через MessageReceived
```

Отмена одного ожидающего вызова не отменяет общий проход и других вызывающих.
Успешное завершение означает, что Companion ответил `NO_MORE_MESSAGES`; обработчики
`MessageReceived` выполняются в отдельной очереди и могут завершиться позднее.
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

Список последовательных портов и подключение к выбранному:

```bash
dotnet run --project samples/MeshCoreSharp.Console -- --list-ports
dotnet run --project samples/MeshCoreSharp.Console -- --serial /dev/cu.usbmodemXXXX 115200 30
```

Формат аргументов: `--serial <portName> [baudRate=115200] [observeSeconds=10]`.

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

Serial-тесты дополнительно проверяют фрагментацию и объединение кадров, мусор в
потоке, отмену, ошибки открытия/чтения/записи, повторное подключение и освобождение порта.
Проверка нативного `System.IO.Ports` на macOS/Linux через временный псевдотерминал:

```bash
python3 tests/MeshCoreSharp.Tests/serial_pty_smoke.py
```

Для неё нужна выполненная Release-сборка и Python 3. Физический Serial-порт не открывается.

Ручное чтение очереди подключённой ноды при `AutoReceiveMessages=false`:

```bash
dotnet run --project tests/MeshCoreSharp.Tests -c Release -- --serial-message-drain-test /dev/cu.usbserial-0001
dotnet run --project tests/MeshCoreSharp.Tests -c Release -- --serial-message-drain-live-test /dev/cu.usbserial-0001
```

Обёртка разрешает только `APP_START` и `SYNC_NEXT_MESSAGE`: радиопередача и изменение
настроек блокируются. Первый вариант читает накопленную очередь и завершается. Live-вариант
после начального прохода ждёт `MESSAGES_WAITING` до 60 секунд, затем явно читает новую
очередь. Обе проверки удаляют полученные сообщения из очереди Companion.

Проверка отсутствия сброса при двух последовательных открытиях Serial:

```bash
dotnet run --project tests/MeshCoreSharp.Tests -c Release -- --serial-no-reset-test /dev/cu.usbserial-0001
```

Она разрешает только `APP_START` и `GET_STATS(core)`, не читает очередь сообщений и
проверяет, что uptime ноды продолжает расти после закрытия и повторного открытия порта.

Отдельная проверка подключённой USB-ноды только локальными командами чтения:

```bash
dotnet run --project tests/MeshCoreSharp.Tests -c Release -- --serial-read-only /dev/cu.usbserial-0001
```

Тест разрешает только фиксированные `APP_START`, `DEVICE_QUERY`, `GET_DEVICE_TIME`,
`GET_BATT_AND_STORAGE`, `GET_CONTACTS`, `GET_CHANNEL` и `GET_STATS` (все три группы).
Он использует публичный `MeshCoreClient`, выводит имена каналов без ключей.
Автоматический приём сообщений не запускается; DTR/RTS включены, чтобы не сбрасывать
ESP32 при открытии порта. Счётчики
радиопередач сравниваются после `APP_START` и в конце проверки. Нужна прошивка с поддержкой
`GET_STATS`; сам тест не отключает возможные автономные передачи прошивки.
Результат аппаратной проверки: [Heltec V3, 24.09.2026](docs/testing/serial-usb-2026-09-24.md).

Отдельный тест **с реальной передачей** ровно одного фиксированного сообщения
`Тестовая отправка при разработке MeshCoreSharp. Ответ не требуется` в канал `#test`:

```bash
dotnet run --project tests/MeshCoreSharp.Tests -c Release -- --serial-send-test /dev/cu.usbserial-0001
```

Канал выбирается по точному имени. Тест не меняет настройки, запрещает повторную
передачу и проверяет увеличение счётчика TX. При неопределённом результате сам
не повторяет отправку. [Результат проверки на Heltec V3](docs/testing/serial-send-2026-09-24.md).

Тест **с передачей одного Flood-адверта и ожиданием личного сообщения до 10 минут**:

```bash
dotnet run --project tests/MeshCoreSharp.Tests -c Release -- --serial-advert-test /dev/cu.usbserial-0001
```

Сначала он читает накопленную очередь, затем отправляет адверт и ожидает новое
личное сообщение через `MessageReceived`. Сам тест не отправляет текстовых ответов;
прошивка может автоматически передавать ACK и служебные пакеты при приёме.
Повторный запуск передаст новый адверт. [Аппаратная проверка](docs/testing/serial-advert-2026-09-24.md).

Аппаратный тест личной отправки конкретному контакту с проверкой `MSG_SENT`, ACK
и счётчиков TX:

```bash
dotnet run --project tests/MeshCoreSharp.Tests -c Release -- --serial-private-send-test /dev/cu.usbserial-0001
```

Тест находит ровно один контакт с префиксом `DF73015A6BB9`, отправляет фиксированный
текст один раз и ожидает совпадающий ACK. Если контакт отсутствует или префикс
неоднозначен, передача не выполняется. При тайм-ауте команда не повторяется.
[Результат аппаратной проверки](docs/testing/serial-private-send-2026-09-25.md).

Аппаратная проверка настройки hashtag-канала, очистки слота и одной передачи:

```bash
dotnet run --project tests/MeshCoreSharp.Tests -c Release -- --serial-channel-config-test /dev/cu.usbserial-0001
```

Тест изменяет только слот, который предварительно прочитан как пустой с нулевым
ключом. Он проверяет запись и очистку чтением обратно, оставляет `#mcs-dev-test`
настроенным и разрешает ровно одну помеченную тестовую передачу.
[Результат аппаратной проверки](docs/testing/serial-channel-config-2026-09-25.md).

Локальный аппаратный CRUD-тест контактов без радиопередач:

```bash
dotnet run --project tests/MeshCoreSharp.Tests -c Release -- --serial-contact-mutation-test /dev/cu.usbserial-0001
```

Он добавляет отдельный временный контакт, проверяет чтение, обновляет его, снова
проверяет поля и удаляет. Обёртка запрещает любые другие изменения и RF-команды.
[Результат аппаратной проверки](docs/testing/serial-contact-mutation-2026-09-25.md).

Проверка удаления существующего контакта, восстановления всех его полей и одной
личной отправки с ожиданием ACK:

```bash
dotnet run --project tests/MeshCoreSharp.Tests -c Release -- --serial-contact-restore-send-test /dev/cu.usbserial-0001
```

Тест разрешает операции только для `RnD Mesh01` с ожидаемым префиксом ключа,
восстанавливает контакт из снимка и отправляет ровно одно помеченное тестовое
сообщение. Получение сообщения подтверждено владельцем второй ноды.
[Результат аппаратной проверки](docs/testing/serial-contact-restore-send-2026-09-25.md).

## Следующий этап

Создан каркас настольного мессенджера на Avalonia для macOS/Windows. Подэтап A1
включает проекты Core/Desktop/Tests, зафиксированные пакеты и SDK, DI/logging и
пустое рабочее окно. БД, подключения и сообщения пока не реализованы.

- [Архитектура приложения](docs/MESSENGER_ARCHITECTURE.md) — компоненты, БД,
  идентичность чатов, reconnect, сохранность, UX и диагностика.
- [План реализации](docs/MESSENGER_PLAN.md) — этапы A–F и критерии приёмки для ИИ-агентов.

Запуск каркаса:

```bash
dotnet run --project src/MeshCoreMessenger.Desktop/MeshCoreMessenger.Desktop.csproj
```

Следующая часть этапа A — пути данных, SQLite и барьер доставки событий библиотеки
для корректного закрытия сессии. Удалённые запросы статуса/телеметрии остаются
отдельным расширением и не требуются для базового общения.

## Контекст для Codex / VS Code

Для продолжения разработки в Codex контекст проекта хранится прямо в репозитории:

- [`AGENTS.md`](AGENTS.md) — обязательные архитектурные и инженерные правила для coding agents;
- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — runtime-архитектура, transaction/router/gate модель;
- [`docs/COMPANION_PROTOCOL.md`](docs/COMPANION_PROTOCOL.md) — особенности Companion Protocol и известные расхождения источников;
- [`docs/ROADMAP.md`](docs/ROADMAP.md) — текущий статус и порядок дальнейшей реализации;
- [`docs/MESSENGER_ARCHITECTURE.md`](docs/MESSENGER_ARCHITECTURE.md) и [`docs/MESSENGER_PLAN.md`](docs/MESSENGER_PLAN.md) — проектирование настольного мессенджера;
- [`docs/CODEX_START.md`](docs/CODEX_START.md) — готовый стартовый prompt для новой Codex-сессии.

Открывайте в VS Code корень репозитория, содержащий `MeshCoreSharp.sln` и `AGENTS.md`.
