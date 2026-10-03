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
На checkpoint UI4 верхний overlay сохранялся с резервным отступом страницы.
Последующая доработка по запросу пользователя перенесла статус в заголовок окна
и убрала этот отступ: [UI-план](ui-components.md#заголовок-окна-вместо-панели-статуса).

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

### Отложенное улучшение понятности профилей — 03.10.2026

Пользователь подтвердил создание второго профиля и работу списка Serial-портов.
Текущую семантику оставляем без изменений. В дальнейшей UI-доработке сделать
создание нового профиля более интуитивным: отчётливо различать создание и изменение
существующего, а запрос «Сохранить / Отбросить / Остаться» при несохранённом draft
показывать рядом с инициирующим действием, чтобы его не приходилось искать ниже
формы. Это отдельное UX-улучшение, не обязательное условие C10.

### Восстановление списка Serial-портов — 03.10.2026

Поле Serial-порта снова обычный `ComboBox`, а не `AutoCompleteBox`: показывает
все обнаруженные порты и сохранённые отсутствующие значения. Выбор меняет только
draft; refresh не сбрасывает выбранный порт и не выбирает первое устройство.
Отсутствующий порт по-прежнему сопровождается пояснением, длинный путь — tooltip.
Core, persistence и supervisor не изменены.

Добавлены тесты отсутствия автоматического выбора и восстановления draft selection
после refresh без save/connect. Debug/Release build: 0 предупреждений/ошибок;
Desktop 239/239 в обеих конфигурациях, Release Core 131/131, MeshCoreSharp 92/92.
Native Avalonia-проверка на изолированной копии `tmp/mc-fake`: открытие списка,
выбор, refresh и Cancel сохраняют корректный selection; Light/Dark, ширины 380/660.
Реальный transport не открывался. Ручная проверка списка реальных портов пользователем
ещё требуется: открыть список, выбрать порт и обновить его, убедившись, что выбор
не сбрасывается и подключение не начинается до явного действия.

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
Удаление профилей, BLE, поиск нод и radio settings не реализуются. Перенос верхнего
статуса выполнен отдельной доработкой после UI4, не частью этого checkpoint.
