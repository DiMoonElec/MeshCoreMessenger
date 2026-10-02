# UI2 — компонент чата

[UI-план](ui-components.md) · [Текущее состояние](current-state.md)

Последующие доработки: [начальная подсветка упоминаний](chat-mentions.md), затем
[rounded chips и copy исходного сообщения](chat-mention-chips.md).

Реализовано 02.10.2026. Пользователь подтвердил визуальную проверку чата:
«все ОК». Это подтверждение внешнего вида; отдельное прохождение каждого
функционального пункта чек-листа ниже не заявлялось.

## Границы и владение

Один сохраняемый `ChatsView` обслуживает Public/Private через существующий
`ConversationNavigationViewModel`: Channels/Personal соответственно. Отдельные
UserControl: `ConversationListView`, `ConversationView`, `ComposerView`, все с
`x:DataType` и локальным StyleInclude. Кисти обеих тем и размеры — ThemeResources.
Переключение вкладок сериализовано; устаревшая projection не видна под новой вкладкой.
History/draft owners живут до shutdown, не останавливаются при скрытии.

Viewport сообщает только фактически видимые строки активного окна. Скрытие,
detach и смена контекста инвалидируют late scroll callbacks; anchor сохраняется
при возврате и prepend. История остаётся bounded/virtualized, post-commit обновления,
поиск и durable drafts используют существующие services. Enter вводит новую строку;
отправка выключена до Stage D. Core, schema и protocol не изменены.

По новому запросу пользователя offline selector убран. Настройка
`desktop.last-connected-node-id` сохраняет фактически определённую ноду; старый
ручной `desktop.viewed-node-id` не выбирает историю. При отсутствии новой настройки
используется наиболее недавно замеченная нода (legacy/fixture startup).
Disconnect сохраняет её историю; Identify следующей ноды заменяет контекст.

На checkpoint UI2 статус подключения оставался прежним overlay; chat резервировал
место сверху. После UI4 по отдельному запросу пользователя статус перенесён в
заголовок окна, overlay и резервный отступ удалены. См. [UI-план](ui-components.md#заголовок-окна-вместо-панели-статуса).

## Проверки

VM-тесты: startup/actual node persistence, запрет offline-переключения, сохранение
owners при скрытии и быстрые Public/Private переходы. Pure viewport-тесты:
cache/offscreen строки не прочитаны, скрытое/неактивное окно не читает, late callback
старого контекста отклоняется. Ключи ресурсов разрешены в Light/Dark.
Автоматические тесты не заменяют проверку фактического рендеринга/прокрутки.

Debug/Release solution build: 0 предупреждений/ошибок. Desktop 158/158 в обеих
конфигурациях, Core 127/127 и MeshCoreSharp 92/92 в Release. Запущены штатные test
executables: solution `dotnet test` с текущим MTP runner не обнаруживает xUnit suites.
`git diff --check` чист.
Дополнительная native-проверка с ограниченным UI-loop, без показа окна, подтвердила
DataContext всех четырёх компонентов и загрузку 5 каналов/20 сообщений из
`tmp/mc-fake` в Light/Dark. Это проверка bindings, не визуальная приёмка.

## Ручная приёмка

Из корня репозитория после Debug build, **без повторного seed** существующей БД:

```sh
dotnet src/MeshCoreMessenger.Desktop/bin/Debug/net10.0/MeshCoreMessenger.Desktop.dll --data-dir tmp/mc-fake
```

- При старте fixture-нода, публичные каналы и `#fixture-twenty` открыты offline.
- Public/Private: разные списки; выбор чата, возврат после других вкладок, черновик.
- Light/Dark: текст, hover, selection, keyboard focus, tooltip читаемы.
- 20 сообщений: переносы, длинная строка, URL/emoji, copy текста; пустой канал.
- 5000 сообщений: older/newer scroll, поиск и переходы, unread уменьшается только
  при просмотре активного окна, скрытый чат unread не уменьшает.
- Узкое окно: список → чат → Назад; повторное открытие того же выделенного чата.
- Cmd/Ctrl+F, Escape/Alt+Left, multiline/IME; отправка неактивна, радио не открывается.
- Закрытие/restart сохраняет drafts и node-scoped навигацию.
