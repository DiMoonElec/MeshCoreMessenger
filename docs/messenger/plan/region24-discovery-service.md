# REGION24 — Полный поиск и локальные наблюдения

[Stage REGION](stage-region.md) · [Общие контракты](region-design.md) · [Протокол](../../library/protocol/regions.md)

**Статус: выполнен 17.10.2026.** Задание подготовлено 16.10.2026.

**Цель:** RegionDiscoveryService поверх neighbours/request/preflight.

**Зависимости:** REGION23. Проверить фактические результаты перед началом.
Рекомендуемый порядок последовательный: предыдущий шаг — [REGION23](region23-discovery-route.md).
Выполнять только этот подэтап; завершение не запускает следующий автоматически.

**Файлы/область:** Core/Application/RegionDiscoveryService (новый), region store, session gateway.
Новые типы/файлы здесь — предлагаемое место, не утверждение об их существовании.
Сначала сверить код, git status и версии схемы/API; готовые механизмы использовать повторно.

## Минимальное чтение

Общий порядок — [правила исполнения REGION](stage-region.md#порядок-работы-агента).
Из [архитектуры](region-design.md) и [протокола](../../library/protocol/regions.md)
прочитать только перечисленные ниже разделы и указанные соседние контракты:

region-design.md: «Discovery в приложении»; REGION8/10/22/23 contracts.

## Шаги

1. Собрать neighbours по deadline, построить preflight и последовательно запросить разрешённые пользователем цели; ограничить количество/время, дать progress/cancel.
2. ReadyDirect выполнять сразу; mutation-required только по явно выбранной политике операции. Вызвать REGION23 и сохранить восстановление/конфликт в outcome.
3. Записать bounded per-source observations и per-repeater outcomes; объединить имена без auto-select/auto-save catalogue, сохранить empty/partial/private distinctions.

**За границами шага:** Фоновый auto-discovery, администрирование репитеров и UI.

## Проверки и завершение

**Обязательные проверки:** Несколько источников; один timeout не уничтожает другие результаты; cancel; stale session; implicit contact refresh; malformed CSV; rate-limit/no response; persistence failure.

**Готово:** Core предоставляет законченный поиск с прогрессом, источниками и честными побочными эффектами.

Если зависимость отсутствует или проверка не выполнена, записать точную причину и
оставить соответствующий пункт открытым. Для аппаратных/GUI проверок различать
автоматический результат, native просмотр и пользовательское подтверждение.
Не объявлять всю REGION завершённой по успеху одного слоя.

- [x] Изменения в границах подэтапа выполнены.
- [x] Обязательные проверки выполнены; команды и фактические результаты записаны.
- [x] Ограничения и оставшиеся проверки указаны.
- [x] Статус этого файла, checklist Stage REGION и current-state обновлены.

## Результат выполнения

IRegionDiscoveryService и RegionService.DiscoverAsync выполняют явный single-flight bounded scan → preflight → последовательные запросы через REGION23. Options задают total/request deadline, target limit/full-key selection, подтверждённые reply paths и consent на побочные действия. Progress/cancel не блокирует RX; throwing observer не отменяет cleanup. ReadyDirect выполняется сразу, остальные требуют явной политики; неизвестный selected key не становится фиктивной direct-целью. Unsupported neighbor scan допускает работу с известными контактами.

Outcomes различают empty/possibly-partial, no path, unsupported, timeout, cancelled, malformed, table-full, preparation/stale/storage/transport failure и success с route warning. Private names остаются key-required, wildcard — unscoped, неизвестные допустимые строки сохраняются как diagnostic evidence. Имена объединяются со списком full-key источников без auto-select, derivation private keys или auto-save catalogue. Один timeout не уничтожает предыдущие результаты и не вызывает retries; storage failure прекращает новые Mesh запросы.

Schema9→10 добавляет bounded RegionDiscoveryOutcomes с node/session FK, status/route metadata, names и безопасным error code. Outcome и per-source observations коммитятся одной транзакцией, с count/age retention и post-commit event. Повтор локальной записи идемпотентен только для того же terminal outcome. Исторические наблюдения не удаляются из-за отсутствия имени в новом ответе. Cancelled target может сохранить outcome при ещё открытой сессии; stale session не записывает поздние имена.

Проверки: Core550/550; final targeted10/10 (multiple sources, timeout/cancel/deadline, consent, implicit refresh, malformed/unsupported, stale owner, DB failure без replay, retention/FK/atomicity, migration rollback и schema9 backup). Release build0 warnings/errors, bootstrap6/6. Hardware и GUI discovery не запускались; сквозная production-wire приёмка продолжается в REGION25.

Следующий шаг — [REGION25](region25-core-acceptance.md).
