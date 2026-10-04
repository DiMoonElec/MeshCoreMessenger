# D6 — личная отправка и ACK

Дополнение 04.10.2026: реализован переход Unconfirmed → Delivered по позднему ACK
в той же сессии, включая гонки записи и защиту от неоднозначных tag.
[Контракт и проверки](../../testing/private-late-ack.md).

[Stage D](stage-d.md) · **Статус: реализован; функциональные автоматические проверки пройдены.
Нативный автоматический аудит пройден; пользователь подтвердил исправление открытия новой переписки.**

**Цель:** доставка принадлежит сохранённой попытке, не текущему чату.

**Scope:** MessageService private: текущий Chat contact, полный key/уникальность
6-byte prefix среди всех типов. Prepared/Sending → MSG_SENT metadata/Accepted commit
→ tracked Delivery observer → Delivered/Unconfirmed/AcceptedByNode(NotExpected)/Unknown.
Observer независим от selected chat, immutable node/session/attempt; UI читает
только committed результат.

**Не входит:** room/sensor/repeater send, automatic/manual retry.
Не обещать «прочитано человеком», не вычислять expected ACK самостоятельно.
**Зависимости:** D5; TextMessageSendResult/Delivery, identity/sending/shutdown.

**Тесты:** ACK завершён до сохранения Accepted; ACK/timeout/NotExpected/error/cancel;
accepted → terminal ordering; collision Chat/non-Chat, absent/removed contact;
chat/profile/node switch; late ACK конкретной старой attempt; ACK timeout без reconnect;
terminal write failure/retry без второго TX; shutdown во время ACK.

**Готово:** честные исходы, записи не зависят от view, observers не теряются при reconnect.
**Ручная проверка: обязательна** — fake delivery states; hardware получение/ACK
только со второй нодой и разрешённым адресатом.


## Реализация D6

- `MessageService.SendPrivateAsync` подключён к существующему composer личных чатов.
  Общий с D5 workflow сохраняет Prepared/Sending до одного вызова API, оригинал и
  захваченный TransmissionText от processor, затем безопасно передаёт draft revision.
  Для private processor получает `IsChannel=false` и бюджет 160 UTF-8 байт.
- Store проверяет полный ключ/current Chat и уникальность шестибайтового префикса
  среди всех присутствующих типов контактов при Prepare; lease повторяет проверку
  непосредственно перед вызовом. Неизвестные/удалённые/non-Chat/неоднозначные адресаты
  не передают сообщение. Схема v3 уже содержит необходимые metadata; новой миграции нет.
- Из `MSG_SENT` сохраняются timestamp и авторитетный `ExpectedAck` как UInt32LE.
  ACK/tag и suggested timeout обрабатывает существующий `MeshCoreSharp` tracker;
  приложение не вычисляет digest и не сопоставляет ответы по очередности или текущему чату.
  Подтверждённый результат дополнительно проверяется на совпадение с сохранённым tag,
  RTT записывается в конкретную attempt.
- Accepted commit предшествует регистрации terminal writer даже при уже завершённом
  Delivery. Observer зарегистрирован в lease до возврата UI; закрытие lease выполняется
  в фоне, session продолжает владеть им до observer/status completion.
  Single-flight принятия команды освобождается после Accepted, не после ACK.
  UI не блокирует следующий текст всё время ожидания первого подтверждения.
- Delivered/Unconfirmed/Unknown обновляют сохранённую попытку и тот же пузырёк после
  commit. `ExpectedAck=0` означает AcceptedByNode/NotExpected. ERROR — Failed;
  неопределённый исход вызова или отмена/обрыв ожидания — Unknown. Тайм-аут ACK
  не вызывает reconnect; Unconfirmed не означает доказанный отказ доставки.
- Shutdown/profile switch/reconnect используют существующий D4 barrier. Ошибка
  status write удерживается в durable tracker; Retry записывает только БД, без TX.
  Никаких automatic/manual retries и room/sensor/repeater sends в D6 не добавлено.

## Проверки

Solution Debug/Release — 0 warnings/errors. Executable runners в обеих конфигурациях:
библиотека **96/96**, Core **230/230**, Desktop **281/281**.

Core: 32 новых сценария, включая быстрый ACK до Accepted, все delivery outcomes,
metadata/RTT, private processor capture, missing/non-Chat/collision/removed contact,
ошибки до Prepare/после invocation, независимые pending attempts, profile switch,
shutdown/cancel, late result и status-write recovery без повторной передачи.
Восемь сценариев используют production `MeshCoreClientFactory`/TCP/supervisor/receive
и временную SQLite с loopback Companion emulator. Проверены:

