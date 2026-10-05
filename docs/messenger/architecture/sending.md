# 7. Отправка и статусы

[Оглавление](../../MESSENGER_ARCHITECTURE.md) · [Маршрутизация чтения](../../README.md)

## 7. Отправка и статусы

На каждое явное действие «Отправить»:

1. Проверить Online, адресата/слот и актуальную конфигурацию, формат и UTF-8 лимит.
2. Сохранить исходящее сообщение и `SendAttempt=Prepared` в БД; при ошибке ничего
   не передавать. Предотвратить повтор кнопки во время принятия одной команды.
3. Сохранить `Sending` **до** вызова API. Начать ровно один вызов `SendTextAsync`
   или `SendChannelTextAsync`; никаких транспортных повторов этого вызова.
4. Для личного сохранить `MSG_SENT` и ACK tag, наблюдать `Delivery` отдельно от
   UI и выбранного чата, записать окончательный результат. Сохранение Accepted
   должно предшествовать сохранению Delivered даже при уже завершившейся Delivery.
5. Для канала после OK сохранить `AcceptedByNode`. Подтверждения участниками нет.

| Состояние попытки | Отображение / смысл |
| --- | --- |
| Prepared | Подготовлено, вызов API ещё не начат |
| Sending | Передаётся компаньону, результат может стать неопределённым |
| AwaitingAck | Личное принято нодой, ожидается подтверждение |
| Delivered | Получен совпавший ACK; это не прочтение человеком |
| AcceptedByNode | Канал принят нодой, либо личное с NotExpected; ACK не обещан |
| Unconfirmed | ACK не получен вовремя; сообщение могло дойти |
| Failed | Доказанный локальный отказ до вызова или явный ERROR прошивки |
| Unknown | Вызов начат, затем отмена/обрыв/авария до достоверного результата |

После рестарта Sending/AwaitingAck переходят в Unknown, Prepared остаётся
неотправленным; ничего не посылать автоматически. После отмены вызванного API
не утверждать «не отправлено», если нет доказательства. Ручной повтор создаёт
новую попытку под тем же сообщением; для каналов по указанию пользователя
04.10.2026 выполняется без подтверждения, предупреждение для будущего личного повтора сохраняется;
предыдущие попытки не стираются. ACK обновляет конкретную попытку, не последнее
сообщение в чате. Старые задачи сохраняют привязку к своей ноде/сессии после смены профиля.

Поле ввода показывает занятые/доступные UTF-8 байты, не число символов. Личное —
160 байт, канал — лимит за вычетом имени собственной ноды и `": "`. Автоматического
разбиения длинного текста нет; Unicode и emoji проверяются теми же правилами, что
и кодировщик библиотеки. Общий helper расчёта/валидации желательно добавить в
библиотеку до UI отправки, чтобы не дублировать правила.

D2: общий helper реализован; composer получает byte count через
[application text processor](text-processing.md). Processor сейчас passthrough,
с API уровней будущей оптимизации. Encoder использует общий validator и сам
не меняет текст. Отправка должна сохранять и использовать захваченный результат
обработки, сохраняя исходный draft до передачи ответственности хранилищу.

D3: `LocalStorage.OutgoingMessages` атомарно сохраняет оригинал и TransmissionText
с первой Prepared-попыткой, использует MessageId как идемпотентный operation ID.
CAS переходы адресованы точным node/message/attempt/session. Sending=6 не меняет
прежние значения enum. Accepted+Expected означает AwaitingAck, Accepted+NotExpected —
завершённый AcceptedByNode. Миграция v3 и startup recovery выполняются до возврата
LocalStorage, с SQLite backup перед upgrade; неопределённые попытки становятся Unknown
без отправки. Legacy private Accepted без явной expectation трактуется консервативно.
History DTO получает последнюю попытку одним bounded SQL join; исходящие уведомления
отдельны от incoming/unread. Детали и проверки — [D3](../plan/d3-outgoing-storage.md).

D4: будущий MessageService получает scoped `SessionCommandLease` через Core gateway.
После Prepare он связывает реальный attempt через BindOutgoingAsync, сохраняет Sending
и вызывает typed text API один раз. Lease читает захваченный TransmissionText из store,
проверяет session/recipient и текущую binding перед вызовом. Весь workflow принятия
результата/записи и ACK observer регистрируется через RunAsync/ObserveAsync; UI не
владеет этими задачами. При закрытии lease неопределённость сохраняется под прежним
owner; failed status writes удерживаются для Retry/Flush без повторения TX.
D4 даёт только инфраструктуру; runtime Send остаётся выключенным до D5/D6.


D5: production `MessageService` выполняет одну явную канальную отправку через D4
lease. Capture включает точные session/generation/binding и ревизию draft;
processor использует имя фактической ноды. После atomic Prepare передаётся
ответственность за текст outgoing store; только совпавшая ревизия draft очищается.
Кнопка/Enter подключены для каналов, выбор нескольких slots явный. Канал получает
Accepted+NotExpected после OK, Failed после явного ERROR либо Unknown при
неопределённости. Separate outgoing notifications обновляют пузырёк/preview
после commit без увеличения unread; статус не создаёт новый пузырёк.
Private send и manual retry ещё не подключены. [Проверки D5](../plan/d5-channel-send.md).


D6: тот же MessageService подключает личную отправку текущему однозначному Chat.
После MSG_SENT commit сохраняются wire timestamp и ExpectedAck ноды; Delivery observer
принадлежит immutable lease/attempt, не выбранному чату. Single-flight принятия команды
освобождается после Accepted; разные ACK ожидания сосуществуют и сопоставляются
библиотекой по protocol tag. Приложение дополнительно проверяет tag Confirmed результата
перед Delivered и сохраняет RTT. Observer/cleanup завершаются через D4 session barrier;
ошибки записи повторяются только в SQLite. Подробности и доказательства —
[D6](../plan/d6-private-send.md).

D7.1, 05.10.2026: в канальном меню «Повторить доставку» создаёт attempt того же message с прежним timestamp; «Отправить как новое» создаёт новое message с новым timestamp. Оба используют сохранённый текст, оставляя текущий draft. [Контракт и проверки](../../testing/channel-repeat-actions.md).

05.10.2026: пользователь запросил автоматические попытки ЛС внутри текущей session
(3 flood либо 3 known + 2 flood); прежняя политика одноразового D6 теперь baseline.
Уточнение: known T1/attempt 0…2 → reset → flood T2/attempt 0…1; один локальный MessageId,
несколько сетевых идентичностей. API выбирает прежний timestamp/attempt + 1 либо новый
timestamp/attempt 0 внутри фаз. Лимит остаётся 160; новый timestamp может создать
дубликат у адресата при потерянном ACK. [Итоговый план](../plan/d7-private-auto-retry.md)
ещё не реализован. В библиотеке остаётся один TX на API вызов, reconnect/startup
не replay. [Packet hash/attempt/ACK](../../library/protocol/private-retry-hashes.md).

05.10.2026, P1: явные timestamp/attempt 0…3 доступны в библиотеке и Core adapter.
Core содержит immutable PrivateRetryPolicy и внутренний планировщик 3/3+2 обоих
режимов. Он только выбирает wire identities/route phases, не выполняет TX/БД/reset.
MessageService пока не исполняет повторы: P2 storage, P3 ACK и P4 coordinator остаются
следующими шагами. [Проверки API](../../testing/private-retry-api.md).
