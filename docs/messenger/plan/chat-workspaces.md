# Два сохраняемых chat workspace

[UI-план](ui-components.md) · [UI6](ui6-integration.md)

03.10.2026: после ручной проверки UI6 пользователь обнаружил сброс публичного чата
при переходе Public → Private → Public. Прежний один owner переоткрывал history,
очищал search и хранил один selected conversation на ноду. Принят и реализован
вариант двух независимых presentation workspace; это уточнение решения UI2.

## Владение и границы

- MainWindow сохраняет два экземпляра одного ChatsView. Каждый получает свой
  ChatWorkspaceViewModel и ConversationNavigationViewModel с фиксированным разделом
  Channels или Personal, собственными HistoryWindowViewModel/DraftEditorViewModel.
- ChatWorkspacesViewModel координирует active section, node changes, post-commit
  refresh и stop обоих owners. Переключение только скрывает/показывает views,
  не вызывает History.OpenAsync и не очищает selection/search/draft/narrow navigation.
- Session/supervisor/ingest, SQLite, read/draft trackers и post-commit subscription
  остаются общими. Session event channel не имеет новых consumers; transport не
  дублируется. Переключение вкладки не вызывает connect/send/advert/mutation.
- Выбор хранится в Settings по `(NodeId, Channels/Personal)`. Прежний общий ключ
  используется как fallback только для его прежней вкладки, без удаления/перезаписи.
  Общий navigation-tab остаётся последней активной чат-вкладкой ноды.
- История ограничена 500 DTO **на workspace**, до 1000 суммарно; это два фиксированных
  окна, не кеш всех переписок или нод. При смене ноды оба owner очищаются; context
  revisions отвергают late результаты. Черновики обеих нод сохраняет общий tracker.
- Скрытый viewport inactive; commits обновляют его unread/new-message projection,
  но не погашают unread и не заменяют окно историей последних сообщений.
  При возврате view восстанавливает stable sequence + вертикальное смещение.
  Refresh того же stable conversation больше не инвалидирует scroll anchor.

Core, protocol, schema и migrations не менялись. MainWindowViewModel уменьшен
на **10 строк** относительно UI6; координация не разрослась в root ViewModel.
Его Navigation/Messages/SelectedConversation остаются facade активного workspace.
Переключение UI сохраняет состояние в рамках запуска; поиск/scroll не стали
новыми настройками на диске. Durable drafts/selection/theme/geometry восстанавливаются
штатными механизмами startup.

## Проверки

- Настоящая временная SQLite + fake supervisor: 20 round trips с независимыми
  selections, message objects, directory/history searches, Unicode drafts и narrow
  list/detail; входящее в скрытый личный чат не заменяет окно и не читает сообщения.
- A → B → A и старый committed B сохраняют node ownership обоих workspace и четырёх
  черновиков. Shutdown с каждого из пяти экранов: ошибка записи, single-flight retry,
  сохранение и восстановление обоих drafts вместе с theme/geometry.
- Независимые Settings keys, restart, legacy fallback и запрет смены фиксированного
  раздела; late node reads проверены для legacy, Personal и Channels.
- Rapid Public/Private не вызывает повторных history/directory загрузок.
- Native Avalonia: две retained views, реальный scroll anchor на `#fixture-5000`,
  selection/search round trips, все экраны Light/Dark 560/1040, shortcut routing и
  error banner. `tmp/mc-fake` была занята пользовательским экземпляром: проверка
  использовала согласованную SQLite backup-копию в отдельной временной папке.
  Оригинальная база/окно не изменялись, transport не открывался.

Debug/Release solution build: **0 warnings/errors**. Desktop **237/237** в обеих
конфигурациях, Release Core **131/131**, MeshCoreSharp **92/92**. Запущены штатные
test executables через `dotnet run --project tests/<проект> -c <config> --no-build`.
Все XAML resource keys разрешены в Light/Dark; compiled bindings и native DataContext
частей чата, включая own-node mention, проверены. `git diff --check` чистый.

## Ручная проверка

03.10.2026 пользователь подтвердил: «вроде как все работает как положено,
явных проблем не заметил». Исправление переключения Public/Private принято;
это не утверждение отдельной проверки каждого пункта ниже или полной приёмки C10.

Перезапустить приложение с `--data-dir tmp/mc-fake` после закрытия прежнего экземпляра.
Выбрать разные публичный и личный чаты, задать каждому поиск/черновик, прокрутить
истории в разные позиции и переключить вкладки несколько раз. Оба состояния должны
сохраняться независимо. Повторить narrow/wide и обе темы; входящее в скрытую вкладку
не должно обнулять unread. Это исправление UI6; C10 и отложенные фильтры не начаты.
