# Архитектура MeshCoreMessenger: оглавление

- [Как читать документацию](README.md) — выбор минимального набора разделов для задачи.
- [scope](messenger/architecture/scope.md) — Цели, границы первой версии и исходные архитектурные решения.
- [solution](messenger/architecture/solution.md) — Технологии, проекты, DI и logging.
- [ownership](messenger/architecture/ownership.md) — Сервисы и единственные владельцы состояния.
- [connections](messenger/architecture/connections.md) — Transport profiles, supervisor, reconnect и открытие session.
- [storage](messenger/architecture/storage.md) — SQLite-модель, commit-before-UI и ошибки сохранения.
- [identity](messenger/architecture/identity.md) — Identity ноды/контакта/канала и версии slot bindings.
- [sending](messenger/architecture/sending.md) — Исходящие сообщения и честные статусы доставки.
- [node-scoped-ui](messenger/architecture/node-scoped-ui.md) — Active/viewed node, directory projections и bounded history.
- [ui-and-local-writes](messenger/architecture/ui-and-local-writes.md) — Компоновка, unread, search, drafts и UI preferences.
- [diagnostics](messenger/architecture/diagnostics.md) — Пакеты, диагностика и границы дешифровки.
- [shutdown](messenger/architecture/shutdown.md) — Event barriers, quiesce и recoverable shutdown.
- [platforms](messenger/architecture/platforms.md) — Платформы, данные, упаковка и поставка.
- [agent-rules](messenger/architecture/agent-rules.md) — Инварианты и правила для следующих агентов.
