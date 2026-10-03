# UI6 — совместная интеграция экранов

[Оглавление](../../MESSENGER_PLAN.md) · [UI-план](ui-components.md)

03.10.2026: реализация и автоматическая приёмка выполнены; финальная ручная
cross-screen проверка пользователем ожидается. C10 не начат.
Ручная проверка выявила пробел round trip Public → Private → Public; он закрыт
[двумя chat workspace](chat-workspaces.md). Результаты ниже — первоначальный checkpoint
UI6, последующие результаты и повторная ручная проверка записаны в связанном разделе.

## Результаты

Все пять разделов уже используют production VM/durable services после UI2–UI5.
Сохранены один ChatsView для публичных/приватных чатов, отдельные DevicesWorkspaceView,
формы подключения/темы и общий механизм section → content. Новых владельцев session
или consumers CompanionSession.Events нет.

Исправлены две потери после редизайна:

- Ошибка startup/durable writes/shutdown оставалась в VM, но не отображалась.
  ApplicationErrorView показывает немодальное уведомление только при HasError,
  с Light/Dark ресурсами. Постоянная статусная панель не возвращена.
- Escape из Settings возвращает на предыдущий раздел. Cmd/Ctrl+F с фокусом на rail
  работает в видимом чате; скрытый чат клавиши не перехватывает. Обычные TextBox
  сохраняют приоритет Escape.

MainWindowViewModel: **прирост 0 строк**. Core, schema, supervisor/state machine и
Companion library не менялись; send/advert/mutations не добавлялись.

## Автоматическая приёмка

Новые UiWorkspaceIntegrationTests используют временную настоящую SQLite, реальные
history/read/draft/preferences owners, fake supervisor/commit notifications и
управляемую задержку draft:

- переходы chat → devices/connection/settings → chat сохраняют выбор, экземпляры
  history/draft, сообщения, поиск и текст; скрытая история не погашает unread;
- fake A → B → A на экране подключения сохраняет node-scoped историю, устройства
  и два независимых черновика; committed сообщение старой B остаётся в B;
- закрытие с каждого из пяти экранов при ошибке записи draft сохраняет pending
  данные, показывает ошибку, допускает single-flight retry и восстанавливает
  черновик/theme/geometry/selection при новом startup.

NavigationShellViewModelTests проверяют возврат из Settings во все четыре раздела.
Прежние suites дополнительно проверяют stale callbacks/reads/generations, bounded
viewport/anchors, barriers, reconnect, save/connect, закрытие при reconnect и fixture
без autoconnect. Они переиспользованы без дублирования lifecycle.

Debug/Release build: **0 warnings/errors**. Desktop: **230/230** в обеих конфигурациях;
Release Core: **131/131**, MeshCoreSharp: **92/92**. Все XAML resource keys найдены в
Light/Dark. Native Avalonia на `tmp/mc-fake`: пять экранов, обе темы, ширины 560/1040,
attach/detach, hidden viewport, Escape/Ctrl+F, Title suffix и error banner.
Transport не открывался. Снимки проверки сохранены локально в игнорируемой `tmp/`.

Regression-команда: `dotnet run --project tests/<проект> -c <конфигурация> --no-build`.
В окружении `dotnet test` сообщил ноль тестов: это **не** успешный прогон;
фактические результаты получены штатными встроенными runners.

## Ручная приёмка

На существующей fixture-базе, без повторного seed:

```sh
dotnet run --project src/MeshCoreMessenger.Desktop -c Debug -- --data-dir tmp/mc-fake
```

1. Выбрать чат, прокрутить в середину, ввести черновик и выполнить поиск. Перейти
   в устройства, подключение и настройки, вернуться: выбор/позиция/черновик сохранены;
   публичные и приватные списки соответствуют вкладкам.
2. Проверить обе темы, narrow/wide, Tab/Space. Cmd/Ctrl+F с фокусом на rail открывает
   поиск видимого чата; на подключении не фокусирует скрытый чат. Escape из Settings
   возвращает назад.
3. Закрыть/открыть: данные, черновик, тема и геометрия сохранены.
4. Если доступна нода: входящее сообщение при открытых Devices/Settings/Connection
   не погашает unread до возврата и просмотра. Reconnect/заголовок не меняют выбранный
   нечата-раздел.

Реальные unplug/replug, wake/DPI, disk-full и другая физическая нода в UI6 не
проверялись. Ошибка записи/retry shutdown и identity A/B/A доказаны fake-тестами.
Нагрузка 100 000 сообщений и измерения памяти/задержек остаются C10;
упаковка/подпись/платформенная release-приёмка — Stage E.

## Отложено по решению пользователя

03.10.2026: фильтры каналов и отдельную визуальную группу unknown не возвращаем.
Их семантику/алгоритм нужно переосмыслить будущей задачей, не в UI6/C10.
Identity/storage и legacy VM-механизмы сохраняются; поиск чатов/истории не отложен.
