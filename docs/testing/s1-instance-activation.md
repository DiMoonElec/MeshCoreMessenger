# S1 — активация существующего экземпляра

[План S1](../messenger/plan/s1-instance-activation.md) · [Ветка S](../messenger/plan/stage-s.md)

07.10.2026: **реализация и автоматические проверки завершены; macOS native/реальный
reopen проверены. Пользователь подтвердил на Windows восстановление существующего
окна при попытке запуска второго экземпляра.**
Трей и новая настройка крестика не включены, S2–S7 не начаты.

## Что изменено

- Program до SQLite/DI создаёт ApplicationInstanceCoordinator: прежний file lock
  и ранний local pipe listener. На занятой папке выполняется только IPC-активация.
- Endpoint публикуется атомарно в `.meshcoremessenger.instance.json` той же папки;
  UUID/PID/version проверяются в handshake. Path aliases не получают отдельный
  endpoint из-за различия строк пути. Descriptor ограничен 1024 байтами.
- CurrentUserOnly/Asynchronous; Windows проверяет server PID через kernel32 и
  использует AllowSetForegroundWindow перед Activate. Внешний TCP listener не создаётся.
- Общий DesktopActivationCoordinator ждёт готовности existing MainWindow, объединяет
  pending UI requests и отправляет callback через dispatcher. Closing/Closed отклоняют
  показ; после failed shutdown callback снова допустим. Отмена клиента не отменяет
  запрос остальных callers.
- MainWindow сохраняет состояние до минимизации; повторный Show не применяет
  startup placement заново. Выбор/история/черновик и connection owners сохраняются.
- macOS Reopen обрабатывается штатным IActivatableLifetime, с TryLeaveBackground
  и тем же действием показа. Настоящий shutdown и владение БД/локом сохранены.

## Автоматические проверки

Debug и Release solution builds: **0 warnings/errors**.
Desktop: **345/345 в Debug и Release**, включая 17 новых S1 regressions.
Release Core: **384/384**; Library: **105/105**.

Проверены: публикация до UI/БД; настоящий второй процесс с exit 0 и без SQLite;
четыре быстрых вторых процесса; coalescing/отмена одного caller; early IPC дольше
handshake budget; прекращение pending callbacks при exit; closing/failed shutdown;
ошибка UI не отключает listener; malformed command; stale/invalid/oversized descriptor;
освобождение лока во время retry; одновременный старт; разные папки; normalized,
Unix symlink и доступные на текущей FS case aliases. Прежние lock/read-only/startup
ошибки и Debug fixture guards проходят.

Сборка без повторного network restore:

```sh
dotnet build MeshCoreSharp.sln -c Release --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false
dotnet build MeshCoreSharp.sln -c Debug --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false
```

Тесты запущены непосредственно через executable xUnit v3 assemblies:

```sh
dotnet tests/MeshCoreMessenger.Desktop.Tests/bin/Release/net10.0/MeshCoreMessenger.Desktop.Tests.dll -noColor
dotnet tests/MeshCoreMessenger.Desktop.Tests/bin/Debug/net10.0/MeshCoreMessenger.Desktop.Tests.dll -noColor
dotnet tests/MeshCoreMessenger.Core.Tests/bin/Release/net10.0/MeshCoreMessenger.Core.Tests.dll -noColor
dotnet tests/MeshCoreSharp.Tests/bin/Release/net10.0/MeshCoreSharp.Tests.dll
```

В sandbox запрещены Unix socket/TCP bind: первоначальные запуски с bind завершались
Permission denied. Успешные IPC/Core прогоны выполнены с доступом к локальным sockets,
на временных папках и loopback fake Companion; к аппаратной ноде не подключались.

## macOS native

ОС: macOS 27.0.1 (26A434), arm64; .NET runtime 10.0.9, Avalonia 12.1.3.
Display preflight CoreGraphics=0/CVDisplayLink=0.

```sh
dotnet build tools/MeshCoreMessenger.ViewportAudit -c Release --no-restore
dotnet tools/MeshCoreMessenger.ViewportAudit/bin/Release/net10.0/MeshCoreMessenger.ViewportAudit.dll --instance-activation-only
```

Production MainWindow + настоящая временная SQLite/fake supervisor + real secondary
process проверены в состояниях Normal, Minimized, Hidden и Maximized → Minimized.
Во всех случаях exit 0, окно visible/active, минимизация снята; Maximized восстановлен.
Selection/history objects, draft и hidden placement сохранены; ConnectCalls=0.
Закрытое окно не принимает повторную активацию.
В том же production MainWindow с управляемым shutdown adapter проверен незавершённый
выход: второй процесс получает exit 2. После имитированной ошибки сохранения
shutdown отменён, следующий второй процесс получает exit 0 и активирует то же окно.

