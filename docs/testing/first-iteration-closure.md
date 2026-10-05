# Завершение первой локальной итерации

05.10.2026. Пользователь разрешил завершить доступные автономные проверки
и использовать подключённую ноду. Scope — переписка с заранее настроенными
контактами/каналами на текущем Mac; полный Stage D/E и публичный релиз не закрыты.
Исходное решение: [минимальная итерация](../messenger/plan/first-iteration-readiness.md).

## Изменения

- Убраны no-op пункты «Поиск (тест)», «Поделиться (тест)», «Задать маршрут (тест)».
  Рабочий поиск остаётся в истории; private menu: Details/ResetRoute/ClearHistory,
  public menu: Details/ClearHistory. Обновлены прежние проверки состава меню.
- Добавлен `tools/publish-macos.sh`: self-contained osx-arm64 `.app` и `.zip`,
  без trimming/single-file; ad-hoc codesign/verify. Artifacts игнорируются Git.
  Lock files дополнены RID osx-arm64 без изменения версий pinned packages.
- [Инструкция](../messenger/user-guide.md): запуск, профиль, лимиты/статусы,
  повторы, данные/backup/restore и ограничения.
- Аппаратный runner теперь возвращает exit 1 с сообщением при ошибке,
  вместо unhandled managed exception/SIGABRT и системного crash popup.
  Это диагностическая утилита, не изменение production delivery policy.

## Автоматические и нативные проверки

Debug и Release: Library 105/105, Core 384/384, Desktop 328/328, без failures/skips.
Debug и Release solution builds — 0 warnings/errors.
Core suite включает backup/restore, pending writes, draft/history reopen,
reconnect/suspend/resume и отсутствие автоматического TX после восстановления.

Native private-send: Light/Dark × 420/960, реальный resend menu,
раздельные статусы нового/старого сообщения и сохранение draft.
Native history-clear: Public/Private × Light/Dark × 420/960, актуальный состав
меню, cancel/confirm, сохранение draft и запрет offline reset.
После очистки меню первый старый native assertion ожидал прежнее число items;
он исправлен вместе с новой композицией меню, production clear не менялся.

Контролируемый diagnostic failure: несуществующий serial port,
exit 1, `HARDWARE TEST FAILED`, без abort. На реальной ноде намеренно
воспроизводить тот же timeout повторной отправкой не требовалось.

## Пакет и локальные данные

macOS Apple Silicon; self-contained app построен в
`artifacts/macos/osx-arm64/build.hs0LoD/MeshCoreMessenger.app`, архив рядом.
Info.plist lint и ad-hoc signature verification успешны; native Avalonia,
SQLite и Serial библиотеки включены в пакет. Developer ID/notarization нет.

Production `.app` запущен напрямую с PATH без dotnet и заведомо отсутствующим
DOTNET_ROOT. Окно/Bootstrap/load заработали; второй экземпляр того же data-dir
вернул exit 2. Затем выполнены штатный Quit и повторный запуск/Quit, процесс
завершился exit 0. AppleEvent Quit при асинхронном shutdown мог возвращать -128,
но ожидание процесса подтвердило нормальный exit 0; это согласуется с временным
Cancel в MainWindow.OnClosing до завершения сохранения.

Источник — пользовательская default DB, открытая только для чтения. SQLite backup
сохранён в `tmp/first-iteration-audit/data-copy`; только у этой копии выключены
AutoConnect/Reconnect, чтобы test startup не забирал реальные входящие сообщения.
До/после запуска проверены integrity_check, foreign_key_check и количества:
268 Messages, 187 Contacts, 8 Conversations, 0 Drafts, 124 SendAttempts,
4 ContactDeliveryHistory. Источник не изменялся. Native fixture/Core tests отдельно
покрывают непустой draft; его наличие в этой пользовательской копии не выдумывается.
Это проверка SQLite/native startup на Mac с SDK, скрытым от app, а не отдельная
машина без SDK. Serial работал через library test; packaged Serial connection
не открывался, наличие dylib не заменяет такую проверку.

## Подключённая аппаратная нода

`/dev/cu.usbserial-0001`, 115200, DTR/RTS=true. RnD CatCore🐈, Heltec V3;
firmware v1.17.1-d929643, protocol 13, build 14-Aug-2026.
Прочитано 188 контактов, 40 channel slots, 4 named; errors=0, outbound queue=0.
Секреты каналов/ключи не публикуются в отчёте.

| Проверка | Результат |
| --- | --- |
| Read-only start/device/directory/stats | 56 allowlisted commands, TX counters неизменны |
| Serial close/open | uptime 3223 → 3227 секунд, no reset observed, 4 read-only commands |
| Один ЛС RnD Mesh01 (DF73015A6BB9) | route flood/0xFF, timestamp 1791208455, expected ACK 0x2DED13EE, MSG_SENT принят; ACK не получен, TimedOut |
| Радиосчётчики ЛС | TX 1→2, flood 1→2, RX 31→32; ровно одна send command, без повторов |
| Один публичный текст #test, slot 1 | OK/Accepted, timestamp 1791208567; TX 2→3, flood 2→3, RX 33→33 |
| Один ЛС SMK Mesh01 (0D7778385667) | direct/0x40, timestamp 1791209338, MSG_SENT; matching ACK 0xACF38516, Confirmed, RTT 2765 ms |
| Радиосчётчики direct ЛС | TX 3→4, direct 0→1, flood остаётся 3, RX 40→41; ровно одна send command |

Тексты явно диагностические («…при разработке MeshCoreSharp. Ответ не требуется»).
Получение канального текста удалённой нодой не подтверждено: ACK у канала нет.
Flood ЛС timeout не устанавливает причину — отсутствующая/недоступная удалённая нода,
радиопрохождение или другой фактор; ACK success для flood не объявляется. Старый runner
после этого результата вызвал abort из-за unhandled assertion; исправление runner
описано выше. Это не падение production Messenger и не основание менять send logic.

После flood timeout проверен другой контакт с недавней успешной доставкой:
SMK Mesh01 подтвердил direct ЛС. Diagnostic runner получил optional третьим
аргументом шестибайтовый hex prefix; проверяет длину до открытия порта и
единственность контакта до TX. Неверная длина проверена: exit 1 без открытия порта.
Успех direct не доказывает исправность дальнего/flood пути.

Hardware tests использовали AutoReceiveMessages=false: очередь сообщений ноды
не читалась. Каналы, контакты и radio settings не менялись командами тестов.

## Что требует дальнейшего участия пользователя

- Двусторонний обмен текстами и дальний multi-hop/fallback; direct ACK уже
  подтверждён на SMK Mesh01; [оставшаяся матрица P8](private-retries-acceptance.md).
- Физическое USB unplug/replug и реальный сон/пробуждение/заблокированный дисплей
  production Desktop. Компьютер пользователя намеренно не усыплялся и не блокировался.
- Подключение к ноде именно из готового `.app`; затем длительное повседневное
  использование. Windows/Intel Mac и внешняя подпись/дистрибуция не проверены.

Доступная автономная часть завершена; сборка пригодна для начала ограниченного
личного использования, но неподтверждённая аппаратная доставка остаётся явным
ограничением. Новые Tools/contact/channel mutations не начаты.
