# Архитектура MeshCoreSharp

- [Как читать документацию](README.md) — выбор минимального набора разделов для задачи.
- [overview](library/architecture/overview.md) — Цели и границы библиотеки.
- [runtime-pipeline](library/architecture/runtime-pipeline.md) — RX pipeline, dispatcher и независимая доставка событий.
- [layers](library/architecture/layers.md) — Ответственность API, транспорта, протокола и runtime.
- [immediate-transactions](library/architecture/immediate-transactions.md) — Single-flight команды и subscribe-before-send.
- [packet-matching](library/architecture/packet-matching.md) — Маршрутизация ответа и unsolicited packets.
- [multi-frame](library/architecture/multi-frame.md) — Многочастные транзакции контактов.
- [concurrency](library/architecture/concurrency.md) — CommandGate, MeshRequestGate, ACK и mutations.
- [message-pump](library/architecture/message-pump.md) — Автоматический и явный drain, timeout и reconnect.
- [api-and-errors](library/architecture/api-and-errors.md) — Таймауты, публичный API, ошибки и границы будущих проектов.
