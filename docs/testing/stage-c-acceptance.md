# Stage C — автоматическая приёмка и нагрузка

03.10.2026 · [План C10](../messenger/plan/c10-acceptance.md)

Автоматические проверки выполнены; финальная ручная приёмка пользователем требуется.
Это не release-приёмка Windows/macOS, не аппаратный тест и не начало D.

## Сквозной сценарий

`StageCAcceptanceTests.cs` дополняет `UiWorkspaceIntegrationTests` через partial class,
переиспользуя настоящую временную SQLite, production read/draft/preferences owners,
fake supervisor/dispatcher и управляемый draft debounce. Все fixture writes идут
через Core stores, не прямой SQL.

- A содержит 100 000 канальных и 12 личных сообщений; B — по 12. Совпадающие
  имена/ключи собеседника/канала не объединяют истории разных собственных нод.
- Offline startup восстанавливает A. 1994 page loads проходят историю полностью
  назад и вперёд: cursor продвигается, gaps/дубликатов нет, выгруженные данные снова
  доступны, предел — 500 DTO на workspace.
- Поиск раннего сообщения и jump не уменьшают unread; скрытый viewport не читает.
  Фактический просмотр 1–3 уменьшает счётчик после durable advance.
- Fake Online A → B → A сохраняет четыре раздельных черновика. Late committed B
  остаётся в B и не меняет ни публичную, ни личную историю A.
- Новый dirty draft перед shutdown вызывает injected write failure; выход отменён.
  Два concurrent retry разделяют одну попытку. Новый MainWindowViewModel на том же
  хранилище восстанавливает A, оба draft и watermark. Реальное закрытие/открытие
  SQLite дополнительно входит в ручной сценарий ниже.

Переиспользованные regression suites: Core `MessengerEndToEndTests` проверяет
Identify/reconnect A/B/A через настоящий supervisor/session/coordinator/ingest с
fake Companion, одну attempt и сообщения при teardown. `MessageIngestorTests` —
commit-before-notification и retry queue; `ReceiveCoordinatorTests` — единственный
event consumer, barriers и timeout → новая session. Desktop shutdown tests —
порядок barriers, ошибки ingress/session/read/draft/preferences, сохранение
lock/writer и single-flight retry. Late reads/callbacks проверены history/navigation/
supervisor suites, без дублирования lifecycle в новом тесте.

Queued-dispatcher regression воспроизводит найденную race refresh во время node
initialization. Ожидание completion устраняет пропуск открытия history/draft,
не сериализуя загрузки разных нод и не ослабляя context guards/shutdown cancellation.
Отдельный тест подтверждает, что shutdown отменяет refresh, ожидающий инициализацию,
без зависания выхода. Прежние late-node-load tests по-прежнему проходят.

## Release-измерения

macOS 27.0.1 (26A434), arm64; SDK 10.0.301, runtime 10.0.9.
Изолированный test process, без параллельного build/test; это не performance SLA.

| Измерение | Результат |
| --- | --- |
| Seed + SQLite setup + offline VM startup | 10 845 мс |
| VM page load p50 / p95 / max, 1994 загрузки | 0,39 / 0,46 / 1,64 мс |
| History read API, последние 100, p50 / p95, 20 чтений | 0,26 / 0,40 мс |
| Поиск одного раннего совпадения, включая completion polling | 24,15 мс |
| Повторный VM startup + stop | 26,64 мс |
| Пик message DTO двух workspace | 512: 500 публичных + 12 личных |
| Выборочный managed heap во время scroll | 11,1 MiB |
| Максимальный RSS запуска по `/usr/bin/time -l` | 160 251 904 байт (≈153 MiB) |

History API timing включает read connection и DTO, не только SQL engine.
VM timing включает storage/presentation/ImmediateUiDispatcher, **не** реальный UI.
Managed heap — sampled показатель с harness, не строгий пик всей памяти; RSS
включает .NET/runner и setup. `Process.PeakWorkingSet64` здесь возвращает 0, поэтому
тест сообщает unavailable, а не «приложение занимает 0».
Нет хрупких assertions на миллисекунды/GC; correctness закреплена лимитами/ownership.
Измерялось окно истории; ручное накопление страниц результатов поиска и очень
большие справочники этим нагрузочным сценарием не измерялись.

## Native Avalonia и regression

Отдельная fixture с `#fixture-100000` создана настоящим dev seeder в изолированной
`tmp/mc-c10.FGr3Cm`. Пользовательская `tmp/mc-fake` и стандартная папка не изменялись.
Lifecycle Start не вызывался, transports не открывались.

Light/Dark, 1040/560 × 700: 500 DTO, 24 реальных `ListBoxItem` (virtualized panel).
Layout + render + сохранение PNG: 32,56 / 11,09 / 20,25 / 12,46 мс соответственно.
Это включает запись файла, **не** frame time. Public → Private → Public сохраняет
selection и деактивирует скрытый viewport. Снимки просмотрены; лежат в ignored tmp.

Debug/Release solution build: 0 предупреждений/ошибок. Desktop 242/242 в обеих
конфигурациях; Release Core 131/131; MeshCoreSharp 92/92. Реальные suites запущены
через `dotnet run --project tests/<проект> -c <конфигурация> --no-build`,
нулевой discovery `dotnet test` не засчитан. `git diff --check` чистый.

Повторить изолированный большой тест:

```sh
dotnet run --project tests/MeshCoreMessenger.Desktop.Tests -c Release --no-build -- -method '*StageCRealHundredThousand*' -reporter verbose -showliveoutput
```

## Обязательная ручная приёмка

Подготовленная локальная база уже заполнена, повторно seed не запускать:

```sh
dotnet run --project src/MeshCoreMessenger.Desktop -c Debug -- --data-dir tmp/mc-c10.FGr3Cm
```

1. Открыть `#fixture-100000`, долго прокручивать в обе стороны, прыгнуть к последнему,
   найти `Сообщение 000003` и перейти к результату. Подгрузка не застревает,
   позиция не сбрасывается. Полный проход 100 000 руками не нужен.
2. Проверить `#fixture-unread`: счётчик уменьшается при просмотре, не от загрузки/
   поиска. Перейти на другую вкладку и вернуться: Public/Private имеют независимые
   selection/search/drafts/scroll. Повторить Light/Dark и narrow/wide.
3. Ввести разные Unicode-черновики в публичном и личном чатах, выбрать тему,
   изменить размер окна. Закрыть/открыть **без seed**: черновики, тема, геометрия,
   выбранные диалоги и прочитанное восстановлены.

На другой машине создать новую явную папку штатным Debug запуском с
`--seed-fake-data --large`; после заполнения убрать эти флаги. Fixture-профиль
не подключать: порт несуществующий, AutoConnect/Reconnect выключены.
Fake A/B/A, write failure и late callbacks доказаны тестами, вторая нода не требуется.
Реальные unplug/wake/DPI, Windows, упаковка/подпись и физический disk-full остаются E.
Отложенные фильтры и UX создания профиля не блокируют C10.
