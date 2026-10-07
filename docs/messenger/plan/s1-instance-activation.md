# S1 — активация существующего экземпляра

[Stage S](stage-s.md) · **Статус: реализован; автоматическая и macOS native части проверены. Основной Windows-сценарий подтверждён пользователем 07.10.2026.**

**Цель:** повторный запуск с той же папкой данных показывает существующее окно,
не открывая вторую БД и не создавая второе соединение.

**Scope:** дополнить `ApplicationInstanceLock` локальным IPC; кандидат —
`NamedPipeServerStream/NamedPipeClientStream`. Сохранить OS file lock как единственный
арбитр владения папкой. Имя endpoint связано с пользователем и идентичностью папки;
доступ локальный, текущему пользователю, протокол содержит только ограниченный
запрос активации и ответ, без передачи произвольных команд.

Первый экземпляр начинает принимать запросы сразу после захвата лока, до долгого
открытия БД/загрузки VM. До готовности UI запрос сохраняется; после готовности
единое действие через UI dispatcher показывает окно, восстанавливает Normal/
Maximized и активирует его. Не пересоздавать окно и workspace owners.
Повторный запуск ждёт подтверждения с ограниченным timeout и завершается до
SQLite/DI/connection lifecycle; успешная передача активации даёт exit 0.

При отсутствии ответа диагностировать неудачу, не обходить занятый лок.
Если владелец завершился, допустим ограниченный повтор захвата лока и обычный startup.
Во время shutdown не подтверждать показ уже уничтоженного окна.
Обработать также обычный macOS reopen, который может прийти без нового процесса.

**Не входит:** трей, настройки, глобальный запрет разных папок, выбор аккаунта через
UI, изменения автоподключения/reconnect, внешние сетевые команды.
**Зависимости:** существующие Program/App, DesktopAppPaths/DataDirectorySelection,
[data directories](../architecture/data-directories.md), [shutdown](../architecture/shutdown.md).

**Тесты:** реальный IPC между процессами на временной папке; одновременный старт;
запрос до готовности UI; несколько запросов; timeout/crash/restart; startup error;
shutdown race; один владелец SQLite; нормализованные пути/регистр/symlink aliases
не направляют активацию не тому владельцу; разные папки не конфликтуют.
Нулевые connection commands от действия показа окна.

**Готово:** занятую папку обслуживает прежний экземпляр; failure не ослабляет lock;
endpoint освобождается корректно, file lock удерживается до закрытия хранилища.
Кратковременный процесс повторного запуска допустим; второго рабочего экземпляра нет.
**Ручная проверка: обязательна** — macOS/Windows, прямой запуск и установленный пакет,
свёрнутое/неактивное окно, macOS Dock/reopen и серия быстрых запусков.
Возврат скрытого окна из трея допроверяется в S3/S7.

## Реализация 07.10.2026

Desktop `ApplicationInstanceCoordinator` создаётся в Program до SQLite/DI и владеет
прежним ApplicationInstanceLock до закрытия хранилища. Лок не заменён PID/IPC.
NamedPipeServerStream/ClientStream работают с Asynchronous/CurrentUserOnly;
на Windows используется native pipe, на macOS — Unix socket.

Владелец атомарно публикует `.meshcoremessenger.instance.json` в своей папке:
версия, PID, короткое случайное имя pipe и InstanceId. Альтернативные пути к одной
папке читают тот же descriptor; имя не вычисляется из строки пути.
Descriptor ограничен 1024 байтами, Unix permissions — owner read/write.
Handshake проверяет version/InstanceId/PID, wire request — только Activate;
другие команды не выполняются. Windows сверяет настоящий server PID через
GetNamedPipeServerProcessId и передаёт foreground permission через AllowSetForegroundWindow.

Второй запуск ждёт не более 8 секунд, после выполненного показа возвращает exit 0
до открытия БД/DI/соединения. Closing/unavailable и timeout дают exit 2 с диагностикой.
Connect/handshake ограничены 1 секундой; server ждёт готовности UI до 10 секунд.
После освобождения лока во время retry допускается обычный запуск нового владельца;
занятый лок не обходится. Stale/invalid descriptor не является основанием takeover.

DesktopActivationCoordinator объединяет ожидающие UI requests, ждёт первого Opened
после восстановления placement и вызывает existing MainWindow через dispatcher.
Отмена одного клиента не отменяет остальных. Closed/Exit прекращают активацию;
текущий shutdown request отклоняет показ, failed shutdown снова позволяет его.
Повторный Show не загружает placement заново; minimized не перезаписывает сохранённое
normal/maximized состояние. Workspace/connection owners не пересоздаются.

App подписывается на IActivatableLifetime/ActivationKind.Reopen; TryLeaveBackground
и единое действие показа используются и для native macOS reopen, и для IPC.
ConnectionLifecycle.StartAsync остаётся одноразовым обработчиком первого Opened.
Core/library, schema, package pins и reconnect policy не менялись.

## Проверки и оставшаяся приёмка

07.10.2026: пользователь подтвердил Windows-проверку: при попытке запуска второго
экземпляра разворачивается существующее окно. Основной пользовательский сценарий S1
принят на обеих целевых платформах. Остальные Windows-сценарии расширенной матрицы
и характеристики пакета этим сообщением не подтверждены.

Результаты, команды и Windows checklist: [отчёт S1](../../testing/s1-instance-activation.md).
Системный трей/новое поведение крестика не включены; это S3.
Отдельные Windows foreground/elevation сценарии и self-contained пакеты обеих ОС требуют отдельной
ручной приёмки; успешный native macOS audit её не заменяет.
