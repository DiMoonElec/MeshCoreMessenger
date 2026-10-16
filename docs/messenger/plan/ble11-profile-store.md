# BLE11 — Хранение BLE-профилей

[Stage BLE](stage-ble.md) · [Оглавление](../../MESSENGER_PLAN.md)

**Статус: реализован 16.10.2026.**

**Цель:** Сохранять и читать BLE endpoint существующим profile store.

**Зависимости:** BLE9, BLE10. Проверить результаты зависимостей перед началом.
Общие инварианты и решения — в [Stage BLE](stage-ble.md).

**Файлы/область:** `Core/Persistence/Sqlite/SqliteConnectionProfileStore.cs`, profile manager tests.
Новые имена/папки — предлагаемое место реализации; проверить актуальные исходники.

## Шаги

1. Обновить явные SELECT/INSERT/UPDATE и reader mapping для двух BLE-полей; исключить positional ошибки прежних столбцов.
2. Сохранить Save/SaveAndSelect, selected-profile setting и startup semantics. Сохранение профиля не создаёт соединение.
3. Протестировать переключение transport типа существующего профиля и очистку неактивных полей.
4. Сохранённый offline endpoint должен читаться на macOS тоже; платформа ограничивает подключение, не чтение истории/профиля.

**Не входит:** WinRT в Core, авто-поиск вместо сохранённого ID, подключение из store.

## Проверки и завершение

**Тесты/проверки:** Create/update/reopen/delete; TCP/Serial roundtrip; BLE → TCP → BLE; selected profile не меняется при простом Save; недоступное устройство; старые Sessions сохраняются.

**Готово:** Domain/store полностью поддерживают BLE на временной БД; hardware не требуется.

**Ручная проверка:** Не требуется.

- [x] Изменения в границах подэтапа выполнены.
- [x] Обязательные проверки выполнены; фактические результаты записаны ниже.
- [x] Статус подэтапа, checklist Stage BLE и current-state обновлены.

## Результат выполнения

16.10.2026: явные SELECT/INSERT/UPDATE и reader mapping SqliteConnectionProfileStore
расширены BleDeviceId/BleDeviceName. Новые столбцы добавлены в конец SELECT, индексы
прежних полей не сдвигались. Save/SaveAndSelect, Settings selection и lifecycle не
менялись. Устройства не ищутся, доступность endpoint при сохранении не проверяется;
WinRT dependencies в Core не добавлены. Профили читаются portable кодом на всех ОС.

Проверки: `dotnet run --project tests/MeshCoreMessenger.Core.Tests -c Release --
-class MeshCoreMessenger.Core.Tests.BleProfileStoreTests
-class MeshCoreMessenger.Core.Tests.BleProfileModelTests
-class MeshCoreMessenger.Core.Tests.ConnectionProfileTests
-class MeshCoreMessenger.Core.Tests.LocalStorageTests
-class MeshCoreMessenger.Core.Tests.BleProfileMigrationTests` — **57/57**.
Все три транспорта проходят сравнение каждого поля после reopen; проверены update,
nullable display name, одинаковые имена при разных ID, offline ID, BLE→TCP→BLE,
очистка полей, неизменность selection при Save, сохранность Session/Node связи и
отклонение invalid update без изменения сохранённого endpoint.

В существующем IConnectionProfileStore нет Delete API; новый CRUD-контракт вне scope
не добавлялся. Удаление неиспользуемого профиля и запрет удаления профиля со связанной
сессией проверены прямым SQL только на временном fixture.

Portable и Windows: `dotnet restore MeshCoreSharp.sln [ -p:McmWindowsDesktop=false ]
--locked-mode` и `dotnet build MeshCoreSharp.sln -c Release
[ -p:McmWindowsDesktop=false ] --no-restore` успешны, 0 warnings/errors.
Windows restore выполнен последним; lock files не менялись. Native macOS не проверялся.
Полный Core suite после BLE10 — 411/411; после store mapping выполнены указанные
targeted tests, полная Desktop suite не запускалась. GUI и реальные подключения не менялись.

Следующий подэтап — BLE12. Автоматическая последовательность 16.10.2026 остановлена
на границе application integration: BLE8 всё ещё не закрыт по реальному RX174–176.
Перед production включением проверить подготовленную тестовую очередь на ноде;
существующий backlog без отдельной подготовки не сливать. Основание и незавершённая
проверка — [BLE8](ble8-device-probe.md) и [актуальный отчёт](../../testing/ble-stock-firmware.md).
