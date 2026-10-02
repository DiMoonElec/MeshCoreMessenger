# B6 — ConnectionSupervisor, автоподключение и reconnect (выполнено 28.09.2026)

[Оглавление](../../MESSENGER_PLAN.md) · [Маршрутизация чтения](../../README.md)

> Исторический результат B6. Правило mismatch из-за expected key профиля отменено
> в [B7.5](b7-5-identity.md); остальные lifecycle/generation/retry инварианты сохраняются.

#### B6 — ConnectionSupervisor, автоподключение и reconnect (выполнено 28.09.2026)

**Цель:** сделать единственного владельца активной попытки и всех переходов
`Offline/Connecting/Identifying/Synchronizing/Online/RetryWaiting/Disconnecting/
NeedsAttention`.

- Supervisor имеет один сериализованный control loop и один generation token.
  `StartAutoConnect`, `ConnectNow`, `Disconnect`, `SwitchProfile`, shutdown и wake
  посылают команды этому loop; они не создают параллельные reconnect-задачи.
- При старте после показа offline-истории читать выбранный профиль и запускать одну
  фоновую попытку только при `AutoConnect=true`. Ручное отключение отменяет попытку
  и retry до явного Connect либо следующего запуска. Смена профиля дожидается полного
  закрытия старой сессии до открытия новой.
- Реализовать задержки 1, 2, 4, 8, 15, 30 секунд, затем 30 секунд с jitter ±20%; после
  60 секунд устойчивого Online сбрасывать счётчик. Время, delay и источник jitter
  внедряются интерфейсами для детерминированных тестов.
- К retry относятся временные TCP/Serial ошибки, unplug и занятый/отсутствующий порт.
  Невалидный профиль, неожиданный ключ ноды, несовместимая прошивка и ошибка БД
  переходят в `NeedsAttention` без цикла. Причина и время следующей попытки входят
  в immutable state snapshot для UI.
- State callbacks старой generation не меняют новую state machine. Уже принятые
  сообщения старой сессии при этом не отбрасываются: B4 завершает их по сохранённым
  SessionId/NodeId.
- Добавить `IPlatformPowerEvents`: при suspend остановить/пометить сессию, при wake
  сериализованно проверить или пересоздать соединение; открытый socket/port сам по
  себе не считать доказательством связи. Реализации macOS/Windows держать в Desktop,
  Linux — через тот же интерфейс без отдельной модели приложения.

Обязательные тесты B6: точная последовательность состояний; только одна активная
попытка; deterministic backoff/jitter/reset; ConnectNow прерывает delay без второго
loop; ручной stop; смена профиля; transient/permanent classification; late callback
старой generation; suspend/wake; shutdown во время Connecting, Synchronizing и
RetryWaiting. Тесты используют fake session/time/power events.

##### B6.1 — базовый supervisor и reconnect (выполнено 28.09.2026)

Реализован один сериализованный control loop с immutable snapshot состояний
`Offline/Connecting/Identifying/Synchronizing/Online/RetryWaiting/Disconnecting/
NeedsAttention`. `StartAutoConnect`, `ConnectNow`, ручное отключение и shutdown не
создают параллельных циклов. Каждая новая generation получает новый
`CompanionSession` и `ReceiveCoordinator`; следующая попытка начинается только после
полного завершения предыдущей.

Attempt lifecycle передаётся supervisor отдельным сигналом и не читает
`CompanionSession.Events`: единственным consumer этой очереди остаётся
`ReceiveCoordinator`. Закрытие разделено на quiesce новых drain, остановку сессии с
library event barrier, дочитывание application events и ingest barrier. Событие,
пришедшее во время финального session barrier, покрыто отдельным regression test и
коммитится до завершения attempt.

Добавлены transient/permanent classification, учёт `Reconnect=false`, задержки
1/2/4/8/15/30 секунд с injectable jitter ±20%, сброс backoff после 60 секунд Online
и generation guard для поздних progress/lifecycle callbacks. Timeout
`SYNC_NEXT_MESSAGE` закрывает старую session и создаёт новую; mismatch ключа,
ошибки БД/ingest и несовместимые/невалидные настройки переходят в `NeedsAttention`
без reconnect loop. Supervisor не имеет API отправки, advert или mutation.

Проверено: полный Release build без предупреждений; Core tests — 80/80, Desktop —
18/18, MeshCoreSharp regression suite — 92/92. Тесты используют fake attempts,
управляемое время/delay/jitter и временную SQLite. Физическая нода не использовалась.

Классификация неожиданного ключа как permanent failure позднее отменена в B7.5:
другой ключ через тот же endpoint теперь является нормальным результатом Identify.
Остальные правила `NeedsAttention`, reconnect и generation guard не изменены.

В B6.1 намеренно не входили смена профиля, suspend/wake, platform power adapters,
Desktop startup/autoconnect и UI состояния. Первые три части выполнены в B6.2;
Desktop-интеграция и исходный checklist всего этапа B остаются для B7.

##### B6.2 — смена профиля и suspend/wake (выполнено 28.09.2026)

Добавлен `SwitchProfile`: существующий профиль выбирается отдельной операцией без
перезаписи его полей, retry отменяется, а новая attempt/generation создаётся только
после полного `StopAsync`/dispose предыдущей. Смена во время suspend только сохраняет
выбор; соединение открывается после wake. Поздние callbacks прежнего профиля
отсекаются тем же generation guard, параллельных attempts нет.

Добавлен `IPlatformPowerEvents`. Его callbacks только ставят suspend/wake в общий
control loop. Suspend отменяет startup/retry или полностью закрывает Online attempt;
wake всегда создаёт новую session и не доверяет прежнему socket/port. Manual
disconnect остаётся Offline, а `NeedsAttention` сохраняется без автоматического
reconnect. Повторные power-события идемпотентны; shutdown отписывает supervisor.

В Desktop реализованы adapters Windows через message-only window и
`WM_POWERBROADCAST`, macOS через `IORegisterForSystemPower`/CFRunLoop с обязательным
acknowledgement sleep, а для остальных платформ — no-op того же интерфейса. Новых
NuGet-зависимостей нет. macOS adapter прошёл локальный start/stop smoke-test;
Windows message mapping проверен детерминированно, но на Windows не запускался.

Проверено: полный Release build без предупреждений; Core tests — 94/94, Desktop —
21/21, MeshCoreSharp regression suite — 92/92. Fake-тесты покрывают switch из Online
и RetryWaiting, switch во время suspend, suspend/wake из Connecting, Synchronizing,
Online и RetryWaiting, duplicate wake, manual disconnect, `NeedsAttention`, late
callback и отсутствие перекрывающихся attempts. Физическая нода не использовалась.

Регистрация supervisor/power events в Desktop DI, startup/autoconnect и UI состояния
намеренно не добавлены: это граница B7. SQLite schema и MeshCoreSharp/protocol не
изменялись.
