# Рекомендуемые подэтапы

[Оглавление](../../UI_COMPONENTS_AND_REDESIGN_ANALYSIS.md) · [Маршрутизация чтения](../../README.md)

> Это исходное четырёхшаговое предложение, а не текущий порядок реализации.
> Согласованные UI1–UI6 находятся в [актуальном плане](../plan/ui-components.md).

## Рекомендуемые подэтапы

Предлагаемые названия UI1–UI4 — обозначения в этом анализе, не выполненные этапы.
При принятии решения их можно добавить как C9.1–C9.4 перед C10, сохранив запись о
первоначально завершённом C9. Переписывать план так, будто новая навигация уже была
реализована, не следует.

### UI1 — выделение views с сохранением поведения

Цель: уменьшить обязанности MainWindow до shell/window lifecycle.
Scope: `ConnectionSettingsView`, `ChatWorkspaceView`/`ChatView`, перенос viewport и
scroll adapter к history control; typed bindings, явные activation/visibility и
focus contracts. Существующие VM и внешний вид максимально сохраняются.
Не входит: rail, новая палитра, отдельные device workflows, переделка Core.
Зависимости: C9 и короткий ручной baseline текущих scroll/search/draft/settings.
Тесты: скрытый/отсоединённый chat не меняет read watermark; повторный attach не
дублирует callbacks; selection/anchor/draft не сбрасываются; shutdown из Settings
ждёт прежних barriers; существующие Core/Desktop/library regressions.
Готово: MainWindow не обращается напрямую к внутреннему history list/search TextBox;
компоненты имеют понятное владение, baseline сохранён. Ручная UI-проверка обязательна.
Сложность: средняя; основной риск — scroll/read lifecycle.

### UI2 — rail и полноэкранная область выбранного раздела

Цель: реализовать описанную пользователем структуру окна.
Scope: shell sections Channels/Personal/Devices/ConnectionSettings, rail сверху/
снизу, mapping к workspace, возврат из Settings, постоянные status/node context,
адаптивный header и breakpoint по workspace width. Settings открывается в правой
области, не overlay. Исправить shortcuts для видимого экрана.
Не входит: новая телеметрия, отправка, пер-node кеш всех viewport, отдельная ОС-window.
Зависимости: UI1.
Тесты: выбор каждой section, Settings → назад сохраняет chat context; connect A/B
на Settings выбирает фактическую node history; Offline selector доступен только
Offline; incoming в скрытом чате не считается прочитанным; narrow Back/focus/IME;
переходы без второго connection loop, subscriptions и без send/advert/mutation.
Готово: каждая иконка показывает свой screen, status/error доступны всегда; добавление
нового раздела не требует копировать shell. Ручная wide/narrow проверка обязательна.
Сложность: средняя.

### UI3 — отдельный список и read-only карточка устройств

Цель: Devices выглядит как устройство, а не обычный чат с отключённым вводом.
Scope: service directory, DeviceDetailsView на существующих metadata, type-specific
labels и nullable fields; read-only служебная история остаётся доступной. Сохранить
unknown и отсутствующие в актуальном справочнике записи; не создавать Conversation
при простом открытии карточки. При необходимости выделить Devices VM, сохранив
одного владельца активной history и node context.
Не входит: remote CLI/status/telemetry/login, карта, mutations и live online badge.
Зависимости: UI1–UI2.
Тесты: Repeater/Room/Sensor/None/unknown и отсутствующий metadata; full key/node
ownership, nullable path/coordinates, late details A после B; ни editor/send, ни
network commands от открытия; read-only сообщения и unread доступны корректно.
Готово: устройство отделено от Chat client, metadata честно подписаны, исходящая
команда не вызывается. Ручная проверка с fake fixtures обязательна.
Сложность: низкая–средняя при существующем read-only scope.

### UI4 — визуальное оформление и итоговая keyboard/theme проверка

Цель: согласованный компактный интерфейс с привычной структурой мессенджера.
Scope: semantic theme resources, rail icons, selected/focus states, row density,
chat bubbles/time/labels, header/search/draft spacing, empty/error states, длинный
Unicode/ключи, контраст system/light/dark. Применять shared styles внутри текущего
Avalonia проекта, без дополнительной UI-библиотеки.
Не входит: полный Telegram clone, анимационная система, localization framework,
tray/notifications, send/delivery UI до D.
Зависимости: UI2–UI3.
Тесты: theme/geometry/shortcut regressions, layout на минимальном и wide размере,
ограниченный history viewport; UI snapshots/headless tests — если оправдано
инфраструктурой, не добавлять framework только ради сравнения каждого цвета.
Готово: согласован ручной wide/narrow вид, light/dark, keyboard/focus/IME; для
недоступной Windows платформенная проверка явно остаётся в E.
Ручная UI-проверка обязательна; автоматы не доказывают эстетическое качество.
Сложность: средняя, трудоёмкость зависит от числа итераций дизайна.

После UI4 выполняется существующий C10: интеграция A/B/A, scroll/read/search/draft,
writer failure/retry shutdown/restart, 100 000 сообщений и полный Release/regression.
Stage D сохраняет исходный scope отправки и управления контактами/каналами.
