# S3 — трей и настоящий выход

[План S3](../messenger/plan/s3-tray-lifecycle.md) · [Ветка S](../messenger/plan/stage-s.md)

07.10.2026: **S3 реализован; автоматические проверки и native macOS audit пройдены.
Пользователь подтвердил работу на macOS; Windows приёмка ожидается.**

Пользователь сообщил: «на macOS все ОК». Детализация отдельных ручных сценариев
не предоставлена; пункты расширенного checklist не отмечаются автоматически.
Windows будет проверен пользователем отдельно.

## Поведение и границы

App владеет DesktopTrayLifecycle и одним Avalonia TrayIcon с меню «Открыть» /
«Выйти». Показ использует общий DesktopActivationCoordinator S1: тот же MainWindow,
без повторного startup или подключения. На Windows левый клик также открывает окно;
на macOS возврат доступен через меню и Dock Reopen. Используется существующий chat
asset: template icon на macOS, белый glyph на синем круге для светлого/тёмного
Windows taskbar. Новых пакетов нет.

Сворачивание скрывает окно. Крестик по умолчанию скрывает его; выбор «Закрывать
приложение» из S2 запускает настоящий выход. Hide/Show сохраняют owners, placement,
черновик и viewport. Связь и обработка сообщений при Hide не останавливаются.
ConversationView наблюдает видимость/WindowState окна и сбрасывает read range,
включая момент незавершённого scroll callback. Выбранная переписка в фоне не означает
просмотр входящего сообщения. Подсказка настроек обновлена; уведомления остаются S5/S6.

Lifetime — OnExplicitShutdown. Пункт «Выйти» вызывает RequestExit, а системный Quit
приходит как WindowCloseReason.ApplicationShutdown и проходит тот же durable barrier.
Hide разрешён только для WindowClosing. После успешного барьера вызывается явный
lifetime Shutdown; tray удаляется в Exit, IPC/lock остаются во владении Program.
Повторные запросы выхода объединяются. При DesktopShutdownException окно показывается
для существующей ошибки/повтора; tray и IPC не освобождаются. ShutdownCoordinator,
его порядок остановки/flush и особый OSShutdown/process-exit путь не менялись.

Если создание tray завершилось ошибкой или нет native menu exporter, окно не скрывается:
сворачивание остаётся обычным, крестик завершает приложение через durable shutdown.
Exporter подтверждает создание backend/menu, а не физическую видимость значка в оболочке.
Windows может поместить его в скрытые значки; Explorer restart обрабатывает Avalonia.

## Выполненные проверки

- Debug solution и Release Desktop/native audit builds: 0 warnings/errors.
- Desktop: **361/361** в Debug и Release. Семь новых policy cases проверяют оба режима,
  отсутствие трея и ApplicationShutdown/OSShutdown/Owner/Undefined close reasons.
  В первом Release прогоне старый SameNodeThroughDifferentProfilesKeepsOneIdentityAndHistory
  упал на конкурентном перечислении ObservableCollection; отдельный класс 49/49 и
  полный повтор 361/361 прошли. Тест и production connection policy не менялись.
- Native `--tray-only` на macOS arm64: настоящий TrayIcon/menu exporter,
  Close → menu command Open три раза, minimize → настоящий второй процесс IPC,
  восстановление Maximized, тот же history/selection/draft; ConnectCalls=0.
  На fixture из 40 сообщений с разной высотой ненулевой scroll offset сохраняется
  после каждого Hide/Show с допуском 2 px.
- Committed incoming на временной SQLite продолжает обрабатываться при скрытом окне;
  read cursor не продвигается, visible range пуст. При unavailable-tray минимизации
  окно остаётся доступным и read range также очищается.
- Управляемый shutdown adapter: hidden Exit, повторный Exit во время барьера,
  failure → показ окна/сохранение трея, Quit через TryShutdown, fallback без трея,
  настройка полного закрытия и успешный повтор выхода из скрытого окна.
  Реальные durable owners проверяет существующий Desktop regression suite.
