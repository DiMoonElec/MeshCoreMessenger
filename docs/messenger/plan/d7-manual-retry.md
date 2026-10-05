# D7 — восстановление и явный повтор

[Stage D](stage-d.md) · **Статус: D7.1 (каналы) реализован; личный повтор/остальная приёмка не начаты.**

05.10.2026: выполнен P1 [расширенного плана ЛС](d7-private-auto-retry.md): явный send API и планировщик, исполнение автоматических повторов ещё не подключено. [Отчёт](../../testing/private-retry-api.md).

05.10.2026: по новому запросу подготовлен [итоговый план автоматических повторов ЛС и истории успешных маршрутов](d7-private-auto-retry.md). После known ×3/reset fallback начинает новый timestamp/attempt 0, API поддерживает оба режима сетевого повтора. Это расширяет прежний scope manual-only; код пока не реализован.

05.10.2026: пользователь подтвердил аппаратную приёмку двух канальных действий; [отчёт дедупликации](../../testing/channel-repeat-hardware-dedup.md). Личная часть D7 остаётся незавершённой.

05.10.2026: канальная часть получила [два явных действия](../../testing/channel-repeat-actions.md): «Повторить доставку» сохраняет timestamp и MessageId; «Отправить как новое» создаёт новые timestamp и MessageId. Ни одно действие не очищает текущий draft. Личная часть исходного scope ниже остаётся незавершённой.

04.10.2026: по отдельному запросу пользователя реализован только
[повтор канального сообщения без подтверждения](../../testing/channel-manual-repeat.md).
Для каналов warning dialog из исходного scope ниже отменён явным указанием
пользователя. Новый attempt под тем же message, отдельный wire timestamp,
без дубликата в локальном чате/очистки draft. D7 целиком пока не закрыт.

**Цель:** пользователь решает, повторять ли потенциально доставленное сообщение.

**Scope:** UI D1 просмотра попыток/предупреждения подключить к store; восстановленные
Prepared/Unknown, Unconfirmed/Failed. Повтор — новый AttemptNumber под тем же message,
текущая проверенная session той же ноды/актуальный адресат. Не переносить на B/другой
channel fingerprint. Новые wire timestamp/ACK metadata; старые attempts не стирать,
late ACK не завершает новую. Durable retry записи отличать от radio retry.
Prepared тоже передавать только явно.
D14/D15 уже реализованы досрочно: повтор должен удерживать общий
ConversationOperationGuard на всём prepare/send/ACK/durable-write lifetime,
чтобы очистка истории не удаляла активную попытку; проверить гонку retry/clear.

**Не входит:** background outbox, automatic retry, редактирование сохранённого текста.
**Зависимости:** D3–D6, warning preview D1.

**Тесты:** crash/reopen → zero TX; confirm/cancel; double click → одна attempt;
new number/ownership; old ACK; offline/wrong node/stale binding; prepare/terminal
persist failure; shutdown/retry без replay.

**Готово:** send/recovery/manual retry end-to-end; открытие окна/reconnect не передают.
**Ручная проверка: обязательна** — restart/fake Unknown, warning, история attempts,
offline отказ/две темы. Hardware repeat не требуется.
