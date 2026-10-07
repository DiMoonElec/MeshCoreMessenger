# S6 — системные уведомления macOS и Windows

[Stage S](stage-s.md) · **Статус: реализация подготовлена 07.10.2026; ручная приёмка macOS/Windows ожидается.**

**Цель:** простое системное уведомление и безопасный возврат в нужную переписку.

**Scope:** небольшой платформенный adapter к общему DesktopNotificationService S5; сначала macOS, затем Windows,
результаты и ограничения каждой ОС фиксируются отдельно.
**Linux отложен по решению пользователя:** отдельный адаптер и приёмка позднее;
в S6 реализуются только Windows и macOS.
До выбора зависимости проверить показ и click activation в настоящем формате
поставки: macOS .app с существующим bundle identity, Windows portable directory.
Выбрать минимальный поддерживаемый способ без новых application projects.
Кандидаты: macOS UserNotifications, Windows native app notifications;
окончательный bridge/package определяется проверкой совместимости, не этим планом.

Разрешение macOS запрашивается в понятном контексте включения уведомлений;
не повторять запрос при каждом сообщении/старте. Отказ ОС не выключает приём
и не вызывает повторных диалогов; экран может показать короткую системную подсказку.
Чекбоксы предпочтений и фактическая доступность платформы — разные состояния.
Режимы Focus/Do Not Disturb и правила ОС уважаются.

Клик маршрутизируется в общее действие показа S1, затем существующую навигацию
по node/conversation/message identity. Учесть native activation уже работающего
приложения и запуск процесса ОС; активировать владельца нужной папки данных.
Не переключать подключение и не выдавать историю другой ноды за текущую.
Для удалённой/недоступной цели или несовпадающей node context показать окно
с понятным fallback, не выполнять connect/mutation.
Для первой итерации не добавлять inline reply и управление нодой из уведомления.

Не помещать текст сообщения/ключи в launch arguments и обычные логи.
Для старых баннеров после выхода/смены папки проверить маршрутизацию и очистку,
не делать неограниченное ожидание IPC.

**Не входит:** remote push/APNs/WNS, сервер уведомлений, Linux приёмка,
переработка автоподключения, обязательная смена Windows portable на MSIX.
**Зависимости:** S1, S5; S3 для финальной проверки hidden/tray. S4 не блокирует
адаптеры, но новые пользовательские подписи должны соответствовать S4.

**Тесты:** разрешено/отказано/недоступно, callbacks на non-UI thread, уже запущен/
cold start/занята нужная папка, разные папки, stale target, смена/удаление переписки,
shutdown race, нулевые connection/mutation calls от click, ошибки adapter.
Проверить runtime registration, native dependencies и identity в пакетах.

**Готово:** на каждой проверенной ОС banner/click работают в self-contained пакете,
отказ или отсутствие поддержки не мешают переписке; непроверенные платформы
не объявляются поддержанными.
**Ручная проверка: обязательна** — реальные macOS .app и Windows portable,
разрешения, фоновое/скрытое окно, клик, повторный запуск, Focus/DND, rename/package move.

**Источники:** [Apple UserNotifications](https://developer.apple.com/documentation/usernotifications),
[разрешения Apple](https://developer.apple.com/documentation/usernotifications/asking-permission-to-use-notifications),
[Microsoft app notifications](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-quickstart).
Конкретный способ регистрации зависит от выбранного API и формата поставки.

## Реализация 07.10.2026

Общий `IDesktopNotificationAdapter` реализован через `NativeNotificationAdapter`.
Он присваивает непрозрачный token, заменяет предыдущий баннер группы, ограничивает
число целей и очищает их при выходе. Message policy и очередь S5 сохранены.
Платформенный интерфейс дополнительно сообщает доступность, запрашивает разрешение,
принимает click callbacks и удаляет уведомления.

macOS: небольшой Objective-C bridge к `UserNotifications`, включаемый в `.app`;
запрос разрешения выполняется кнопкой «Разрешить в системе» в существующих настройках.
`dotnet run` показывает подсказку о запуске `.app`, без обращения к неподдерживаемому
CLI-host API. Windows: `Microsoft.WindowsAppSDK.Foundation` 2.0.20,
`AppNotificationManager`/`AppNotificationBuilder`, portable/self-contained native SDK.
GUI-проекты используют Windows TFM/RID и отдельные Windows lock-файлы;
Core и Companion library остаются платформенно независимыми.
Linux использует адаптер без показа и остаётся отложенным.

В настройках остаются два checkbox S2; рядом — фактический статус ОС и кнопка
первичного разрешения macOS. Отказ не вызывает повторных permission dialogs.
При попытке доставки статус ОС перечитывается, чтобы учитывать изменения разрешения.
Focus/DND могут подавлять баннер независимо от настроек приложения.

`NotificationTargetRegistry` хранит максимум 128 небольших файлов с GUID-token,
папкой данных и node/conversation/message ID, сроком два дня. Текст/ключи туда и
в launch arguments не попадают. При штатном выходе собственные баннеры и цели
удаляются; после аварии оставшиеся записи позволяют разрешить cold-start click.
Неправильный/просроченный token даёт обычный показ окна.

`NotificationClickController` направляет callback в S1. Для другой занятой папки
используется её IPC; при отсутствии владельца запускается приложение с token,
без открытия чужой SQLite текущим процессом. Program разрешает cold-start token
до открытия БД; Windows cold COM payload читается через AppLifecycle после
notification registration, с ограниченным ожиданием. S1 дополнен bounded командой с тремя GUID. Затем UI router проверяет
текущую ноду/модальный диалог, читает локальную позицию и открывает сообщение через
существующий history jump. Недоступная цель показывает fallback; connect/mutation
из router не вызываются. Обычная startup policy подключения не менялась.

При выходе сначала отменяются S5 workers и click tasks, затем выполняется native
cleanup и существующий UI/durable shutdown. Ошибка удаления баннера не блокирует
сохранение истории. [Проверки и ручные сценарии](../../testing/s6-native-notifications.md).
