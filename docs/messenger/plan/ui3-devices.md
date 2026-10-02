# UI3 — устройства

[UI-план](ui-components.md) · [Текущий статус](current-state.md)

Реализован 02.10.2026 после UI4/UI5 по согласованному плану.
Автоматические проверки завершены. Пользователь подтвердил успешную ручную
проверку: «Проверил, все работает».

## Компоненты и границы

- `Views/Devices/DevicesWorkspaceView` — сохраняемый экран shell, список слева,
  карточка справа; на узком окне list/detail и «Назад».
- `DeviceListView` — имя, меньшим шрифтом тип, отметка «Только в истории», поиск,
  страницы по 50, обновление, inline ошибки и пустые состояния.
- `DeviceDetailsView` — read-only карточки identity и сохранённых сведений,
  выделяемый/копируемый полный ключ, свёрнутые технические сведения.
- `Styles/DevicesWorkspaceStyles.axaml`, `DeviceListStyles.axaml`,
  `DeviceDetailsStyles.axaml` — локальные StyleInclude, существующие токены
  ThemeResources/ Typography, обе темы. Новые ресурсы не потребовались.
- `ViewModels/DevicesWorkspaceViewModel` — отдельная selection и чтение через
  `IConversationDirectoryReader` (ServiceContacts, SearchPage, ContactDetails).
- `DeviceDetailsViewModel` и `DeviceListItem` — presentation сохранённых данных.

MainWindow регистрирует экран тем же общим механизмом shell. MainWindowViewModel
получил **8 строк**: создание/свойство, передача viewed node/имени, refresh после
commit и загрузки истории, await StopAsync. Новый владелец node/session не создан.
Core, SQLite schema, протокол и существующие chat ViewModels не менялись.

## Данные и гарантии

Identity устройства — NodeId владельца истории плюс **полный** public key контакта,
не имя/тип/префикс. Смена viewed node очищает selection; node/list/detail revisions
и отмена защищают от старых результатов. Refresh сохраняет выбранное устройство
даже при изменении порядка списка; загруженные страницы дедуплицируются по ключу.
Shutdown отменяет и ожидает зарегистрированные чтения до закрытия хранилища.
Выбор устройства не меняет выбранный чат, history owner или черновик; скрытый чат
использует прежние visibility guarantees. View не подписывается на session events.

Карточка показывает имя, тип (2/3/4 и честный fallback неизвестного), полный ключ,
PresentOnNode, LastAdvertUtc, координаты, UpdatedUtc, flags, raw OutPath/AdvertPayload.
PresentOnNode означает присутствие в последнем справочнике, **не online**.
Для координат 0,0 указана неоднозначность. OutPath сохранён как raw buffer без
OutPathLength: hops/маршрут не выводятся. Отсутствующий advert не выдумывается.
Батарея, RSSI, firmware, uptime, live telemetry, карта и радио-запросы вне scope.
Нет send/advert/config mutations, composer или отдельного device chat.

## Проверки

- Debug и Release solution build: 0 предупреждений/ошибок.
- Desktop: **203/203** в обеих конфигурациях; Release Core **131/131**,
  MeshCoreSharp **92/92**; `git diff --check` чистый.
- Новые fake-тесты: типы/fallback и поля, paging/search, сохранение selection,
  одинаковые имена/префиксы, разные ноды, late page/detail/search, неверный owner,
  inline error/retry, narrow/back, cancellation/shutdown и команды после Stop.
- Fixture startup test: шесть устройств трёх типов, chat selection/draft retained,
  прежняя проверка отсутствия autoconnect. Fixture-session остаётся закрытой.
- Native macOS рендер на существующей `tmp/mc-fake`: Light/Dark, ширины 1000/420;
  bindings/ресурсы проверены, выбор устройства сохраняет chat selection/history/draft.
  Транспорт не открывался. Это дополнительная проверка, не ручная приёмка.

## Ручная приёмка

Запустить существующую тестовую базу без повторного seed:

```sh
dotnet run --project src/MeshCoreMessenger.Desktop -c Debug -- --data-dir tmp/mc-fake
```

Проверить вкладку «Устройства»: шесть fixture-устройств, имя/тип, выбор и full-key
copy, presence/координаты, технический блок; поиск и пустой результат; обе темы;
широкое/узкое окно и возврат «Назад». Переключение на чат сохраняет переписку и
черновик. Disconnect оставляет историю последней ноды. Hardware не нужен для
read-only карточек; новые live сведения и радио-действия не реализованы.
