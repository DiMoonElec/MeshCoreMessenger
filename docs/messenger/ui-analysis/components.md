# Границы компонентов и владельцы

[Оглавление](../../UI_COMPONENTS_AND_REDESIGN_ANALYSIS.md) · [Маршрутизация чтения](../../README.md)

## Границы компонентов и владельцы

| Компонент | Ответственность | Владелец данных/состояния |
| --- | --- | --- |
| `MainWindow` | Window placement, activation, close/shutdown, composition shell | Существующие preferences/shutdown services |
| `NavigationRailView` | Выбор верхнего раздела, кнопка Settings снизу | Shell section, независимый от выбранного диалога |
| Connection status / node context | Connection/profile/node/error; offline — последняя нода без selector | `MainWindowViewModel`, snapshot supervisor; выделение status view отложено |
| `ChatsView` | Chat directory, adaptive list/detail, фокус поиска | Один сохраняемый navigation coordinator для Public/Private |
| `ConversationListView` | Список, search, selection events | `ConversationNavigationViewModel` |
| `ConversationView` / `ComposerView` | Header, history search, viewport/scroll adapter / draft | `HistoryWindowViewModel` / `DraftEditorViewModel` |
| `ConnectionSettingsView` | Редактор профилей, port refresh, connect/disconnect, theme | `ConnectionProfilesViewModel` и явные shell commands/preferences |
| `DevicesWorkspaceView` | Node-scoped service directory, выбор устройства | Существующий directory API; отдельный devices VM при необходимости |
| `DeviceDetailsView` | Read-only metadata и связанные служебные записи | `ContactDetailsProjection`, immutable context полного ключа/NodeId |

Для Repeater/Room/Sensor сначала достаточно одного `DeviceDetailsView` с
условными блоками по типу. Отдельные специализированные карточки имеют смысл,
когда действительно появятся разные сценарии. Из текущих committed данных доступны
имя, тип, ключ, flags, OutPath, LastAdvertUtc, координаты и PresentOnNode.
PresentOnNode означает наличие в справочнике, а не доказанный online. Батарея,
текущий RSSI и удалённая телеметрия не подразумеваются этими projections.

UserControl не должен читать БД, создавать client/supervisor или подписываться на
session events. Бизнес-команды идут через ViewModel; code-behind допустим для
viewport, focus, translating positions и platform window events.

На первом шаге сохраняется один navigation/history/draft coordinator и его
существующие post-commit подписки. ViewModel живёт до shutdown приложения, а view
может скрываться или переподключаться. `StopAsync` не вызывается при переключении
вкладки: он необратимо останавливает workers. Заново создавать coordinator на каждый
выбор иконки нельзя. Скрытые views при этом не должны сообщать просмотр сообщений.

Для небольшой фиксированной навигации можно использовать явные DataTemplates в
ContentControl с сохранёнными VM, либо сохранённые views с переключением видимости.
Выбор сделать в UI1/UI2 по lifecycle тестам. В обоих вариантах контракты attach,
detach и visibility должны быть явными; кешировать неограниченное число чат-views
или историю каждого открытого диалога не требуется.
