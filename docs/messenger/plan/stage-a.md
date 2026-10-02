# A. Каркас, хранилище и граница событий

[Оглавление](../../MESSENGER_PLAN.md) · [Маршрутизация чтения](../../README.md)

## A. Каркас, хранилище и граница событий

### A1 — каркас приложения (выполнено 25.09.2026)

- [x] Созданы `MeshCoreMessenger.Core`, `MeshCoreMessenger.Desktop` и два проекта
  xUnit v3; сохранена единственная Companion-библиотека `MeshCoreSharp.dll`.
- [x] Настроены ссылки `Desktop -> Core -> MeshCoreSharp` без доступа UI к internal runtime.
- [x] SDK зафиксирован на `10.0.301`; версии пакетов централизованы в
  `Directory.Packages.props`, для всех проектов созданы NuGet lock-файлы.
- [x] Добавлен базовый DI/logging bootstrap и минимальное Avalonia-окно с ViewModel.
- [x] Проверены restore в locked mode, Release build без предупреждений, два новых
  smoke-теста и прежние 82 регрессионных теста библиотеки.
- [x] Desktop реально запущен на macOS 26.6 arm64; старт DI/Avalonia подтверждён
  логом. Сетевые/Serial-клиенты не создавались, аппаратных передач не было.

Зафиксированные прямые пакеты: Avalonia `12.1.3`, CommunityToolkit.Mvvm `8.4.2`,
Microsoft.Data.Sqlite и Microsoft.Extensions `10.0.12`, xUnit v3 `4.0.1`.

Команды проверки A1:

```bash
dotnet restore MeshCoreSharp.sln --locked-mode
dotnet build MeshCoreSharp.sln -c Release --no-restore
dotnet test MeshCoreSharp.sln -c Release --no-build --no-restore
dotnet tests/MeshCoreSharp.Tests/bin/Release/net10.0/MeshCoreSharp.Tests.dll
dotnet run --project src/MeshCoreMessenger.Desktop/MeshCoreMessenger.Desktop.csproj -c Release --no-build --no-restore
```

### A2 — фундамент локального хранилища (выполнено 25.09.2026)

- [x] Добавлены `IAppPaths` и платформенные каталоги данных: Application Support
  на macOS, LocalApplicationData на Windows и XDG/fallback на Linux; БД называется
  `messenger.db`, резервные копии находятся в отдельном подкаталоге.
- [x] Реализованы один последовательный `DatabaseWorker` на отдельном потоке и
  отдельные короткоживущие read-only соединения без pooling.
- [x] Для SQLite включены `foreign_keys=ON`, WAL, `synchronous=FULL` и конечный
  `busy_timeout=5000`; добавлена транзакционная миграция версии 1.
- [x] Созданы таблицы и типизированные модели для settings, connection profiles,
  nodes, sessions, contacts, channels/bindings, conversations, messages,
  send attempts и drafts с внешними ключами, длинами ключей, уникальностью и индексами.
- [x] Реализованы stores настроек и профилей подключения, проверка целостности и
  версии при открытии. Более новая версия отклоняется до записи; ошибка миграции
  не удаляет и не пересоздаёт исходную БД.
- [x] Backup и restore используют SQLite backup API. Restore сначала проверяется
  и мигрируется на временной staging-БД, поэтому ошибочная миграция не заменяет
  рабочую БД; живая WAL-БД не копируется как одиночный файл.
- [x] На временных каталогах прошли 15 тестов хранилища и прежний Core smoke-тест;
  Release build прошёл без предупреждений, прежние 82 регрессионных теста
  библиотеки также прошли.
- [x] Подключение к Companion, reconnect, приём/отправка и аппаратные проверки не запускались.

Команды проверки A2:

```bash
dotnet build MeshCoreSharp.sln -c Release --no-restore --disable-build-servers
dotnet tests/MeshCoreMessenger.Core.Tests/bin/Release/net10.0/MeshCoreMessenger.Core.Tests.dll
dotnet tests/MeshCoreMessenger.Desktop.Tests/bin/Release/net10.0/MeshCoreMessenger.Desktop.Tests.dll
dotnet tests/MeshCoreSharp.Tests/bin/Release/net10.0/MeshCoreSharp.Tests.dll
```

### A3 — безопасный барьер событий библиотеки (выполнено 25.09.2026)

- [x] В публичный API `MeshCoreClient` добавлен `FlushEventsAsync(CancellationToken)`.
- [x] FIFO-маркер завершает вызов после возврата всех синхронных callbacks,
  поставленных до него; более поздние события конкретный вызов не задерживают.
