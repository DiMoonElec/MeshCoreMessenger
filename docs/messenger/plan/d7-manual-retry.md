# D7 — восстановление и явный повтор

[Stage D](stage-d.md) · **Статус: не начат.**

**Цель:** пользователь решает, повторять ли потенциально доставленное сообщение.

**Scope:** UI D1 просмотра попыток/предупреждения подключить к store; восстановленные
Prepared/Unknown, Unconfirmed/Failed. Повтор — новый AttemptNumber под тем же message,
текущая проверенная session той же ноды/актуальный адресат. Не переносить на B/другой
channel fingerprint. Новые wire timestamp/ACK metadata; старые attempts не стирать,
late ACK не завершает новую. Durable retry записи отличать от radio retry.
Prepared тоже передавать только явно.

**Не входит:** background outbox, automatic retry, редактирование сохранённого текста.
**Зависимости:** D3–D6, warning preview D1.

**Тесты:** crash/reopen → zero TX; confirm/cancel; double click → одна attempt;
new number/ownership; old ACK; offline/wrong node/stale binding; prepare/terminal
persist failure; shutdown/retry без replay.

**Готово:** send/recovery/manual retry end-to-end; открытие окна/reconnect не передают.
**Ручная проверка: обязательна** — restart/fake Unknown, warning, история attempts,
offline отказ/две темы. Hardware repeat не требуется.
