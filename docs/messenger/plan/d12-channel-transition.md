# D12 — безопасный переход канального слота

[Stage D](stage-d.md) · **Статус: не начат.**

**Цель:** доказать drain/readback границы до настоящего Set/Clear.

**Scope:** Core slot-transition workflow с ReceiveCoordinator: закрыть send для
target slot, сериализовать затрагивающие directory операции, закончить drain,
library-event/ingest barriers; отметить переход, выполнить injected fake mutation/
readback, commit versioned binding, обновить resolver, возобновить drain/send только
при безопасной готовности. Сообщения переходного периода — неопределённые, не
автоматически новый секрет. Terminal Quiesce не считать обычной pause без lifecycle анализа.

**Не входит:** production mutation UI, второй consumer, обход command gate,
обещание атомарности firmware + SQLite.
**Зависимости:** D4, D11; identity, B3 pending transitions/B5 ReceiveCoordinator.

**Тесты:** exact drain/barriers/mutate/readback/commit/resume ordering; interleaved
MessagesWaiting/messages до/во время/после; same/change fingerprint, два slots;
SYNC timeout → новая session без replay; БД отказ → NeedsAttention;
shutdown/reconnect во всех фазах; старый ingest не теряется/не попадает новому каналу.

**Готово:** transition pipeline proven fakes независимо от radio API.
**Ручная проверка:** не обязательна; busy/error визуально подготовлен в D11.
