# D13 — применение channel mutations

[Stage D](stage-d.md) · **Статус: не начат.**

**Цель:** подключить проверенный переход к библиотеке/UI D11.

**Scope:** hashtag derivation, явный 16-byte secret, rename/replace/clear через
SetChannelAsync/ClearChannelAsync; readback fingerprint/name/slot, secret только в памяти.
История остаётся у fingerprint; same secret rename сохраняет identity, другой secret —
другая identity. Открытие формы/выбор режима не меняет slot config. Unknown outcome
запрещает send до явного resync/reconnect/readback, не запускает auto-replay mutation.

**Не входит:** filters/ACL/invitations, auto restore config, local history delete,
настройки радио.
**Зависимости:** D11–D12; D5 отправка через актуальный binding.

**Тесты:** hashtag/secret validation и границы 31/32 UTF-8 bytes имени;
same/change key, two slots; clear/history;
OK/readback mismatch/timeout/error; outage после command; concurrent send запрет;
secret не в БД/logs; late node context, shutdown/initial resync.

**Готово:** UI wired через transition pipeline, история не смешивается/не удаляется.
**Ручная проверка: обязательна** — fake сценарии; hardware только согласованный
test slot с сохранением/восстановлением исходной конфигурации.
