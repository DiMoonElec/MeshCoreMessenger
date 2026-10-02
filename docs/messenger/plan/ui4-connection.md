# UI4 — вкладка подключения

[UI-план](ui-components.md) · [Connections](../architecture/connections.md)

Реализовано 02.10.2026 раньше UI3 по запросу пользователя. Ручная приёмка ожидается.

## Компоненты и изменения

- `Views/Connection/`: `ConnectionSettingsView` — центральная scrollable колонка;
  `ConnectionStatusView`, `ConnectionProfileEditorView`, `ConnectionAdvancedSettingsView`.
  Все с x:DataType и локальным StyleInclude, основные поля отдельно от свёрнутых
  дополнительных параметров. Light/Dark, wrap/trimming/tooltip длинных значений.
- `Styles/`: четыре connection-файла, `Typography.axaml`, новые размерные токены
  в `ThemeResources.axaml`; используются существующие тематические кисти.
- `ConnectionProfileEditorViewModel` — только draft/dirty/inline validation.
  `ConnectionProfilesViewModel` — список, сохранение, отмена, pending profile selection
  с действиями Сохранить/Отбросить/Остаться, port refresh с сохранением старого списка
  при ошибке. Сохранённый отсутствующий порт остаётся в списке и получает пояснение.
- `ConnectionControlViewModel` — статус и single-flight явные действия. Snapshot/node
  callbacks имеют revision/generation guards; lifetime cancellation при закрытии.
- `NavigationShellView` — одна таблица section → retained view; Public/Private используют
  одну chat view. Connection зарегистрирован тем же API, будущие Devices/Settings
  подключаются без нового свойства shell. `MainWindow.axaml(.cs)` — только композиция.

**Прирост `MainWindowViewModel`: 0 строк, файл не изменён.** Profiles orchestration
разделён с editor и control; каждый файл значительно меньше 500 строк.

Минимальные согласованные изменения Core:

1. `ConnectionProfileManager` / `IConnectionProfileManager`: `SaveAsync` записывает
   профиль без выбора; `SaveAndSelectAsync` переиспользует запись и сохраняет прежний
   контракт. Default interface method сохраняет совместимость существующих custom
   manager implementations; не поддерживающие save-only явно возвращают NotSupported.
2. `ConnectionSupervisorModels` / `ConnectionSupervisor`: read-only immutable
   `UsedProfile` в snapshot. Без него ProfileId не позволяет сравнить сохранённые
   настройки с реально используемыми. Equality guard публикации сравнивает прежние
   поля; metadata не добавляет StateChanged. State machine/retry policy не изменены.

SQLite schema, MeshCoreSharp, Core event ownership/barriers не изменены.

## Семантика действий

Выбор в списке только открывает редактор. Save не меняет startup selection, active
attempt или retry cycle; Cancel восстанавливает сохранённый профиль. При dirty draft
Connect выключен с пояснением. Подключение использует сохранённые данные и выбирает
этот профиль для дальнейшего startup. Для другого/изменённого профиля явный reconnect
ждёт полного Disconnect, затем существующий SwitchProfile, который также обновляет
cached profile при suspend. Без изменённого профиля используются Select/ConnectNow;
это сохраняет обычное поведение ConnectNow/retry. Новых handlers сна или Core
переходов не добавлено. Повторный клик не отменяет текущий
workflow и не создаёт второй. При timeout 30 с новая связь не начинается; ошибка
видна в карточке состояния. Shutdown отменяет workflow до начала новой attempt.
RetryWaiting без изменений использует штатный ConnectNow; Disconnect подавляет retry.

Статус различим по тексту/значку; реально используемый профиль отделён от редактора.
Сохранённые изменения того же активного профиля отмечены как неприменённые.
Уход с чата сообщает inactive; возвращение пересчитывает viewport, owners не заменяются.
Верхний overlay сохранён; место под ним зарезервировано токеном отступа страницы.

## Проверки

Добавлены Core-тесты save-only/selection/normalization/update/validation и snapshots
активного/retry cycle. Все прежние Core-тесты сохранены; новые добавлены отдельно.
Desktop: selection/save/cancel, pending selection, dirty/inline validation,
границы host/port, отсутствующий порт и отказ catalog, single-flight reconnect,
shutdown/late click, timeout на fake time, RetryWaiting disconnect/immediate retry,
late generation и callbacks after Stop. Прежние hidden-chat/viewport, fixture startup
без attempts и recoverable shutdown regressions остаются в suite.

Debug/Release solution build: 0 предупреждений/ошибок. В обеих конфигурациях:
Desktop 174/174, Core 131/131, MeshCoreSharp 92/92. Запущены штатные test executables
(solution dotnet test с текущим MTP runner не обнаруживает эти suites).
`git diff --check` и локальные ссылки документации чисты. Все используемые resource
keys разрешены в Light/Dark, оформительских цветов/размеров в connection-разметке нет.

Native-рендер на существующей `tmp/mc-fake`: Light/Dark, ширина контента 660/380,
профиль-заглушка выбран только в редакторе, AutoConnect=false, Reconnect=false.
Снимки просмотрены агентом: ограниченная центральная колонка, читаемые карточки,
отсутствующий длинный Serial-путь не ломает layout. Пустые строки скрыты.
Финальная визуальная/интерактивная приёмка пользователем ещё требуется.
При render-проверке реальный transport не открывался; данные стандартной папки не использовались.

## Ручная проверка

Из корня после Debug build, без повторного seed:

```sh
dotnet src/MeshCoreMessenger.Desktop/bin/Debug/net10.0/MeshCoreMessenger.Desktop.dll --data-dir tmp/mc-fake
```

- Выбрать «Подключение» и fixture-профиль: выбор/сохранение сами не открывают порт.
- Изменить поле: Save активен только при valid dirty; Cancel возвращает значение.
- Новый профиль / смена профиля при dirty: проверить три inline-действия.
- Serial/TCP переключают поля; BLE недоступен. Пустой host, `abc`, `0`, `65536`
  в TCP port показывают ошибки рядом с полем. Проверить дополнительные параметры.
- Отсутствующий fixture Serial-порт остаётся виден с пометкой; refresh не выбирает
  первый доступный порт. Длинный путь/имя не ломают layout.
- Light/Dark, широкое/узкое окно, keyboard focus, tooltip, scroll страницы.
- Явно подключить заглушку: ожидается диагностируемый отказ на несуществующем порте,
  без reconnect loop (Reconnect=false). Реальное устройство этот профиль не открывает.
- Вернуться в чат: диалог/draft/anchor сохранены; скрытая история не погашает unread.

Без реального устройства fixture-БД не подтверждает успешные Identify/Synchronizing/
Online, фактический public key, unplug/replug или применимость DTR/RTS конкретной плате.
Эти состояния/переходы покрываются fake-тестами; аппаратная/UI-приёмка Online отдельно.
Удаление, BLE, поиск нод, radio settings и перенос верхнего статуса не реализуются.
