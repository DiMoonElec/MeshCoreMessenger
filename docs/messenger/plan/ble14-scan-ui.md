# BLE14 — Поиск устройств в модели представления

[Stage BLE](stage-ble.md) · [Оглавление](../../MESSENGER_PLAN.md)

**Статус: реализован 16.10.2026; видимое подключение к editor — BLE15.**

**Цель:** Связать discovery с UI lifecycle без добавления сложного редактора.

**Зависимости:** BLE3, BLE13. Проверить результаты зависимостей перед началом.
Общие инварианты и решения — в [Stage BLE](stage-ble.md).

**Файлы/область:** Desktop Platform/ViewModels, новый узкий scan adapter/viewmodel; Desktop tests.
Новые имена/папки — предлагаемое место реализации; проверить актуальные исходники.

## Шаги

1. Добавить adapter discovery для Desktop и состояния Idle/Scanning/Completed/Error с cancel. WinRT-результаты преобразовывать в UI DTO.
2. Обновлять коллекцию только через IUiDispatcher; изменения доставлять с проверкой generation.
3. Начинать scan только явным действием пользователя; отменять при закрытии editor/shutdown, повторный scan завершает старый.
4. Сохранять выбранный endpoint при refresh и показывать сохранённый недоступный endpoint. Не подменять его первой найденной нодой.
5. Добавить действие/инструкцию открыть настройки Bluetooth Windows для сопряжения; вернувшись, пользователь явно обновляет список. PIN в приложении не хранится.

**Не входит:** Встроенный PIN-dialog, автоматическое сопряжение, переделка connection page.

## Проверки и завершение

**Тесты/проверки:** Cancel/refresh/shutdown; поздний результат; duplicate names; selected offline endpoint; adapter unavailable; отсутствие connect/save от сканирования; UI thread assertions.

**Готово:** Модель поиска тестируется с fake discovery и не управляет CompanionSession.

**Ручная проверка:** Native просмотр выполняется вместе с BLE15.

- [x] Изменения в границах подэтапа выполнены.
- [x] Обязательные проверки выполнены; фактические результаты записаны ниже.
- [x] Статус подэтапа, checklist Stage BLE и current-state обновлены.

## Результат выполнения

16.10.2026: добавлены DesktopBleDeviceCatalog, platform-neutral BleDeviceChoice и
BleDeviceScanViewModel с Idle/Scanning/Completed/Error, явным scan/cancel/settings.
Все observable updates идут через IUiDispatcher и проверяют текущую generation.
Повторный scan отменяет предыдущий, CancelAsync предназначен для закрытия editor,
StopAsync запрещает новые действия. Даже игнорирующий cancellation backend не
задерживает отмену: его поздняя задача наблюдается, результат не меняет UI.
Фактическая привязка к editor close/shutdown и XAML относится к BLE15.

Refresh сохраняет выбранный ID, оставляет его в списке как «не найден» при отсутствии
результата и никогда не выбирает первый endpoint. Имена — display hints; одинаковые
имена различаются коротким digest ID, identity ноды не вычисляется. IsFound означает
результат discovery, не гарантию GATT connectivity/совместимости. Сохранённый endpoint
виден и на unsupported platform, scan/settings там отключены.

Settings launcher открывает только [ms-settings:bluetooth](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings),
исключительно по явному действию; не хранит PIN, не выполняет pairing/connect/save
и не сканирует автоматически после возврата. Ошибки поиска показываются без native
HRESULT; полное исключение остаётся в logger. Адаптеры и transient scan model
зарегистрированы в AppBootstrap, production editor пока не подключён.

Desktop `BleDeviceScanViewModelTests`, `AppBootstrapTests`,
`ConnectionProfilesViewModelTests` — **21/21 Windows**, **21/21 portable**.
Проверены cancel/refresh/stop, stale native result и уже поставленная старая UI
публикация, duplicate names, отсутствие auto-selection, offline saved ID, platform,
ошибки адаптера/доступа, явный settings launch. Audit dispatcher проверяет каждую
collection/property публикацию, включая вложенные dispatch при отмене.
Оба locked restore успешны, Windows solution Release build без warnings/errors.
Native поиск/визуальная приёмка здесь не выполнялись: они относятся к BLE15.
