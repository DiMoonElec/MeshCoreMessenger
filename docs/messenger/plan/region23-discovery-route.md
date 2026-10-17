# REGION23 — Временный direct route и восстановление

[Stage REGION](stage-region.md) · [Общие контракты](region-design.md) · [Протокол](../../library/protocol/regions.md)

**Статус: выполнен 17.10.2026.** Задание подготовлено 16.10.2026.

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

- [x] Изменения в границах подэтапа выполнены.
- [x] Обязательные проверки выполнены; команды и фактические результаты записаны.
- [x] Ограничения и оставшиеся проверки указаны.
- [x] Статус этого файла, checklist Stage REGION и current-state обновлены.

## Результат выполнения

IRegionDiscoveryRouteWorkflow требует явный consent для implicit contact creation/temporary zero-hop. Общий ConversationOperationGuard владеет full-key контактом до terminal response и cleanup, конфликтуя с private jobs и ручным reset. Перед mutation проверяются owner, свежий полный контакт и revision плана. После принятого SET выполняется независимый от caller cancellation bounded readback; только доказанный zero-hop допускает request/conditional restore. Неоднозначный SET или недостоверный readback не запускает слепой restore.

CompanionSession ведёт per-key observation version по ADVERT/PATH_UPDATED. Plan и prepared snapshot содержат её; event barrier и свежий GET перед restore обнаруживают новый advert/route даже при неизменившихся wire fields. Restore не выполняется при конфликте или закрытой session. После restore есть полный GET и проверка configuration. Firmware не предоставляет compare-and-set: гарантия относится к владению приложения и наблюдённым изменениям; финальный readback выявляет несовпадение результата.

Response, preparation, restoration, implicit creation и ошибки разделены в RegionRouteRequestResult. Новый контакт после anonymous request только перечитывается и сохраняется, не удаляется автоматически. Single-contact readback обновляет SQLite атомарно без retirement остальных контактов/каналов; проверяет active NodeId/SessionId и observation time, публикуется после commit. Timeout/cancellation/malformed response после TX у GetContact/AddOrUpdate/ResetPath закрывает library admission, исключая поздний CONTACT/OK в следующей операции.

Проверки: Core541/541; финальные workflow cases15/15; library228/228; Release solution build0 warnings/errors, Desktop bootstrap6/6. Покрыты terminal wait ownership, restore точных fields, cancel/timeout, новый advert/route, table-full, stale owner, disconnect/late response, commit failure и node/session/time guards. Аппаратные проверки остаются REGION32–33; универсальный D10 не реализовывался.

Следующий шаг — [REGION24](region24-discovery-service.md).
