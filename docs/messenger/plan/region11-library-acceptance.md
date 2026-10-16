# REGION11 — Регрессия библиотеки REGION

[Stage REGION](stage-region.md) · [Общие контракты](region-design.md) · [Протокол](../../library/protocol/regions.md)

**Статус: выполнен 16.10.2026.** Задание подготовлено 16.10.2026.

**Цель:** Совместная проверка новых API и старого протокола на fake transports.

**Зависимости:** REGION10. Проверить фактические результаты перед началом.
Рекомендуемый порядок последовательный: предыдущий шаг — [REGION10](region10-request-regions.md).
Выполнять только этот подэтап; завершение не запускает следующий автоматически.

**Файлы/область:** tests/MeshCoreSharp.Tests; docs/testing/region-library.md (создать).
Новые типы/файлы здесь — предлагаемое место, не утверждение об их существовании.
Сначала сверить код, git status и версии схемы/API; готовые механизмы использовать повторно.

## Минимальное чтение

Общий порядок — [правила исполнения REGION](stage-region.md#порядок-работы-агента).
Из [архитектуры](region-design.md) и [протокола](../../library/protocol/regions.md)
прочитать только перечисленные ниже разделы и указанные соседние контракты:

region-design.md: «Сквозные критерии приёмки»; результаты REGION1–10, только при выявленном пересечении.

## Шаги

1. Собрать channel A/B/private/default/discovery сценарии на общем client; моделировать interleaved pushes и uncertain immediate response.
2. Проверить старые firmware capabilities без записи при connect; библиотека не выполняет auto discovery/replay и сохраняет одну assembly.
3. Прогнать library suite и Release targets, зафиксировать команды/результаты и непроверенные hardware ограничения.

**За границами шага:** Аппаратные передачи и утверждение, что fake проверяет автономный ACK прошивки.

## Проверки и завершение

**Обязательные проверки:** Полный library suite на используемых Windows/portable targets; end-to-end fake wire с fragmentation/coalescing уже существующего транспорта; отсутствие регрессий ACK/drain.

**Готово:** Библиотечный фундамент проверен; следующему агенту не нужно достраивать runtime в Core.

Если зависимость отсутствует или проверка не выполнена, записать точную причину и
оставить соответствующий пункт открытым. Для аппаратных/GUI проверок различать
автоматический результат, native просмотр и пользовательское подтверждение.
Не объявлять всю REGION завершённой по успеху одного слоя.

- [x] Изменения в границах подэтапа выполнены.
- [x] Обязательные проверки выполнены; команды и фактические результаты записаны.
- [x] Ограничения и оставшиеся проверки указаны.
- [x] Статус этого файла, checklist Stage REGION и current-state обновлены.

## Результат выполнения

Сквозная Serial fake wire приёмка объединила fragmented/coalesced RX, каналы A/B, private/default, ранний ACK, automatic drain и обе discovery операции. Добавлены регрессии definite local TX rejection и duplicate ACK hook failure; исправлено различение подтверждённого ответа от uncertain immediate outcome. Общий sample/Core emulator поддерживает новые scope-команды.

[Полный отчёт и команды](../../testing/region-library.md): Windows library225/225, portable223/223, Core460/460; обе locked restore/Release solution builds без warnings/errors. Avalonia build telemetry отключена только CLI property из-за попытки записи вне workspace; NuGetAudit отключён в offline restore. Package locks не менялись. Hardware/firmware compatibility и autonomous ACK остаются REGION32/33.

Следующий шаг — [REGION12](region12-core-models.md).
