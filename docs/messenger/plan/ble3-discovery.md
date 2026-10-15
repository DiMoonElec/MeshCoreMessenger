# BLE3 — Поиск BLE-устройств Windows

[Stage BLE](stage-ble.md) · [Оглавление](../../MESSENGER_PLAN.md)

**Статус: реализован 15.10.2026; fake/portable и Windows native scan проверены.**

**Цель:** Получать список кандидатов без подключения к ноде.

**Зависимости:** BLE2. Проверить результаты зависимостей перед началом.
Общие инварианты и решения — в [Stage BLE](stage-ble.md).

**Файлы/область:** `Transport/Ble/Windows/` внутри MeshCoreSharp, portable discovery DTO/contract, библиотечные tests.
Новые имена/папки — предлагаемое место реализации; проверить актуальные исходники.

## Шаги

1. Реализовать ограниченный по времени поиск через Windows enumeration/advertisement API; возвращать opaque ID для последующего открытия.
2. Основной признак кандидата — NUS service UUID. Имя использовать только для отображения. Обработать объединение advertisement/scan response; отсутствие UUID в одном пакете не доказывает отсутствие сервиса.
3. Позволить включить paired/ранее сохранённый endpoint, даже если он сейчас не рекламируется. Достоверность сервиса окончательно проверяется при GATT discovery.
4. Устранять дубли по endpoint, не по имени. При адресном discovery учитывать address type и преобразование в открываемый Windows ID.
5. Stop/cancel/timeout всегда останавливают watcher и отписывают обработчики; callback старого поиска не меняет новый результат. Возвращать отдельные статусы Bluetooth off/no adapter/access denied.

**Не входит:** Сопряжение, автоматический выбор первой ноды, UI, изменение профилей.

## Проверки и завершение

**Тесты/проверки:** Два одинаковых имени; дубли/обновления; scan response; cancel/restart и late callback; пустой список; adapter unavailable. Fake tests без радио.

**Готово:** Доступен самостоятельный cancellable discovery API; поиск ничего не подключает и не сохраняет.

**Ручная проверка:** Короткий scan допустим для проверки адаптера; имя/модель ноды неизвестны — не выбирать устройство автоматически.

- [x] Изменения в границах подэтапа выполнены.
- [x] Обязательные проверки выполнены; фактические результаты записаны ниже.
- [x] Статус подэтапа, checklist Stage BLE и current-state обновлены.

## Результат выполнения

15.10.2026: перед началом BLE3 по запросу пользователя разделены и созданы два
коммита: `708de12` (BLE2) и `24f1c19` (исправления Windows Core suite/SQLite cancellation).
В рабочем дереве после них выполнялся только BLE3. BLE4 не начат.

## Реализация

Public portable API — `BleDeviceDiscovery.FindAsync(BleDiscoveryOptions?, CancellationToken)`.
На Windows target он использует WindowsBleDiscoveryPlatform, в portable target
возвращает typed UnsupportedPlatform. Default ScanDuration — 5 с, LookupTimeout —
5 с: отдельные бюджеты для adapter check и финальных metadata lookups. Верхняя
граница ожидания — ScanDuration + два LookupTimeout, плюс синхронный cleanup.
Все значения валидируются; KnownDevices копируются перед началом поиска.

Internal IBleDiscoveryPlatform/IBleAdvertisementScanner позволяют проверять поиск
без Windows API. Каждая попытка владеет отдельным watcher, словарём и callbacks.
На завершении, cancellation и failure обработчики отписываются, watcher останавливается
и освобождается. Устаревшие callbacks проверяют локальное accepting состояние;
отмена ожидания metadata не выдаётся за отмену native операции, поздние faults наблюдаются.

Active BluetoothLEAdvertisementWatcher объединяет advertisement/scan response по
паре address + address type; имя и наличие NUS UUID накапливаются, отсутствие UUID
в следующем пакете не стирает ранее полученный признак. Одинаковые имена не объединяются.
Кандидаты по эфиру отбираются только по NUS service, имя не является фильтром.

