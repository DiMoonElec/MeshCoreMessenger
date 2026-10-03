# D5 — явная отправка в канал

[Stage D](stage-d.md) · **Статус: реализован и автоматически проверен 03.10.2026.**

**Цель:** первый законченный send-сценарий без сложности ACK.

**Scope:** MessageService каналов: capture target/text/revision, validate/admission,
Prepared → Sending commits → один SendChannelTextAsync → AcceptedByNode/Failed/Unknown.
Перед wire проверить binding fingerprint/version и фактическую ноду; если slots
несколько — явный выбор текущего, не первое совпадение. Подключить UI D1,
Enter/Shift+Enter с IME, single-flight, post-commit пузырь/превью без reset scroll.
Draft очищается только для переданной message store revision; новый текст не стирать.

**Не входит:** private ACK, manual retry, создание/изменение канала, скрытые повторы.
**Зависимости:** D1–D4; sending/identity.

**Тесты:** exact commit/one wire ordering; БД отказ → zero TX; ERROR vs timeout/обрыв;
binding/node change между click и admission; два клика/Enter/IME; draft edit в процессе;
status update не создаёт строку; hidden UI/shutdown; outgoing unread/viewport;
workspace independence; channel никогда не показывает Delivered.

**Готово:** production Core send с честным AcceptedByNode/crash-safe Unknown,
полный regression набор. **Ручная проверка: обязательна** — fake adapter/UI;
затем при разрешении один помеченный текст в согласованный канал.


## Результат D5

`MessageService` подключён в production DI. Нажатие кнопки/Enter захватывает
node/session/generation, полный fingerprint/versioned binding, текст и ревизию
черновика. Текст обрабатывается через общий `IOutgoingTextProcessor` с именем
фактической ноды; сохранённый TransmissionText используется lease без повторной
обработки. Локальный draft store материализует новый диалог перед atomic Prepare.
Prepared и Sending коммитятся до единственного вызова канального API.

После OK сохраняется Accepted с NotExpected и отображается «Принято нодой».
Явный firmware ERROR — Failed; неопределённость после invocation — Unknown.
Сбой записи статуса переводит подключение в NeedsAttention; явное восстановление
пишет удержанные статусы без повторной передачи. Ручной повтор и private ACK
остаются D7/D6. Данные об услышанных ретрансляторах не выдумываются.

Draft buffer очищает только совпавшие owner/text/revision после передачи
ответственности outgoing store; новый набранный текст не стирается. UI принимает
очистку только при том же текущем owner/revision. При offline поле редактируется,
кнопка выключена; oversize по-прежнему блокируется валидатором. Будущее обрезание
ввода из D2 здесь не реализовано.

Если у fingerprint несколько активных слотов, composer требует явный выбор.
Список не пересоздаётся при каждом readiness refresh, чтобы native ComboBox
сохранял выбор. Перед wire binding повторно проверяется D4 gateway. Enter во
время preedit IME не отправляет; Shift+Enter сохраняет многострочный ввод.
Повтор кнопки блокируется UI и Core single-flight.

Исходящие post-commit invalidations подключены к существующему projection worker.
Виден один пузырёк; статусы меняют его metadata с сохранением viewport, не добавляя
сообщений/unread. При просмотре старой истории её окно сохраняется. Подгрузка
нового интервала включает interleaved входящие сообщения. Public/Private owners
остаются независимыми. Прирост MainWindowViewModel ограничен DI/subscription/
очередью уведомлений; send workflow находится в Core, UI capture — в composer owner.

## Проверки

- Solution Debug/Release: 0 warnings/errors.
- Библиотека **96/96**, Core **198/198**, Desktop **275/275** в обеих конфигурациях.
- Core: 18 новых сценариев. Проверены commit-before-wire, ровно один TX,
  захваченный processor result, отказ БД/старые node/session/binding, смена binding
  после Prepare, ERROR/timeout/cancel, двойное нажатие, новый draft, status-write
  failure и восстановление без replay; stale CAS не выдаёт незакоммиченный Accepted.
- Два из этих сценариев используют настоящий `MeshCoreClientFactory`/TCP transport,
  supervisor/session/receive и temporary SQLite против loopback Companion emulator.
  Эмулятор получал один правильный UTF-8 текст в slot 0, чередовал pushes и ответы;
  подтверждены OK и ERROR, а до ответа в SQLite уже был Sending.
  Используется расширенный sample `FakeCompanionServer`, подключённый как test source,
  без дополнительного библиотечного проекта.
- Desktop: 8 новых сценариев, включая Enter/Shift/IME, clear-revision, тот же экземпляр
  пузырька после status update, hidden UI, независимый private draft, older viewport,
  отсутствие outgoing unread и выбор слота.
- Нативный автоматический send audit: production Avalonia controls + UI adapter +
  настоящая временная SQLite; Light/Dark × 420/960. Выбор слота, bindings, live byte
  counter, Enter=одна отправка, Shift/IME=ноль, один AcceptedByNode bubble и очищенный
  draft проверены. Скриншоты: `$TMPDIR/meshcore-d5-send/`.
- Release native viewport audit: Light/Dark, forward с/без trim и backward;
  смещение 0 DIP, дополнительных read advances 0.
- `git diff --check` и относительные ссылки документации проверены.

```sh
dotnet run --project tests/MeshCoreMessenger.Core.Tests -c Release -- -method '*ProductionChannelSendThroughTcp*'
dotnet run --project tools/MeshCoreMessenger.ViewportAudit -c Release -- --send-only
```

Аппаратная нода не подключалась, эфирных передач не было. По указанию пользователя
аппаратную приёмку он выполняет самостоятельно. Ручная пользовательская приёмка
D5 пока не отмечена: подключить свою ноду, выбрать существующий канал/слот, отправить
один тестовый текст, проверить подпись «Принято нодой» и сохранение истории после
перезапуска без повторной передачи. D6–D18 не начинались.


## Исправление переключения публичных чатов после проверки пользователем

Пользователь обнаружил: после первой отправки переход A → B → A выключает
отправку с «Не удалось проверить адресата»; входящее сообщение восстанавливает её.
На native ChatsView с настоящим списком/кнопкой воспроизведено исключение Avalonia
доступа из другого потока: после async flush черновика `CanEdit` публиковался
вне UI dispatcher, запускал readiness refresh и `NotifyCanExecuteChanged` кнопки.
Входящее сообщение обновляло проекцию через UI dispatcher и скрывало этот сбой.

Публикация draft target/CanEdit теперь выполняется через тот же UI dispatcher,
что и загруженный текст, с проверкой текущей версии контекста. Синхронный offline
startup с `dispatchResult=false` сохранён. Исключение не замалчивается повторной
проверкой или ожиданием входящего сообщения.

Добавлены две регрессии dispatcher/startup и native `--switch-only` audit:
production ChatsView, выбор B/A через ListBox, temporary SQLite и эмуляция команды.
Light/Dark: A send → B send → A send без incoming events, правильные слот/число
отправок и ноль исключений в logger. Перед исправлением audit фиксировал ошибку
доступа к `Button.Command`; после исправления проходит.

```sh
dotnet run --project tools/MeshCoreMessenger.ViewportAudit -c Release -- --switch-only
```

После исправления: solution Debug/Release без warnings/errors, Desktop **277/277**
в обеих конфигурациях. Core/библиотека этим исправлением не изменялись;
их предыдущий результат — 198/198 и 96/96. Пользователь повторил сценарий
на аппаратной ноде и подтвердил исправление бага.
