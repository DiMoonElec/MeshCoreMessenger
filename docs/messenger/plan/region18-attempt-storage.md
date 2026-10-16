# REGION18 — Хранение scope исходящих попыток

[Stage REGION](stage-region.md) · [Общие контракты](region-design.md) · [Протокол](../../library/protocol/regions.md)

**Статус: не начат.** Задание подготовлено 16.10.2026.

**Цель:** Добавочная миграция и immutable scope snapshots.

**Зависимости:** REGION17. Проверить фактические результаты перед началом.
Рекомендуемый порядок последовательный: предыдущий шаг — [REGION17](region17-default-service.md).
Выполнять только этот подэтап; завершение не запускает следующий автоматически.

**Файлы/область:** DatabaseMigrator, outgoing store/contracts, Domain outgoing records; storage tests.
Новые типы/файлы здесь — предлагаемое место, не утверждение об их существовании.
Сначала сверить код, git status и версии схемы/API; готовые механизмы использовать повторно.

## Минимальное чтение

Общий порядок — [правила исполнения REGION](stage-region.md#порядок-работы-агента).
Из [архитектуры](region-design.md) и [протокола](../../library/protocol/regions.md)
прочитать только перечисленные ниже разделы и указанные соседние контракты:

region-design.md: «Хранение и локальные операции», «Отправки, история, повторы и лимиты»; d3-outgoing-storage.md и актуальные private-delivery storage contracts.

## Шаги

1. Добавить следующую миграцию SendAttemptScopes/immutable key version, связывая snapshot с существующим AttemptId.
2. Сохранять requested policy, effective-known, fingerprint/name и материал для точного повтора одной транзакцией с preparation; не логировать custom keys.
3. Старым попыткам оставить LegacyUnknown; rename/archive каталога не меняет прошлое; обеспечить existing clear-history ownership и key retention.

**За границами шага:** Channel/private send orchestration и UI history.

## Проверки и завершение

**Обязательные проверки:** Migration rollback; prepare idempotency; historical LegacyUnknown; archive/delete после отправки; clear-history scope; две ноды; startup recovery без TX.

**Готово:** Любая новая region-aware попытка может быть воспроизведена по сохранённому снимку.

Если зависимость отсутствует или проверка не выполнена, записать точную причину и
оставить соответствующий пункт открытым. Для аппаратных/GUI проверок различать
автоматический результат, native просмотр и пользовательское подтверждение.
Не объявлять всю REGION завершённой по успеху одного слоя.

- [ ] Изменения в границах подэтапа выполнены.
- [ ] Обязательные проверки выполнены; команды и фактические результаты записаны.
- [ ] Ограничения и оставшиеся проверки указаны.
- [ ] Статус этого файла, checklist Stage REGION и current-state обновлены.

## Результат выполнения

Ожидает выполнения. Следующий шаг после завершения — [REGION19](region19-channel-application.md).
