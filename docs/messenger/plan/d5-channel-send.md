# D5 — явная отправка в канал

[Stage D](stage-d.md) · **Статус: не начат.**

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
