# REGION23 — Временный direct route и восстановление

[Stage REGION](stage-region.md) · [Общие контракты](region-design.md) · [Протокол](../../library/protocol/regions.md)

**Статус: не начат.** Задание подготовлено 16.10.2026.

**Цель:** Ограниченная операция подготовки контакта для запроса регионов.

**Зависимости:** REGION22. Проверить фактические результаты перед началом.
Рекомендуемый порядок последовательный: предыдущий шаг — [REGION22](region22-discovery-preflight.md).
Выполнять только этот подэтап; завершение не запускает следующий автоматически.

**Файлы/область:** Core/Application/ContactRouteService, ConversationOperationGuard, DirectoryService, SessionCommandLease; route tests.
Новые типы/файлы здесь — предлагаемое место, не утверждение об их существовании.
Сначала сверить код, git status и версии схемы/API; готовые механизмы использовать повторно.

## Минимальное чтение

Общий порядок — [правила исполнения REGION](stage-region.md#порядок-работы-агента).
Из [архитектуры](region-design.md) и [протокола](../../library/protocol/regions.md)
прочитать только перечисленные ниже разделы и указанные соседние контракты:

region-design.md: «Discovery в приложении»; d10-contact-mutations.md только зависимости; действующий OwnedContactRouteReset.

## Шаги

1. Использовать существующие mutation/readback primitives; добавить только недостающий workflow для явно выбранной подготовки zero-hop, не весь D10.
2. Ввести общее владение full-key route с private retry/reset: snapshot→mutation→readback→request delegate→conditional restore→readback, ожидание completion и bounded cleanup.
3. Перед restore проверить отсутствие нового route/advert state; конфликт сохранить и сообщить. Для неизвестного контакта учесть implicit addContact и refresh, не удалять полученную запись автоматически.

**За границами шага:** Универсальный contact manager, автоматический поиск пути flood и обход незавершённых preconditions.

## Проверки и завершение

**Обязательные проверки:** Fault/cancel на каждой фазе; private send/reset конфликт; incoming route update до restore; table full; disconnect до readback; cleanup failure; отсутствие записи в чужую generation.

**Готово:** Подготовка и восстановление имеют проверенный owner и явный outcome, без скрытого изменения маршрутов.

Если зависимость отсутствует или проверка не выполнена, записать точную причину и
оставить соответствующий пункт открытым. Для аппаратных/GUI проверок различать
автоматический результат, native просмотр и пользовательское подтверждение.
Не объявлять всю REGION завершённой по успеху одного слоя.

- [ ] Изменения в границах подэтапа выполнены.
- [ ] Обязательные проверки выполнены; команды и фактические результаты записаны.
- [ ] Ограничения и оставшиеся проверки указаны.
- [ ] Статус этого файла, checklist Stage REGION и current-state обновлены.

## Результат выполнения

Ожидает выполнения. Следующий шаг после завершения — [REGION24](region24-discovery-service.md).
