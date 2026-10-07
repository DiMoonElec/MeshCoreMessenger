# S6 — системные уведомления macOS и Windows

Дата: 07.10.2026. [Этап S6](../messenger/plan/s6-native-notifications.md),
[архитектура](../messenger/architecture/notifications.md).

Реализация подготовлена; ручная приёмка настоящих banner/click обеих ОС ожидается.
Linux отложен. Аппаратная нода в автоматических проверках не использовалась.

## Автоматические проверки

- macOS Debug/Release сборки: без предупреждений и ошибок.
- Desktop regression suite: **395/395 в Debug и Release** (S5 baseline 385,
  добавлены 10 проверок S6). Core/library production source не менялись.
- Реестр: opaque token, точный scope, expiry, malformed/empty ID, лимит 128,
  отсутствие preview в файле, cleanup.
- Адаптер: замена группы, denied, cancellation, delivery/removal errors,
  очистка targets и disposal независимо от ошибки удаления баннера.
- IPC: типизированная цель передаётся владельцу без открытия SQLite вторым
  экземпляром; для другой папки поднимается её владелец.
- Навигация: ожидание первого окна, локальный history jump, другая нода,
  отсутствующее сообщение, нулевые connect calls; отмена ожидающего click при выходе.
- Permission failure остаётся локальной ошибкой настроек; denied не вызывает
  повторного permission prompt.
- Native Avalonia `--native-notifications-only`: background callback поднимает
  скрытое окно, открывает точное SQLite-сообщение, stale target показывает fallback;
  zero connect. CLI platform status — Unsupported, процесс не падает.
- Native `--shutdown-only`: реальные DI/notification workers/bindings,
  обновления команд на UI thread и durable barrier пройдены.
- Self-contained `.app` собран через `tools/publish-macos.sh osx-arm64`, native
  dylib присутствует, ad-hoc codesign/verify пройдены.
- В настоящем `.app`-контексте отдельный native probe инициализировал
  UserNotifications bridge и получил status=0 (разрешение ещё не запрошено).
  Probe не вызывал authorization/show и не открывал пользовательскую БД.
- Windows C# GUI-граф с `McmWindowsDesktop=true` успешно скомпилирован на macOS,
  без warnings/errors; отдельные Windows lock-файлы проходят locked restore.
  Для проверки компиляции единственный generated manifest копировался временным
  инструментом вместо Windows-only `mt.exe`, PRI expansion отключалась только
  параметром проверочного запуска. Это **не проверка Windows runtime/portable**.
  Обычные project settings используют настоящие SDK packaging tools на Windows.

## Запуск для ручной приёмки

macOS: `bash tools/publish-macos.sh osx-arm64` (для Intel — `osx-x64`),
затем запустить полученный `MeshCoreMessenger.app` из пути, напечатанного скриптом.
Открыть настройки уведомлений и нажать «Разрешить в системе».
В `dotnet run` macOS нативные уведомления недоступны: нужен bundle identity `.app`.

Windows: обычный `dotnet run --project src/MeshCoreMessenger.Desktop` использует
Windows TFM и RID автоматически. Проверить также настоящий portable каталог:

```powershell
dotnet publish src/MeshCoreMessenger.Desktop -c Release -r win-x64 --self-contained true -p:PublishTrimmed=false -p:PublishSingleFile=false -o artifacts/windows/win-x64
```

Запускать `MeshCoreMessenger.Desktop.exe` из этого каталога. Native Windows App SDK
доставляется вместе с приложением; MSIX не требуется. Windows packaging tools
приходят из SDK NuGet-зависимостей. Перенос/переименование каталога требуется
проверить отдельно: native registration привязана к executable identity/path.

## Ручные сценарии — пока не подтверждены

1. ЛС включены/каналы выключены, затем наоборот: баннер соответствует настройкам.
2. Реальное активное окно с видимым сообщением подавляет баннер; скрытое в трее,
   неактивное или прокрученное вверх окно позволяет уведомление.
3. Клик поднимает окно и открывает сообщение; burst заменяет баннер своей группы,
   initial sync даёт сводку S5. Summary открывает приложение без смены ноды.
4. macOS: разрешить, отказать, затем изменить разрешение в настройках ОС;
   диалог не повторяется при получении сообщения. Focus/DND соблюдается.
5. Windows: обычный запуск, второй экземпляр, баннер из portable-пакета,
   click при работающем/скрытом процессе; permission deny и Focus.
6. После аварийного завершения оставшийся баннер запускает приложение и разрешает
   token; занятая нужная папка получает IPC. При штатном выходе баннеры удаляются.
7. Две разные папки данных, удалённое сообщение/папка, другая текущая нода:
   корректный владелец или понятный fallback, без connect/device mutation от click.
8. Выход во время подготовки/доставки/click: история сохранена, late callbacks
   не обновляют закрытое окно. Проверить перенос и переименование приложения.

macOS cold click может быть доставлен ОС уже после обычного bootstrap процесса;
поздняя цель другой папки пересылается её владельцу либо отдельному запуску.
Этот сценарий и поведение нескольких процессов одного bundle/Windows app identity
требуют реальной платформенной проверки; успешная C# сборка их не подтверждает.

Источники: [Apple UserNotifications](https://developer.apple.com/documentation/usernotifications),
[Microsoft notification registration/activation](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-quickstart),
[Unregister и COM lifetime](https://github.com/microsoft/WindowsAppSDK/blob/main/specs/AppNotifications/AppNotifications-spec.md).