- Отдельный временный framework-dependent `.app`, запущенный через LaunchServices
  с пустой собственной папкой: AppleEvent Quit завершил процесс и удалил IPC descriptor.
  Первоначальный AppleEvent получает -128, поскольку Avalonia отменяет синхронный Quit
  до завершения async barrier; затем приложение само завершает lifetime.

Native audit вызывает команды реального меню программно. Это не ручная проверка
мышью или signing/notarization приёмка. Logout/reboot, реальные входящие с радио и
состояния Explorer/Windows shell здесь не воспроизводились. Core/library не менялись;
их suites повторно не запускались в S3.

```sh
dotnet build tools/MeshCoreMessenger.ViewportAudit -c Release --no-restore
dotnet tools/MeshCoreMessenger.ViewportAudit/bin/Release/net10.0/MeshCoreMessenger.ViewportAudit.dll --tray-only
```

## Ручная проверка macOS / Windows

Сначала отдельная пустая папка, без автоподключения. Затем привычная тестовая история.

- [ ] Значок виден в menu bar / системном трее (в том числе в скрытых значках Windows).
  Проверить светлую/тёмную тему ОС и читаемость glyph.
- [ ] Меню мышью содержит «Открыть» и «Выйти»; «Открыть» возвращает окно и фокус.
  На Windows левый клик также открывает окно.
- [ ] Крестик по умолчанию и кнопка сворачивания скрывают окно. Повторить несколько
  циклов; history/selection/draft/scroll сохранены, подключение не перезапускается.
- [ ] Maximized → свернуть → «Открыть» / второй запуск: вернуть Maximized.
- [ ] Второй запуск с той же папкой поднимает скрытое окно; другой каталог независим.
- [ ] Выбор «Закрывать приложение» меняет крестик на полный выход. После перезапуска
  выбор сохранён. Сворачивание по-прежнему отправляет окно в трей.
- [ ] «Выйти» из скрытого окна завершает процесс; повторный запуск штатный, настройки
  и черновики сохранены. После выхода значок исчезает.
- [ ] На macOS Cmd+Q / меню приложения / Dock Quit завершают приложение при обоих
  режимах крестика, в том числе при скрытом окне.
- [ ] При воспроизводимой ошибке сохранения на тестовых данных выход отменён, окно
  показывает существующую ошибку; повтор «Выйти» после восстановления записи завершает.
- [ ] При подключённой тестовой ноде входящие в фоне остаются непрочитанными, текущая
  отправка завершает прежний workflow; Show не запускает новое подключение/отправку.

Windows пакет можно собрать прежним publish workflow, например:

```powershell
dotnet publish src/MeshCoreMessenger.Desktop -c Release -r win-x64 --self-contained true -p:PublishTrimmed=false -p:PublishSingleFile=false -o artifacts/windows/s3-win-x64
$s3Data = Join-Path $env:TEMP 'MeshCoreMessenger-S3-Manual'
& 'artifacts/windows/s3-win-x64/MeshCoreMessenger.Desktop.exe' --data-dir $s3Data
```

Windows пакет здесь не собирался и не запускался. Использовать одну и ту же папку
в обоих запусках; запуск без аргументов выбирает пользовательскую default папку.

## Проверенные API

[TrayIcon Avalonia 12.1.3](https://github.com/AvaloniaUI/Avalonia/blob/12.1.3/src/Avalonia.Controls/TrayIcon.cs)
и [ClassicDesktopStyleApplicationLifetime](https://github.com/AvaloniaUI/Avalonia/blob/12.1.3/src/Avalonia.Controls/ApplicationLifetimes/ClassicDesktopStyleApplicationLifetime.cs).
В этой версии IsOSShutdown у ShutdownRequestedEventArgs не является публичным API;
различение обычного Quit и завершения ОС делается по публичному WindowCloseReason.
