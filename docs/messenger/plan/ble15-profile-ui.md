# BLE15 — BLE в существующем редакторе подключения

[Stage BLE](stage-ble.md) · [Оглавление](../../MESSENGER_PLAN.md)

**Статус: код реализован 16.10.2026; автоматизированные и native fake проверки пройдены,
ручная приёмка пользователем ожидается. Этап целиком не закрыт.**

**Цель:** Дать пользователю создать профиль и подключиться к выбранной BLE-ноде.

**Зависимости:** BLE11–BLE14, BLE8 подтверждён. Проверить результаты зависимостей перед началом.
Общие инварианты и решения — в [Stage BLE](stage-ble.md).

**Файлы/область:** `Desktop/ViewModels/ConnectionProfileEditorViewModel.cs`, `ConnectionProfilesViewModel.cs`, `Views/Connection/ConnectionProfileEditorView.axaml`, styles и Desktop tests.
Новые имена/папки — предлагаемое место реализации; проверить актуальные исходники.

## Шаги

1. Заменить заглушку «Bluetooth LE — пока недоступен» рабочим вариантом на Windows. На других платформах явно показать недоступность; сохранённый BLE-профиль остаётся видимым.
2. Добавить поиск, список, выбранное устройство и краткое состояние сопряжения/ошибки. Имена одинаковых устройств различать коротким endpoint hint.
3. Связать draft полей BLE с dirty tracking и Save; переключение типа очищает несовместимые настройки.
4. Оставить Connect/Disconnect/SwitchProfile существующему owner. Выбор устройства/профиля и Save не запускают подключение.
5. Во время scan/open/shutdown корректно ограничить команды; ошибки pairing/MTU/adapter отобразить понятным текстом без сырых HRESULT в основном UX.

**Не входит:** Новый экран настроек ноды, переработка всех connection controls, управление радио.

## Проверки и завершение

**Тесты/проверки:** Bindings, dirty/save/reopen, отмена поиска, профиль недоступной ноды, reconnect и switch, unsupported platform. Проверить существующие TCP/Serial editor tests.

**Готово:** Профиль BLE можно выбрать, сохранить и явно подключить через существующий supervisor.

**Ручная проверка:** Обязательна: Windows Light/Dark, узкое/широкое окно, клавиатура/Tab; fake discovery до реального connect.

- [x] Изменения в границах подэтапа выполнены.
- [ ] Обязательные проверки выполнены; фактические результаты записаны ниже.
- [x] Статус подэтапа, checklist Stage BLE и current-state обновлены.

## Результат выполнения

16.10.2026: BLE доступен в существующем редакторе на Windows: scan, cancel, выбранный
endpoint, pairing hint, ссылка на настройки Windows, dirty/save/reopen. ID/name
проходят в draft/profile, неактивные поля очищаются в сохранённом draft; PIN и identity
не добавлялись. На portable target сохранённый BLE виден, подключение отключено.
Выбор и Save не подключают устройство. Connect/Disconnect/Switch остаются у прежнего
owner; discovery ограничен во время open/teardown. Закрытие панели отменяет scan,
shutdown отписывает editor и останавливает scan model.

По дополнительному запросу пользователя проверен реальный transport ComboBox:
обнаружен сброс TCP обратно на BLE при пересоздании ItemsSource. TransportKinds
заменён стабильной observable collection; native audit проходит TCP→Serial→BLE→TCP→BLE,
проверяет поля и каждый save/readback. Отдельный BLE device picker игнорирует временный
null при reset списка и явно восстанавливает выбранный endpoint после refresh.

BLE text budget доведён до composer и durable admission: read-only
IBleTextBudgetProvider → client adapter → session command scope → processor/composer.
Лимит обновляется при изменении текста/контекста/readiness и перепроверяется перед
передачей; draft не обрезается. TCP/Serial сохраняют прежние protocol/name limits.
Ошибки BLE представлены понятным текстом без native HRESULT; классификация/retry
политика BLE13 сохранены. Runtime routing, framing и SQLite schema здесь не менялись.

Проверки: полный Core **451/451**, library **179/179 Windows**, **177/177 portable**;
Desktop targeted **48/48** на обеих ветках плюс startup fixture **1/1** на каждой.
Обе Release solution builds/locked restore без warnings/errors. Native Avalonia audit
с fake discovery/временной БД проверил bindings, transport switching, dirty/save,
сохранённый offline endpoint, Light/Dark при420/900px и Tab-focus кнопки поиска;
автоматических connect/switch не было. Headless bootstrap tests получили injectable
UI dispatcher, поскольку их процесс не запускает Avalonia UI loop.

По повторному указанию пользователя продолжить после успешного hardware drain
до166bytes выполнен код UI. Это не закрывает BLE8 RX174–176: пограничный recovery
остаётся аппаратно неподтверждённым. Реальный Desktop BLE connect, ручной полный
обход Tab/Shift+Tab и пользовательская визуальная приёмка ещё не выполнены;
полный Desktop suite не объявляется зелёным. BLE16 не начинался.

Команды и ручной сценарий: [отчёт BLE15](../../testing/ble15-profile-ui.md).
