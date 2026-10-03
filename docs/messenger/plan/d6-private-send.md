# D6 — личная отправка и ACK

[Stage D](stage-d.md) · **Статус: не начат.**

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
