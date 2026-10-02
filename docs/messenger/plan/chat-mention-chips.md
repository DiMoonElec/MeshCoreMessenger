# Rounded mention chips и копирование сообщения

[UI2 — чат](ui2-chat.md) · [Первоначальная подсветка](chat-mentions.md)

Реализовано по уточнённому запросу пользователя. Пользователь подтвердил
успешную ручную проверку: «Проверил, все работает корректно».

## Архитектура и файлы

- `Presentation/MentionParser.cs`: прежний parser и правила совпадения сохранены;
  сегмент содержит RawText и отдельный DisplayText (сейчас равен RawText).
- `Controls/MentionTextBlock.cs`: теперь TextBlock; обычные сегменты — Run,
  mentions — InlineUIContainer → MentionChip (Border) → TextBlock.
  Нет собственного renderer/glyph calculations/source-offset mapping.
  MentionChip передаёт baseline внутреннего TextBlock через штатный
  TextBlock.BaselineOffset с учётом padding и BorderThickness.
- `Styles/MentionStyles.axaml`: локальные стили foreign/own, DynamicResource
  кисти обеих тем. `ConversationStyles.axaml` оставляет стиль тела сообщения.
- `Styles/ThemeResources.axaml`: ChatMentionRadius=6, ChatMentionPadding=4,1;
  прежние семантические кисти не менялись. Цветов в C# нет.
- `Views/Chat/ConversationView.axaml`: StyleInclude и ContextMenu на весь bubble;
  MenuItem передаёт текущую HistoryMessageListItem через CommandParameter.
- `ConversationView.axaml.cs` и `Presentation/MessageCopy.cs`: copy только
  `message.CopyText` через Avalonia Clipboard.SetValueAsync(DataFormat.Text).
  CopyText уже хранит исходный message.Text, не собирается из inlines/labels.
- `Desktop.Tests/MentionTests.cs`: прежние parser-тесты сохранены, control-тесты
  обновлены под chips; fake clipboard writer доказывает копирование raw модели,
  независимо от отображаемого текста.

Квадратные скобки сохранены. Точное имя ViewedNode по-прежнему даёт own-цвета;
offline история сохраняет этот контекст. Core/schema/protocol/history owners
не менялись; существующие незакоммиченные изменения первоначальной подсветки
не откатывались. Данных, radio sends или новых зависимостей не добавлено.

## Ограничения и layout

InlineUIContainer — атомарный элемент внешнего text flow. Не помещающийся chip
целиком переходит на следующую строку; очень длинное имя может переноситься внутри
плашки по доступной ширине. LineHeight принудительно не задаётся, margin/transform
для сдвига текста не нужны. Padding немного увеличивает естественную высоту строки.
Непрерывного mouse selection больше нет по согласованному scope. Source of truth
для Copy — модель; представление можно позже менять без переделки clipboard.
Версия Avalonia 12.1.3 [представляет embedded control как один символ layout](https://raw.githubusercontent.com/AvaloniaUI/Avalonia/12.1.3/src/Avalonia.Controls/Documents/InlineUIContainer.cs),
поэтому реконструкция исходного текста из Inlines была бы неверным контрактом.

## Проверки

Debug/Release solution build: 0 warnings/errors. Desktop **219/219** в обеих
конфигурациях, Release Core **131/131**, MeshCoreSharp **92/92**; diff check чистый.
Все static/dynamic resource keys разрешаются в Light/Dark.

Native macOS проверка на `tmp/mc-fake`, без правки сообщений/подключения транспорта:
обычный текст; mention в начале/середине; несколько; emoji; punctuation; длинное
сообщение/явный newline; особо длинное имя. Light/Dark, ширины 620/280;
baseline каждой плашки совпадает с baseline одной из строк (погрешность <1px),
стили разрешены. Снимки просмотрены. Меню реально открыто на bubble и проверено,
что CommandParameter — модель данного сообщения. Фактический системный clipboard
не перезаписывался автоматической проверкой: writer тестируется fake-делегатом.

Ручная проверка: внешний вид/переносы в обеих темах, смена темы без restart,
ПКМ на тексте или chip → «Копировать» → вставка целого оригинала со скобками,
emoji/newline; scrolling и copy после повторного использования строки; offline own.
