# REGION16 — Capabilities и чтение default в сессии

[Stage REGION](stage-region.md) · [Общие контракты](region-design.md) · [Протокол](../../library/protocol/regions.md)

**Статус: не начат.** Задание подготовлено 16.10.2026.

**Цель:** Read-only подключение library API к Core и startup.

**Зависимости:** REGION15. Проверить фактические результаты перед началом.
Рекомендуемый порядок последовательный: предыдущий шаг — [REGION15](region15-catalog-service.md).
Выполнять только этот подэтап; завершение не запускает следующий автоматически.

**Файлы/область:** ICompanionClient, MeshCoreClientFactory, CompanionSession, ConnectionAttempt и session gateway.
Новые типы/файлы здесь — предлагаемое место, не утверждение об их существовании.
Сначала сверить код, git status и версии схемы/API; готовые механизмы использовать повторно.

## Минимальное чтение

Общий порядок — [правила исполнения REGION](stage-region.md#порядок-работы-агента).
Из [архитектуры](region-design.md) и [протокола](../../library/protocol/regions.md)
прочитать только перечисленные ниже разделы и указанные соседние контракты:

region-design.md: «Владение состоянием и модель», «Совместимость»; architecture/connections.md и shutdown.md.

## Шаги

1. Добавить typed adapter/fake seams для region methods, без raw client в Core/UI; capabilities хранить по конкретной session.
2. Встроить read-only default refresh после identification и до открытия зависящего send admission; сохранить snapshot с NodeId/SessionId/generation.
3. Обработать Unsupported как отсутствие конкретной функции, Unknown отдельно; late/no-correlation timeout передать existing session lifecycle. Connect не вызывает SET/RESET/discovery.

**За границами шага:** Automatic configuration replay и UI default editor.

## Проверки и завершение

**Обязательные проверки:** Legacy firmware unsupported; KnownEmpty/Unknown; другая нода по endpoint; reconnect c внешним изменением default; stale result; shutdown; initial message drain.

**Готово:** Новая сессия получает честный capability/default snapshot без мутации ноды.

Если зависимость отсутствует или проверка не выполнена, записать точную причину и
оставить соответствующий пункт открытым. Для аппаратных/GUI проверок различать
автоматический результат, native просмотр и пользовательское подтверждение.
Не объявлять всю REGION завершённой по успеху одного слоя.

- [ ] Изменения в границах подэтапа выполнены.
- [ ] Обязательные проверки выполнены; команды и фактические результаты записаны.
- [ ] Ограничения и оставшиеся проверки указаны.
- [ ] Статус этого файла, checklist Stage REGION и current-state обновлены.

## Результат выполнения

Ожидает выполнения. Следующий шаг после завершения — [REGION17](region17-default-service.md).
