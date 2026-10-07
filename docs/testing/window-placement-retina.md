# Геометрия окна на macOS Retina

07.10.2026: пользователь сообщил, что после изменения размера/положения и выхода
следующий запуск снова раскрывает окно на весь экран. Исправление реализовано;
пользователь подтвердил исправление («проверил, работает»).

## Причина и исправление

На проверенной Avalonia 12.1.3 macOS screen coordinates имеют Scaling=1, а окно
на Retina — RenderScaling=2. Capture умножал Bounds на RenderScaling; Restore
сравнивал результат с WorkingArea экрана и делил на Screen.Scaling. Размеры
удваивались, затем clamp растягивал восстановленное окно до рабочей области;
macOS могла сообщить Maximized для такого окна.

Capture теперь использует Scaling текущего Screen, согласованный с Position,
WorkingArea и Restore. RenderScaling остаётся масштабом отрисовки. Дополнительно
closed/shutdown-accepted окно не обновляет сохранённую геометрию во время teardown.
Схема БД, ключ preferences и shutdown barriers не менялись.

Из пользовательской БД только прочитан один ключ desktop.window-placement в
read-only режиме. Значения БД не сбрасывались; история/сообщения не читались.
Старое сохранённое Maximized может восстановиться при первом запуске после правки:
следует один раз задать нужные размер/позицию и выполнить штатный выход.

## Проверки

Native `--placement-only` на временной SQLite: восстановить ранее Maximized,
изменить размер через native window properties, затем normal 820×520,
Position=(130,150), записать preferences и создать новое окно. Отдельный reader
перечитывает placement из SQLite. До правки сохранено 1640×1040 и новое окно
Maximized 1470×820; после правки сохранено/восстановлено 820×520 и (130,150), Normal.
Проверяется также, что Close не перезаписывает snapshot. Это новый native Window
с persisted projection, не автоматизация мыши и не отдельный production process.

Debug/Release native roundtrip и Release S3 tray audit пройдены; builds без warnings/errors: Hide/Show, Maximized/minimize,
IPC второго процесса, сохранение draft/scroll и exit/failure остаются рабочими.
DesktopPreferencesTests — 11/11. Native сценарии не подключаются к ноде.
Windows DPI/несколько мониторов требуют отдельной ручной проверки; используются
те же Screen coordinate units, без platform-specific формулы для Windows.

```sh
dotnet build tools/MeshCoreMessenger.ViewportAudit -c Debug --no-restore
dotnet tools/MeshCoreMessenger.ViewportAudit/bin/Debug/net10.0/MeshCoreMessenger.ViewportAudit.dll --placement-only
```
