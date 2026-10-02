# Что уже есть и где сосредоточена сложность

[Оглавление](../../UI_COMPONENTS_AND_REDESIGN_ANALYSIS.md) · [Маршрутизация чтения](../../README.md)

## Что уже есть и где сосредоточена сложность

| Текущая часть | Подготовленность и проблема |
| --- | --- |
| `Views/MainWindow.axaml` — 359 строк | В одном файле header, selector собственной ноды, вкладки, справочник, поиск, история, draft и overlay настроек |
| `Views/MainWindow.axaml.cs` — 458 строк | Вместе window shutdown/geometry/theme, выбор записей, keyboard routing, scroll/anchor и read visibility |
| `MainWindowViewModel` — 821 строка | Владеет active/viewed node, connection presentation, обновлениями committed projections и координацией; подходит для shell, но содержит toggle overlay настроек |
| `ConversationNavigationViewModel` — 1229 строк | Уже разделяет Personal/Channels/Devices и node-scoped выбор; одновременно владеет справочниками, карточкой, history и draft |
| `HistoryWindowViewModel` — 1149 строк | Готовое bounded окно до 500 сообщений, search, scroll requests, unread и чтение; переносить поведение заново не нужно |
| `DraftEditorViewModel` и `DraftWriteTracker` | Готовые revision, debounce, ownership и durable shutdown; визуальное отделение редактора безопасно при сохранении владельца |
| `ConnectionProfilesViewModel` — 427 строк | Уже самостоятельный редактор Serial/TCP/профилей; его view пока встроен в главное окно |
| `IConversationDirectoryReader` | Разделяет ChatContacts/ServiceContacts/Channels/unknown; отдаёт secret-free contact/channel details |
| `DesktopPreferences`, `DesktopShortcutRouter` | Тема, placement и shortcuts уже выделены; маршрутизация пока предполагает поля конкретного MainWindow |

Размеры приведены как ориентир концентрации ответственности, а не как причина
механического разбиения классов. В первую очередь нужно разделить views и владение
визуальным lifecycle; большой рефакторинг всех ViewModel для первого шага избыточен.

Конкретные UI-проблемы, которые стоит закрыть вместе с компоновкой:

- Header содержит длинный title, статус и три кнопки в горизонтальной группе с
  `Auto`-шириной. При минимальной ширине 560 он конкурирует за место с названием;
  narrow logic сейчас меняет только панели переписки.
- `ConnectionStatusDetail` существует в ViewModel, но не отображается в XAML.
  Новая статусная область должна показывать причину/retry и различать выбранный
  профиль редактора и профиль активной attempt.
- Ошибки navigation/search/read/draft имеют разных владельцев. Новый shell должен
  отобразить их в подходящих областях, включая глобальную ошибку shutdown.
- Devices используют общий detail с заголовком и read-only историей. Отдельная
  полноценная карточка устройства пока отсутствует, хотя metadata уже читаются.
- Overlay настроек сейчас оставляет чат под ним. После замены на полноценный экран
  нужно явно сообщать, что история больше не просматривается.
