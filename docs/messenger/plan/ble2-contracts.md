# BLE2 — Контракты BLE и управляемый fake backend

[Stage BLE](stage-ble.md) · [Оглавление](../../MESSENGER_PLAN.md)

**Статус: реализован 15.10.2026; Windows и portable проверки пройдены.**

**Цель:** Задать небольшой тестируемый каркас без реального подключения.

**Зависимости:** BLE1. Проверить результаты зависимостей перед началом.
Общие инварианты и решения — в [Stage BLE](stage-ble.md).

**Файлы/область:** `src/MeshCoreSharp/Transport/Ble/`, `src/MeshCoreSharp/Exceptions/`, существующий `tests/MeshCoreSharp.Tests`.
Новые имена/папки — предлагаемое место реализации; проверить актуальные исходники.

## Шаги

1. Добавить платформенно-нейтральные options/DTO: opaque device ID, ограничение времени открытия, размеры/лимиты очереди. UUID вынести в одно место.
2. Добавить internal backend/session abstraction только для необходимых GATT-операций и событий. WinRT-типы не должны попадать в общие сигнатуры.
3. Добавить fake backend: управляемые завершения discovery/open/write/subscribe, notifications, disconnect и запоздалые callbacks.
4. Определить typed причины ошибок: unsupported platform, adapter unavailable, pairing/access required, missing service, insufficient MTU, unreachable, transport failure. Сохранять исходный HRESULT/status без PIN/секретов.
5. Зафиксировать состояния Closed/Opening/Ready/Closing/Faulted, принадлежность ресурсов одной generation и единый путь cleanup. Каркас пока не регистрировать в production DI.

**Не входит:** Discovery Windows, готовое подключение, новый runtime/router.

## Проверки и завершение

**Тесты/проверки:** Валидация пустого endpoint/неположительных timeout/лимитов; fake позволяет воспроизводить завершения в заданном порядке без Bluetooth. Тесты включены в существующий runner, не только добавлены как файлы.

**Готово:** Следующие подэтапы могут проверять гонки через fake; состав публичного API минимален.

**Ручная проверка:** Не требуется.

- [x] Изменения в границах подэтапа выполнены.
- [x] Обязательные проверки выполнены; фактические результаты записаны ниже.
- [x] Статус подэтапа, checklist Stage BLE и current-state обновлены.

## Результат выполнения

15.10.2026: перед началом BLE2 изменения BLE1 проверены в staged diff и закоммичены
в `f0719fe`. Пользователь сообщил о подготовленной BT-ноде; аппаратные действия
для BLE2 не нужны и не выполнялись. BLE3 и следующие подэтапы не начаты.

### Изменения

- `Transport/Ble/BleMeshCoreTransportOptions`: opaque DeviceId, конечный положительный
  OpenTimeout (15 с default), ReceiveQueueCapacity (256 default), MaximumFrameSize
  (protocol baseline 176 bytes). Внутренняя валидация используется при создании
  connection attempt; пустой ID, бесконечный/слишком большой timeout и недопустимые
  лимиты отвергаются. ID не нормализуется как MAC или имя.
- `BleDeviceInfo` — public portable DTO кандидата с отдельным ID/display name,
  признаками paired и advertised service. Это не проверенная Companion identity.
- `BleGattProfile` содержит NUS service/RX/TX UUID и ATT value overhead в одном месте.
- `IBleBackend` и `IBleGattSession` — internal portable seam: bounded discovery,
  open, capabilities, MTU и его изменения, notifications/disconnect, CCCD enable/disable,
  запись полного logical frame через WriteWithResponse и асинхронное освобождение.
  WinRT-типов в сигнатурах нет. Отмена ожидания не означает завершение native I/O;
  backend освобождает частично открытые ресурсы при failure, успешный open передаёт
  владение вызывающему даже при запоздалом результате.
