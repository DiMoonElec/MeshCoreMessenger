# C9 — тема, геометрия окна и завершение keyboard UX

[Оглавление](../../MESSENGER_PLAN.md) · [Маршрутизация чтения](../../README.md)

#### C9 — тема, геометрия окна и завершение keyboard UX

**Цель:** сделать уже работающие сценарии удобными при ежедневном запуске.

**Scope:** system/light/dark через Settings, сохранение normal bounds/window state,
восстановление окна в доступной области экрана при смене монитора/DPI; Cmd/Ctrl,
Tab/focus, Escape/Back для существующих сценариев, масштабирование и контраст.
Проверить постоянный статус ноды/профиля/retry и немодальные ошибки во всех layouts;
ошибку сохранения не стирать успешным reload истории. Записи настроек включить в
уже определённый порядок завершения; не задерживать startup сетевыми действиями.

**Не входит:** новый дизайн-системный framework, полная локализация, tray/notifications,
новые команды ноды и telemetry polling. **Зависимости:** C3, C5, C7–C8.

**Обязательные тесты:** persistence темы/geometry, fallback для повреждённых Settings,
недоступный монитор/невалидные bounds, сохранение normal bounds при maximized;
маршрутизация shortcuts при разных focus/IME, отсутствие send, ошибки writer и
shutdown. Платформенные вычисления отделить от реально запускаемого окна.

**Готово:** preferences переживают рестарт, окно доступно, основные действия
выполняются клавиатурой. **Ручная UI-проверка:** обязательна на macOS и, при наличии,
Windows — темы, DPI/мониторы, Cmd/Ctrl, IME. Недоступную платформу отметить как
непроверенную и перенести именно её платформенную приёмку в E.

**Фактически выполнено 01.10.2026.** Добавлен desktop-only `DesktopPreferences`
поверх существующей `Settings` без изменения схемы: system/light/dark и normal window
bounds/maximized сохраняются revisions и последним retryable barrier штатного
shutdown. Повреждённые значения безопасно дают system theme/штатную геометрию.
Чистый `WindowPlacementCalculator` отделяет validation/clamp/primary fallback от
Avalonia window и учитывает screen scaling при применении физических bounds. В окне
добавлены динамическое переключение темы, сохранение normal bounds при maximized,
Cmd/Ctrl+F, Escape и Alt+Left; text editor/IME не перехватываются, send отсутствует.
Статус profile/node/retry и немодальные ошибки остаются в постоянном header во всех
layouts. Settings читаются локально до показа окна; сетевой lifecycle по-прежнему
стартует только после `Opened`.

Детерминированно покрыты SQLite restart темы/геометрии, повреждённые Settings,
недоступный монитор, invalid/oversized bounds, normal bounds при maximized, routing
Cmd/Ctrl/focus/Escape/Back и защита text input/IME, failure/retry Settings writer и
точный shutdown order. Release build прошёл без предупреждений; Core — 127/127,
Desktop — 111/111, MeshCoreSharp — 92/92. Ручная проверка C9 на macOS ожидается:
system/light/dark, maximized и перенос/смена масштаба монитора, Cmd/Ctrl+F,
Escape/Alt+Left, Tab/focus и IME. Windows недоступна и её платформенная приёмка
переносится в E. C10 не начат.
