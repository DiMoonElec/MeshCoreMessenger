# S3 — трей, скрытие окна и настоящий выход

[Stage S](stage-s.md) · **Статус: реализован; автоматическая и macOS native части проверены. Пользователь подтвердил работу на macOS и Windows.**

07.10.2026: реализованы TrayIcon/menu, OnExplicitShutdown, скрытие/возврат и выход через прежний durable barrier. [Результаты и ручной checklist](../../testing/s3-tray-lifecycle.md).

**Цель:** приложение остаётся доступным в фоне и всегда имеет понятный путь возврата
и полного завершения.

**Scope:** встроенный Avalonia `TrayIcon` на уровне App, native menu «Открыть» /
«Выйти» и подходящий ресурс значка. Минимальный Desktop coordinator разделяет
Show/Hide/Exit; использует общее действие активации S1.
Сворачивание скрывает окно; крестик следует S2. Скрытие отменяет закрытие окна,
сохраняет normal/maximized placement, selection/draft/scroll и все существующие owners.
Не останавливать connection lifecycle и не повторять startup при очередном Show.

Приложение живёт до явного выхода; используется `OnExplicitShutdown` с явной
интеграцией всех exit paths. «Выйти», системный Quit/Cmd+Q и завершение сеанса ОС
не превращаются в Hide. Сохранить существующий особый путь OS shutdown.
Обычный выход идёт через DesktopShutdownCoordinator; failure показывает окно
с ошибкой/повтором, не оставляет недоступный скрытый процесс.
Не очищать tray/IPC и не освобождать file lock до принятого завершения.

При скрытии/минимизации сбросить видимый диапазон истории для read tracker;
сам выбранный чат не означает просмотр. Возврат сохраняет viewport.
Не скрывать окно в недоступный трей: нужен рабочий fallback возврата, без новых
пользовательских настроек. На macOS пункт «Открыть» обязателен независимо от click event.

**Не входит:** новая connection policy, UI owners recreation, badges/mute/звуки,
переработка shutdown barriers или библиотечного runtime.
**Зависимости:** S1, S2; [shutdown](../architecture/shutdown.md),
[workspace](chat-workspaces.md), [C6 unread](c6-unread.md), [C9 placement](c9-preferences.md).

**Тесты:** Close × оба режима, minimize/Show, повторные Hide/Show, exit при hidden,
повторный exit и shutdown failure/retry; OS close reasons, недоступный tray;
hidden/minimized не продвигает read cursor; сохранение черновика/scroll/maximized;
приём и текущие отправки продолжаются по прежним правилам, Show не запускает connect.

**Готово:** ни один сценарий не оставляет окно без возврата; настоящий выход
сохраняет данные и освобождает ресурсы в прежнем порядке.
**Ручная проверка: обязательна** — macOS/Windows, tray menu, Dock/taskbar,
крестик/сворачивание/Quit, повторный запуск из трея, ошибка сохранения и перезапуск.

**Источник:** [Avalonia TrayIcon](https://docs.avaloniaui.net/controls/navigation/trayicon),
[application lifetimes](https://docs.avaloniaui.net/docs/fundamentals/application-lifetimes).
Платформенные ограничения проверять на закреплённой версии и реальных пакетах.
