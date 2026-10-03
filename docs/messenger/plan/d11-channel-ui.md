# D11 — UI управления каналами

[Stage D](stage-d.md) · **Статус: не начат.**

**Цель:** формы до опасной смены конфигурации слота.

**Scope:** hashtag либо name + 16-byte secret (например, hex 32 цифры),
явный slot, rename/replace secret/clear с предупреждением о новой identity и
сохранении старой истории. Hidden secret input с явным показом; slot occupancy,
unknown outcome/readback-required/busy/error/offline preview. Для одного fingerprint
в нескольких slots явный send binding, без отложенных фильтров/группировки C.

**Не входит:** commands, secrets в SQLite/логах, QR/import/обмен секретами,
ACL/участники. Production mutation buttons disabled.
**Зависимости:** D1 preview, D10; identity/secret-free directory projections.

**Тесты:** add/cancel не пишет; name UTF-8 limit, length/hex/empty hashtag/occupied slot errors;
secret не попадает в logs/tooltips/projection; stale forms, обе темы.

**Готово:** формы/исходы доступны в preview, история не меняется.
**Ручная проверка: обязательна** — narrow/wide, names, keyboard,
различие rename/secret change/clear и явный slot.
