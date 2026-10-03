# D4 — admission команд и session ownership

[Stage D](stage-d.md) · **Статус: не начат.**

**Цель:** send/mutations используют готовую session и безопасно завершаются.

**Scope:** маленькая Core command facade/lease и typed adapters library API,
расширение ICompanionClient для fake testing. Admission только Online, immutable
NodeId/SessionId/generation; закрыть admission до teardown, cancellation/tracked
completion/barriers, durable failure/retry. Supervisor остаётся lifecycle owner;
control loop не ждёт ACK. Observers не принадлежат UI. Нет replay; новая attempt
ждёт закрытия старой. Контракт применим к D10/D13/D17.

**Не входит:** user send, contact/channel workflows, второй event consumer,
raw client ViewModel. Runtime кнопки пока выключены.
**Зависимости:** D3; connections/shutdown, ConnectionAttempt/Session/ReceiveCoordinator.

**Тесты:** fake blocked command → disconnect/switch/shutdown во всех фазах; одна attempt;
stale lease до вызова → zero TX, после вызова → старое ownership;
receive/event/ingest barriers; shutdown write failure/retry; late completion
не меняет новую session; нет admission в Connecting/Synchronizing/RetryWaiting.

**Готово:** lifecycle boundary доказан до реального TX; изменение прежних state
publication/retry правил допускается только при необходимом обосновании.
**Ручная проверка:** не обязательна, основное доказательство — deterministic fake tests.
