# BLE15: редактор подключения и BLE-лимит текста

16.10.2026. Код готов, ручная приёмка ожидается. [Подэтап](../messenger/plan/ble15-profile-ui.md).

## Изменения

Windows editor позволяет выбрать Ble, явно найти устройство, сохранить endpoint и
отдельно подключиться существующей командой. Список сохраняет offline ID, одинаковые
имена различаются hint; имя не используется как identity. На portable target
сохранённый BLE-профиль виден, подключение и поиск недоступны. Scan прекращается
при закрытии панели/смене транспорта/shutdown; его результат не выполняет Save/Connect.
Во время сканирования Save/Connect заблокированы, во время open/teardown недоступен
поиск. Ошибки pairing/access/MTU/приёма представлены без native HRESULT.

Текущий BLE budget читается через portable IBleTextBudgetProvider без I/O. Adapter,
session command scope и MessageService передают его processor и composer, учитывая
тип текста и фактическое имя. Счётчик перечитывается при изменении текста, контекста,
readiness и попытке отправки. Сам по себе idle MTU change не имеет отдельного UI
event; проверка непосредственно перед send и внутри BLE write использует актуальный
лимит. Текст сверх него отклоняется, исходный draft не режется. TCP/Serial не
реализуют BLE provider и сохраняют прежние limits. Фиксированные150/127/137 из
наблюдений телефона в код не переносились.

## Регрессия выбора транспорта

По запросу пользователя native audit расширен проверкой transport ComboBox.
Обнаружено: выбор Tcp сбрасывался на Ble. Новый массив TransportKinds при каждом
чтении и notification о его замене сбивали TwoWay selection. Исправлено: одна
observable collection на весь editor; необходимые варианты добавляются без reset.
Сохранённый BLE на неподдерживаемой платформе остаётся в списке после смены типа.

Native regression выбирает **TCP→Serial→BLE→TCP→BLE**, проверяет выбранный enum,
видимость полей, save/readback каждого типа и сохранение выбора после обновления
профилей. Для device ComboBox отдельно проверено сохранение offline selection при
перестройке discovery списка. Он использует OneWay presentation и явный handler
выбора, игнорирующий промежуточный null, как существующий Serial picker.

## Проверки

| Проверка | Результат |
| --- | --- |
| Полный Core suite | 451/451 |
| Library harness Windows / portable | 179/179 / 177/177 |
| Desktop: BleProfileEditorTests, ComposerValidationTests, ConnectionSettingsTests, AppBootstrapTests, BleDeviceScanViewModelTests, ConnectionProfilesViewModelTests | 48/48 Windows и 48/48 portable |
| FakeDataSeederTests.ClosedFixtureSessionSurvivesReopenAndDesktopStartupWithoutAttempts | 1/1 Windows и 1/1 portable |
| Locked restore / Release solution build, обе ветки | Успешно, 0 warnings/errors, locks не менялись |
| Native BLE editor audit | Успешно; fake discovery, временная БД, 0 connects/switches |

```text
dotnet run --project tests/MeshCoreMessenger.Core.Tests -c Release
dotnet run --project tests/MeshCoreSharp.Tests -c Release
dotnet run --project tools/MeshCoreMessenger.ViewportAudit -c Release -- --ble-editor-only
```

Для portable ветки — locked restore и build/run с `-p:McmWindowsDesktop=false`.
Windows restore возвращается последним. Native audit проверяет реальное Avalonia
окно, bindings, Light/Dark и420/900px, возможность Tab-focus кнопки поиска. Скриншоты
и логи — ignored `artifacts/ble15/`; это тестовые устройства, не пользовательские данные.

Полный Desktop suite не объявляется зелёным; предыдущий известный незавершённый
прогон остаётся отдельным ограничением. Native macOS и реальная BLE-сессия Desktop
не проверялись. Hardware RX174–176 остаётся неподтверждённым, как описано в
[отчёте stock firmware](ble-stock-firmware.md).

## Ручная приёмка

Запуск обычного приложения:

```powershell
dotnet run --project src/MeshCoreMessenger.Desktop -c Release
```

1. В настройках подключения создать/выбрать профиль, переключить TCP/Serial/Ble;
   проверить выбранный тип, соответствующие поля и возврат к сохранённому профилю.
2. Для BLE явно найти сопряжённую ноду, выбрать её и сохранить профиль. Убедиться,
   что Save не подключает ноду. Телефон отключить от проверяемой Companion-ноды.
3. Нажать «Подключить», проверить контакты/каналы/историю, отключение и повторное
   подключение. До этого automated checks не выполняли RF send или config writes.
4. Проверить Light/Dark, узкое/широкое окно, полный обход Tab/Shift+Tab и выбор
   устройства с клавиатуры; проверить закрытие панели во время поиска.

До результата этой проверки BLE15 целиком не закрывается, BLE16 не начат.
