# D18 — совместная приёмка Stage D

[Stage D](stage-d.md) · **Статус: не начат.**

**Цель:** совместимость новых writes/commands со Stage A/B/C/UI.

**Scope:** temporary SQLite/fake A/B: offline draft → Online → channel/private send →
immediate/late ACK → workspace/session/node switch → crash/reopen → explicit retry.
Discovery → contact add/edit/remove с историей; channel same-key rename/change-key/
clear с backlog/barriers; local clear/manual advert; disk/network/readback errors,
recoverable shutdown. Bounded UI/history, post-commit status без reset scroll и
промежуточного погашения unread.

**Не входит:** packaging/signing E, отложенные filters/long messages,
самовольный эфир/удаление настоящих данных.
**Зависимости:** D1–D17; C10/Stage B invariants.

**Тесты:** все Core/Desktop/MeshCoreSharp suites, Debug/Release build без warnings,
v2 migration/recovery/reopen; fake A→B→A, stale operations/ACK, pending sends/mutations
в shutdown, commit failure/retry, zero startup/reconnect replay;
100 000-message history + status update без роста DTO/N+1 reads.

**Готово:** исходный checklist D закрыт фактическими результатами; runtime доступные
функции не используют заглушки. Manual/hardware отмечаются отдельно.
**Ручная проверка: обязательна** — Light/Dark/narrow/wide, UI workflows;
две ноды: двусторонний private/channel exchange и честный ACK/delivery.
Нет второго узла → fake proof и список непроверенного hardware, не аппаратный успех.
Адресаты/slots/advert согласуются, тексты помечены как разработческие;
mutations восстанавливают исходную конфигурацию.
