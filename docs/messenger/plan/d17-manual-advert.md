# D17 — ручная отправка advert

[Stage D](stage-d.md) · **Статус: не начат.**

**Цель:** одно явное действие вызывает один существующий API.

**Scope:** facade D4 → SendAdvertisementAsync(mode), single-flight; wired D16.
Online/current identity, ERROR vs uncertain timeout/cancel; после reconnect
повтор только новым кликом. Ни load/startup/autoConnect/wake, ни чужой advert
не инициируют собственный.

**Не входит:** scheduling/rate automation, DB messages для advert,
радиопараметры/обещание доставки.
**Зависимости:** D4, D16; library advertisement API.

**Тесты:** exact mode/one call; double click; offline/Synchronizing/stale lease;
timeout/disconnect/shutdown; reconnect/startup/wake → zero advert;
continuous incoming/Events без конкуренции.

**Готово:** manual action работает, lifecycle не вызывает его неявно.
**Ручная проверка: обязательна** — fake результаты, затем только явно разрешённый
hardware advert/режим. Другие mutations при проверке не выполнять.