- `MeshCoreBleTransportException` содержит typed Reason, nullable NativeHResult,
  NativeStatus/ProtocolError и InnerException. NativeHResult также переносится в
  стандартное HResult. PIN/секреты не являются полями DTO/исключения; будущий backend
  обязан не включать их в сообщения. Причины включают unsupported, adapter unavailable,
  pairing/access, missing service/characteristic, insufficient MTU, unreachable и
  transport failure. Для наследования снят sealed с MeshCoreTransportException;
  nullable InnerException разрешён в двух базовых конструкторах, как в System.Exception.
  Существующий catch MeshCoreTransportException принимает BLE-ошибку.
- `BleConnectionAttempt` — internal учёт владения одной generation с состояниями
  Closed/Opening/Ready/Closing/Faulted. Повторный open требует новый экземпляр.
  Ready разрешён только явным переходом после будущей проверки CCCD; этот каркас
  сам не выполняет GATT. Close/Dispose используют одну cleanup Task и освобождают
  принятую сессию один раз. Запоздалый open результат отвергается и освобождается;
  ошибку cleanup можно наблюдать в Task, terminal state — Faulted.
- `tests/MeshCoreSharp.Tests/FakeBleBackend.cs`: управляемые pending operations
  discovery/open/subscribe/unsubscribe/write/dispose с RunContinuationsAsynchronously,
  сохранёнными CancellationToken, явным complete/fail/cancel и channels для ожидания
  начала операции. События notification/MTU/disconnect и сохранённые delegates
  позволяют воспроизвести callback после отписки. Fake находится только в tests.
- Семь BLE contract tests включены в существующий console harness через Program.cs.

### Проверки

Команды выполнены из корня на Windows, SDK 10.0.401:

| Команда / проверка | Результат |
| --- | --- |
| `dotnet build MeshCoreSharp.sln -c Release --no-restore` | Оба library targets и Windows consumers; 0 warnings/errors |
| `dotnet run --project tests/MeshCoreSharp.Tests -c Release --no-build` | **114/114**, включая семь новых BLE сценариев и Windows API smoke |
| `dotnet restore MeshCoreSharp.sln -p:McmWindowsDesktop=false --locked-mode` | Успешно |
| `dotnet build MeshCoreSharp.sln -c Release -p:McmWindowsDesktop=false --no-restore` | Portable consumers; 0 warnings/errors |
| `dotnet run --project tests/MeshCoreSharp.Tests -c Release -p:McmWindowsDesktop=false --no-build` | **113/113**, те же BLE контракты без WinRT |
| `dotnet restore MeshCoreSharp.sln --locked-mode` после portable проверки | Успешно; восстановлен Windows graph |
| Portable library deps.json | Нет WinRT/Windows SDK/WindowsAppSDK dependency |
| `git diff --check` | Успешно |

Проверены: validation/границы, native diagnostics, открытия двух endpoints с обратным
порядком завершения, одинаковые имена разных устройств, отмена discovery, notification
до завершения CCCD, копирование payload fake write, write после cancellation,
MTU/disconnect, callback после dispose, pending cleanup/повторный close, cleanup failure,
поздний успешный open после отмены ожидания и изоляция следующей generation.
Тесты задают порядок через pending tasks/channels, без Thread.Sleep/таймерных задержек.
Локальные логи: artifacts/ble2/library-windows.log и library-portable.log (не в Git).

### Границы и оставшееся

Реального Windows backend/сканирования, IMeshCoreTransport реализации и production DI
пока нет. State/ownership каркас не управляет очередью RX, подпиской, deadline или
конкурирующими native writes: эта интеграция и безопасное закрытие active I/O остаются
в BLE4–BLE7. Полные Core/Desktop suites повторно не запускались: runtime, БД и UI
не менялись; известные ограничения их Windows прогонов остаются в отчёте BLE1.
Сборка всех их проектов прошла. Native macOS и аппаратный BLE обмен не проверялись.
Следующий подэтап — **BLE3**, поиск устройств Windows; подготовленная нода пригодится
для проверки scan и последующего BLE8 probe.
