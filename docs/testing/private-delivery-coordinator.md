# P4 — session-owned очередь и автоматические flood-попытки

05.10.2026. [План](../messenger/plan/d7-private-auto-retry.md).
P3 закоммичен как `ce0fd41`.

## Реализация

Production Desktop DI включает PrivateDeliveryCoordinator. SendPrivateAsync
сохраняет текст/политику и возвращает Prepared + Queued после принятия job,
не ожидая MSG_SENT и всех ACK. Capacity reservation выполняется до Prepare и
transfer draft: максимум 32 jobs, 8 на контакт. Ключ FIFO — собственная нода,
session/generation и полный ключ контакта. Следующий job того же контакта ждёт
завершения предыдущего. Отмена среднего job не разрешает последующему обогнать
ещё активный предыдущий. Операционный guard удерживается для очереди и всего job.

Parent lease регистрирует весь workflow до возврата фасада. Каждый TX получает
отдельный child lease исходного scope с единственным BindOutgoing/invocation;
current gateway не используется для следующей попытки. Cancellation/disconnect
останавливают job; cleanup и durable статусы входят в session drain barrier.
Prepared jobs после restart/reconnect не воспроизводятся.

Исходный flood: T1/attempt 0,1,2 или новые T1/T2/T3 с attempt 0, один MessageId.
Перед каждой попыткой проверяются сессия, контакт, текущий маршрут и durable итог.
Только достоверный library TimedOut разрешает следующий TX. ERROR, ExpectedAck=0,
Unknown, persistence pause, неверный/missing контакт прекращают цикл. При изменении
flood на known job останавливается: автоматический reset добавляется в P5.

ACK любой попытки через общий commit P3 пробуждает job; текущий waiter отменяется
отдельно от parent/очереди. Immediate gate занят лишь read/prepare/Sending/MSG_SENT,
а ожидания разных контактов и channel commands продолжаются. Окно AckTracker
библиотеки сохраняется. Already-invoked TX может завершиться после raced ACK;
следующей попытки после durable Delivered нет.

Progress читается вместе с history: Sending n/3 между попытками, итог Delivered
приоритетнее последней attempt, после исчерпания бюджета — красная Ошибка.
Сохраняются configured route, PC-время начала Sending (не эфирное время), offset/
timezone, реальный flood flag MSG_SENT и оценочный wall-clock deadline по bounds
библиотеки. Library monotonic timer остаётся источником TimedOut. SQLite v5 не меняется.

## Проверки

Release build: **0 warnings/errors**; библиотека **105/105**, Core **330/330**,
Desktop **318/318**. Добавлено 18 Core и 3 Desktop тестовых случая:

- Оба режима: ровно три передачи после timeout, корректные timestamp/attempt и
  различные protocol ACK tags, один исходящий пузырёк.
- Первый ACK → один TX, сохранённые route/mode/PC time; ACK второй и поздний ACK
  первой во время третьей → остановка и общий Delivered.
- Два сообщения одному контакту идут FIFO с разными сетевыми идентичностями.
  Разные контакты ждут ACK параллельно, ACK в обратном порядке не перепутаны.
- Channel command проходит во время private ACK wait.
- Отмена среднего queued job сохраняет FIFO; per-contact/global capacity освобождается,
  отказ не переносит draft и не создаёт лишнее сообщение. Mutations блокируются guard.
- Disconnect останавливает активное/queued сообщения. Новый TCP-эмулятор той же ноды
  не получает старые сообщения после reconnect; явная новая отправка проходит.
- Явный node ERROR и отсутствие ExpectedAck не запускают автоматические повторы.
- Virtual TimeProvider завершает три часовых ожидания без реального ожидания часов.
- SQLite prepare/Accepted failure: pause, durable finish, Retry/Flush без повторного TX.
- Known route сохраняет одноразовый D6 baseline.
- Desktop: Active/Delivered/Unconfirmed цикла имеют приоритет над последней attempt;
  Active показывает «Отправка (попытка 2/3)», финальная ошибка делает пузырёк красным.

TCP-эмуляция использует настоящую Companion-библиотеку. Аппаратная нода и native
GUI audit не использовались; совместная визуальная/аппаратная приёмка — P8/пользователь.
При первом полном запуске один существующий SQLite cancellation test попал в гонку
interrupt до начала SQL; повтор полного набора прошёл. Реализация DatabaseReader
в этом этапе не менялась.

## Граница части

Known-route ещё не получает 3+2: он отправляется один раз, как D6. Следующий P5
добавит owned reset/readback и переход к двум flood-передачам с новой сетевой
идентичностью. Аналитический read API/learned enrichment и ingress dedup — P6/P7.
Конструктор MessageService без optional coordinator сохраняет прежний режим;
production DI использует новый исполнитель.