- два одинаковых текста подряд одному и разным адресатам: разные wire timestamps/tag;
- ACK второго раньше первого: сначала доставленным становится только второй,
  затем первый; каждому сохраняется собственный RTT;
- посторонний и повторный ACK не завершают чужую попытку;
- совпавший `expected_ack` для двух pending sends приводит к Unknown у обеих,
  а последующий ACK не выдаёт ложное Delivered;
- немедленный ACK, отсутствие ACK/timeout без reconnect, zero tag/NotExpected,
  ERROR и shutdown во время ожидания; Sending уже сохранён до ответа эмулятора.

Desktop: четыре новых сценария — private capture/160-byte counter, offline draft,
AwaitingAck → terminal в том же экземпляре пузырька после переключения workspace,
сохранность нового/private/public draft и отсутствие исходящих unread.

Добавлен `--private-send-only` native audit: production ConversationView/ComposerView,
временная SQLite, Light/Dark × 420/960, Enter/Shift/IME, очистка draft,
AwaitingAck → Delivered и доступность следующей отправки до ACK.
**Нативный аудит не выполнен успешно:** две попытки запуска в текущей среде завершаются
до создания окна с `Avalonia.Native was not able to start the RenderTimer`, код macOS
`-6661`. Это ограничение запуска зафиксировано отдельно от прошедших UI ViewModel tests;
визуальная проверка и скриншоты D6 пока не заявляются.

```sh
dotnet run --project tests/MeshCoreMessenger.Core.Tests -c Release -- -method '*Tcp*Private*' -method '*ProductionPrivateSendThroughTcp*'
dotnet run --project tools/MeshCoreMessenger.ViewportAudit -c Release -- --private-send-only
```

Аппаратная нода не подключалась, эфирных передач не было. По указанию пользователя
аппаратную проверку он выполняет самостоятельно: отправить два текста подряд
разрешённому Chat-компаньону, проверить его получение и ACK, переключиться в другой
чат во время ожидания, вернуться и проверить статусы. Перезапуск не должен повторять
отправку. Ручная пользовательская приёмка пока не отмечена. D7–D18 не начинались.


## Исправление открытия истории после первого ЛС

Пользователь прислал stack trace `The calling thread cannot access this object`:
фоновой projection worker после commit вызывает `ConversationNavigation.RefreshAsync`
→ `HistoryWindow.OpenAsync` → `ResetSearch` → `RaiseSearchProperties` →
`AsyncRelayCommand.NotifyCanExecuteChanged`. ResetSearch выполнялся до UI dispatcher,
поэтому привязанная Avalonia-кнопка отказывалась обновляться. Верхняя плашка относилась
к проекции истории, а не к отказу ноды передать сообщение.

ResetSearch теперь выполняется через тот же ApplyAsync/UI dispatcher с проверкой
node/conversation/context version, что и публикация загруженной истории. Для offline
startup сохранён inline путь `dispatchResult=false`. Протокол, ACK и передача не менялись.

Добавлены две проверки search-command notifications при фоновом OpenAsync и inline
startup, а также первая private отправка контакту без существующей Conversations-записи.
UI adapter materializes draft/conversation как production workflow. Native audit теперь
тоже отправляет первый текст контакту без истории и проверяет её переоткрытие на настоящих
controls: Light/Dark × 420/960, тот же Delivered bubble, новый draft и доступный следующий
send. **Native audit пройден**; прежняя блокировка RenderTimer больше не воспроизвелась.
Скриншоты — `$TMPDIR/meshcore-d6-private-send/`.

Solution Debug/Release без warnings/errors; Desktop **284/284** в обеих конфигурациях.
При одном Debug-запуске дочерние MSBuild nodes аварийно завершились (MSB4166);
последовательная сборка `-m:1` и повтор runner прошли. Один дополнительный Release
runner не завершился и был остановлен; диагностический повтор с `-longRunning 10`
прошёл 284/284 за 16 секунд. Причина незавершившегося запуска не установлена.
Core/библиотека не изменены,
их результат D6 остаётся 230/230 и 96/96. Проверены diff и ссылки документов.
Агент аппаратную ноду не использовал. Пользователь повторил открытие новой переписки ЛС
и подтвердил 03.10.2026: ошибка больше не возникает.