Для перевода адреса в Windows ID используется системный typed address selector и
DeviceWatcher с запросом DeviceAddress/AddressType. Callback проверяет оба значения
и возвращает OS-provided DeviceInformation.Id, затем watcher останавливается и
все handlers снимаются. Windows ID не строится вручную, BluetoothLEDevice.FromIdAsync/
FromBluetoothAddressAsync и GATT discovery не вызываются.

Сохранённые KnownDevices остаются видимыми при отсутствии рекламы; старые флаги
advertising/paired не выдаются за текущие. Опциональный список paired endpoints
получается через Windows enumeration без соединения. Эти устройства могут не иметь
NUS и остаются непроверенными кандидатами: окончательная проверка сервиса — BLE4.
После получения ID результаты объединяются по opaque ID, не по имени. Не найденный
или неразрешённый OS endpoint не подменяется сконструированным адресом.

Добавлена причина RadioOff отдельно от AdapterUnavailable и AccessDenied. Она добавлена
в конец enum, прежние numeric values сохранены. Windows backend сохраняет HRESULT
или BluetoothError status. Adapter check не меняет Radio.State и не вызывает pairing/
consent dialog. Production DI, connection profile, UI, Core/runtime и БД не менялись.

## Проверки

| Проверка | Результат |
| --- | --- |
| Windows `dotnet build MeshCoreSharp.sln -c Release --no-restore` | 0 warnings/errors |
| `dotnet run --project tests/MeshCoreSharp.Tests -c Release --no-build` | **122/122**, восемь новых discovery cases |
| Portable `dotnet restore MeshCoreSharp.sln -p:McmWindowsDesktop=false --locked-mode` | Успешно |
| Portable `dotnet build MeshCoreSharp.sln -c Release -p:McmWindowsDesktop=false --no-restore` | 0 warnings/errors |
| `dotnet run --project tests/MeshCoreSharp.Tests -c Release -p:McmWindowsDesktop=false --no-build` | **121/121**, включая UnsupportedPlatform |
| Windows `dotnet restore MeshCoreSharp.sln --locked-mode` после portable проверки | Успешно |
| Native `dotnet run --project samples/MeshCoreSharp.Console -c Release --no-build -- --list-ble 3` | Найден один NUS-кандидат; около 4,3 с вместе с запуском dotnet |

Fake tests проверяют merge scan response, два одинаковых имени, одинаковое числовое
значение public/random адресов, missing UUID в позднем пакете, сохранённый offline
endpoint, paired refresh и отключение paired enumeration, empty/unresolved results,
cancel/restart и late callbacks, adapter off/no adapter/access denied, start failure,
неожиданный Stop, metadata timeout и late completion. Порядок контролируется fake
clock/tasks/channels без Thread.Sleep; tests включены в существующий harness.

Native scan на Realtek адаптере выполнен после fake tests. Обнаружен
**MeshCore-RnD CatCore🐈**, NUS=true, paired=false, получен opaque ID Windows.
Финальный scan успешно повторён. Это обнаружение кандидата, не подтверждение полного
публичного ключа/firmware или GATT связи. Подключение, сопряжение, извлечение message
backlog, RF-передачи и конфигурационные команды не выполнялись.
Пользователь подтвердил корректное обнаружение своей ноды и запросил коммит BLE3.

Подробности native проверки — [отчёт BLE3](../../testing/ble3-discovery.md).
Локальные логи: artifacts/ble3/library-windows.log, library-portable.log,
native-scan-final.log. Они игнорируются Git. Pins, lock files и global.json не менялись.
Полные Core/Desktop suites повторно не запускались; Core 389/389 зафиксирован отдельно
в коммите 24f1c19. Ограничение Desktop UI suite из того отчёта сохраняется.

## Команда проверки

```powershell
dotnet run --project samples/MeshCoreSharp.Console -c Release -- --list-ble 5
```

Аргумент — длительность сканирования 1–60 с (default 5). Ctrl+C отменяет поиск.
Команда выводит name/paired/NUS и Windows ID; устройства автоматически не выбираются.
В Desktop BLE пока не включён. Следующий подэтап — **BLE4**, открытие GATT и проверка
характеристик; модель/firmware/целевой endpoint необходимо установить до подключения.
