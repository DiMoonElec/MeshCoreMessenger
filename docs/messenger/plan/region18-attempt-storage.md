# REGION18 — Хранение scope исходящих попыток

[Stage REGION](stage-region.md) · [Общие контракты](region-design.md) · [Протокол](../../library/protocol/regions.md)

**Статус: выполнен 17.10.2026.** Задание подготовлено 16.10.2026.

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

- [x] Изменения в границах подэтапа выполнены.
- [x] Обязательные проверки выполнены; команды и фактические результаты записаны.
- [x] Ограничения и оставшиеся проверки указаны.
- [x] Статус этого файла, checklist Stage REGION и current-state обновлены.

## Результат выполнения

Миграция schema8→9 добавляет SendAttemptScopes: requested mode/revision, effective state, immutable key16/name/fingerprint, session/generation и budget. Исторические попытки получают LegacyUnknown без вычисления ключей. FK/owner trigger защищают принадлежность попытке, ноде и сессии; UPDATE запрещён. Архив/удаление каталога не затрагивают снимки. Ключи удаляются каскадом только вместе с соответствующими SendAttempts при штатной очистке истории.

PrepareOutgoingMessage/PrepareChannelRepeat/PreparePrivateAttempt имеют typed Scope capture. Initial preparation и scope записываются одной транзакцией; idempotency проверяет весь снимок. Приватные retries сохраняют оригинальный scope; channel repeat не может подменить известный effective scope. GetAttemptScopeAsync возвращает immutable snapshot только для указанной ноды. Снимки не выводят ключи в ToString/диагностику.

Проверки: targeted Core109/109 (outgoing/private/region migrations/incoming/history-clear), Release solution build0 warnings/errors. Новые 9 regression cases: rollback миграции и preparation, idempotency, legacy upgrade+backup, удаление каталога, startup recovery, clear-history, private/channel repeats и две реальные node/session identity. Полный Core результат записан в current-state. Новые API ещё не подключены к channel/private orchestration — это REGION19–20. Реальная пользовательская БД не открывалась, hardware TX не выполнялся.

Следующий шаг — [REGION19](region19-channel-application.md).