Дополнительно собран отдельный временный framework-dependent `.app` из Release
output с audit bundle identity и пустой собственной папкой данных. LaunchServices
`open -n ... --args --data-dir ...` запустил экземпляр; обычный повторный `open`
доставил Reopen. App зарегистрировал успешный показ; NSWorkspace frontmost PID
совпал с PID владельца descriptor, новый владелец не появился. Штатный AppleEvent
Quit завершил приложение и удалил descriptor. Попытка NSRunningApplication.hide
в этом пакетном сценарии вернула false: скрытие всего приложения этой проверкой
не подтверждено; отдельное скрытие окна подтверждено native audit выше.

На другой пустой временной папке выполнен настоящий SIGKILL только audit process:
descriptor остался, повторный startup открыл SQLite и создал новый InstanceId;
следующий второй процесс получил exit 0. Restarted owner штатно закрылся, descriptor
удалён. Пользовательская папка/радио не использовались.

Это проверка App/reopen/IPC в `.app`, **не self-contained/signing/notarization
приёмка нового релиза**. Полная поставка остаётся Stage E/S7.

## Windows — чек-лист пользователя

07.10.2026: пользователь выполнил проверку на Windows и подтвердил, что функционал
работает: при попытке запуска второго экземпляра разворачивается существующее окно.
Основной сценарий S1 на Windows принят. Версия ОС, формат пакета, отдельная проверка
keyboard focus и результаты остальных пунктов расширенной матрицы не сообщены;
они не отмечаются автоматически как пройденные. Агент Windows-проверку не выполнял.

Проверять обычную пользовательскую сессию, сначала offline на отдельной пустой папке.
Для сборки из исходников нужен SDK из global.json; portable пакет можно подготовить:

```powershell
dotnet publish src/MeshCoreMessenger.Desktop -c Release -r win-x64 --self-contained true -p:PublishTrimmed=false -p:PublishSingleFile=false -o artifacts/windows/s1-win-x64
$s1Data = Join-Path $env:TEMP 'MeshCoreMessenger-S1-Manual'
$s1Exe = Resolve-Path 'artifacts/windows/s1-win-x64/MeshCoreMessenger.Desktop.exe'
& $s1Exe --data-dir $s1Data
```

Этот Windows пакет здесь не собран и на Windows не запускался.
Вторая копия/новый терминал используют тот же `$s1Data`; при запуске из Проводника
без аргументов используется стандартная папка, поэтому оба запуска должны быть
в одном режиме. Не включать автоподключение в тестовой папке.

- [ ] Повторить запуск при видимом, но неактивном окне: вернуть то же окно и фокус.
- [x] Свернуть окно, повторить запуск: существующее окно разворачивается;
  подтверждено пользователем 07.10.2026. Передача keyboard focus отдельно не уточнялась.
- [ ] Максимизировать, свернуть, повторить: восстановить Maximized.
- [ ] Сделать несколько быстрых запусков, в том числе сразу при первом старте:
  одно постоянное окно/процесс; вторые процессы заканчиваются, история не сбрасывается.
- [ ] На тестовой истории убедиться, что selection/draft/scroll сохранены.
- [ ] Запустить с другой папкой: независимое окно; повтор первой папки активирует первое.
- [ ] Штатно закрыть, дождаться завершения процесса, запустить снова: нормальное открытие.
- [ ] Во время выхода повторить запуск: допустим bounded отказ либо новый владелец
  после освобождения лока; второго владельца БД не появляется.
- [ ] На пустой тестовой папке аварийно завершить **только тестовый процесс**, запустить
  снова: stale descriptor не мешает; следующий повтор активирует новый экземпляр.
- [ ] Если проверяется обычный/elevated запуск, зафиксировать результат отдельно:
  CurrentUserOnly проверяет elevation, поэтому допустим безопасный отказ активации;
  занятой лок не обходится. Основной пользовательский сценарий — одинаковый уровень.

Записать версию Windows, способ запуска, state до/после, foreground/focus и ошибки.
Windows запрещает принудительный foreground в некоторых условиях; раскрытие окна
и передача клавиатурного фокуса проверяются отдельно. Трей/крестик пока не тестировать
как новую функцию: они остаются прежними до S3.
