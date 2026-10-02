# UI5 — общие настройки

[UI-план](ui-components.md) · [Preferences и shutdown](../architecture/ui-and-local-writes.md)

Реализовано 02.10.2026 по запросу пользователя, раньше UI3. Пользователь подтвердил
успешную ручную проверку 02.10.2026: «Все работает».

## Реализация

- `Views/Settings/ApplicationSettingsView.axaml(.cs)` — typed UserControl, пока только
  карточка оформления и выбор «Авто / Тёмная / Светлая».
- `Styles/ApplicationSettingsStyles.axaml` — локальный StyleInclude; существующие
  Typography, SettingsColumnWidth (680), padding/spacing/radius и Light/Dark кисти
  ThemeResources. Колонка центрирована, ограничена шириной 680 и сужается на узком
  окне; содержимое прокручивается. Оформительских литералов в view нет.
- `MainWindow.axaml.cs` регистрирует retained view через общий shell API для Settings
  и передаёт существующий root DataContext. Чат и его owners не пересоздаются.
- `MainWindowViewModel` меняется только в подписях/порядке ThemeOptions. Прежние
  SelectedTheme, применение через Window/App и DesktopPreferences сохранены.

Тема применяется сразу, «Авто» соответствует ThemeVariant.Default и системной теме.
Отдельной кнопки сохранения нет: выбранная тема входит в прежний retryable durable
preferences barrier завершения работы. Профили/соединение не затрагиваются.
Core, SQLite schema, supervisor и протокол не изменены.

## Проверки

Три новых Desktop-теста проверяют список вариантов, выбор каждого, mapping к
Avalonia ThemeVariant, запись через настоящий preferences/SQLite и восстановление
после reload. Сохраняется Offline. Прежние real-SQLite restart и retry после отказа
writer остаются в suite.

Debug/Release build: 0 предупреждений/ошибок. Desktop 187/187 в обеих конфигурациях;
Release Core 131/131 и MeshCoreSharp 92/92. Один первый Release-прогон выявил race
существующего `SameNodeThroughDifferentProfilesKeepsOneIdentityAndHistory`: enumeration
ObservableCollection совпало с projection refresh в fake UI dispatcher. Отдельный
повтор полного прогона прошёл; этот тест и production history в UI5 не менялись.
`git diff --check` чистый.

Native-проверка на `tmp/mc-fake`: выбор через настоящий ComboBox меняет
Application.RequestedThemeVariant для всех трёх вариантов; Light/Dark рендеры при
ширине контента 660/380, без подключения транспорта. Все используемые кисти/размеры
уже существуют в ThemeResources; окончательная визуальная приёмка пользователем.

## Ручная проверка

Открыть «Настройки», переключить Авто → Тёмная → Светлая: всё окно меняет оформление
сразу. Проверить центрирование/ширину на узком и широком окне, keyboard navigation;
штатно закрыть и открыть с той же папкой данных — тема должна восстановиться.
Для Авто проверить смену системной темы. Возврат в чат сохраняет выбор и черновик,
скрытый чат не помечает сообщения прочитанными.