- [x] Барьер не объявляет пустой очередь ноды, commit приложения или завершение
  самостоятельно запущенной обработчиком async-работы.
- [x] Отмена прекращает только ожидание вызывающего кода: принятый маркер остаётся
  в очереди, доставка событий и последующие барьеры продолжают работать.
- [x] После `DisconnectAsync` можно дождаться накопленных callbacks. После disposal
  новый барьер отклоняется с `ObjectDisposedException`; принятый до закрытия маркер
  продолжает дренироваться.
- [x] `DisconnectAsync`, `DisposeAsync`, RX, dispatcher и TCP/Serial не изменены;
  `DisposeAsync` по-прежнему не ждёт пользовательские callbacks неявно.
- [x] Добавлены 10 детерминированных regression tests; полный набор библиотеки —
  92/92, Release build — без предупреждений и ошибок.
- [x] Физическая нода и аппаратные передачи не использовались.

Команды проверки A3:

```bash
dotnet build MeshCoreSharp.sln -c Release --no-restore --disable-build-servers
dotnet tests/MeshCoreSharp.Tests/bin/Release/net10.0/MeshCoreSharp.Tests.dll
```

### A4.1 — один экземпляр на каталог данных (выполнено 25.09.2026)

- [x] До запуска Avalonia приложение открывает `.meshcoremessenger.lock` внутри
  фактического `IAppPaths.DataDirectory` с `FileShare.None` и удерживает handle
  в течение всего процесса.
- [x] Разные каталоги имеют независимые блокировки. Постоянный lock-файл сам по
  себе не блокирует запуск; штатное или аварийное закрытие handle освобождает OS-lock.
- [x] Второй процесс для того же каталога завершается до обычного startup с кодом
  `2` и диагностикой; недоступный каталог получает отдельную ошибку и код `3`.
- [x] Добавлены 5 тестов механизма; вместе с bootstrap smoke-тестом Desktop — 6/6.
- [x] На macOS вручную проверены два одновременных процесса: второй отклонён, после
  принудительного завершения первого новый процесс успешно открыл приложение.
- [x] SQLite, MeshCore-сессия, reconnect и аппаратные интерфейсы не запускались.

Команды проверки A4.1:

```bash
dotnet build MeshCoreSharp.sln -c Release --no-restore --disable-build-servers
dotnet tests/MeshCoreMessenger.Desktop.Tests/bin/Release/net10.0/MeshCoreMessenger.Desktop.Tests.dll
dotnet tests/MeshCoreMessenger.Core.Tests/bin/Release/net10.0/MeshCoreMessenger.Core.Tests.dll
dotnet tests/MeshCoreSharp.Tests/bin/Release/net10.0/MeshCoreSharp.Tests.dll
```

### A4.2 — офлайн-загрузка истории (выполнено 25.09.2026)

- [x] Добавлен ограниченный read-only `ILocalHistoryReader`: список диалогов и
  страницы сообщений читаются через отдельные короткоживущие SQLite-соединения.
- [x] Диалоги сортируются по последнему сообщению; страницы сообщений имеют
  курсор `LocalSequence`, возвращаются хронологически и не смешивают ноды/диалоги.
- [x] Startup соблюдает порядок: каталог данных -> instance lock -> открытие и
  миграция SQLite -> DI и начальная история -> Avalonia. Второй экземпляр не
  открывает БД; ошибка открытия получает диагностику и код завершения `4`.
- [x] Минимальный офлайн-shell показывает список диалогов, последние 100 сообщений
  выбранного диалога, пустое состояние, ошибку чтения и статус «Не подключено».
- [x] При выходе отменяются и завершаются текущие чтения, затем закрывается writer/БД
  и только после этого освобождается блокировка каталога.
- [x] Core-тесты — 23/23, Desktop-тесты — 10/10, регрессии библиотеки — 92/92;
  полный Release build прошёл без предупреждений.
- [x] Desktop запущен на macOS arm64 с локальной БД без Companion-сессии;
  TCP/Serial, физическая нода и аппаратные передачи не использовались.

Готово, когда БД/настройки переживают рестарт, миграции и backup проходят на
временных каталогах; неподдерживаемая/повреждённая БД не уничтожается;
барьер проверен с задержанным обработчиком и отключением RX; регрессии библиотеки
проходят; сохранённая история доступна до подключения. Новых радиопередач для
этапа не требуется. **Этап A завершён 25.09.2026.**
