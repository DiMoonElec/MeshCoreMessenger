# REGION8 — Сбор ответов соседних репитеров

[Stage REGION](stage-region.md) · [Общие контракты](region-design.md) · [Протокол](../../library/protocol/regions.md)

**Статус: выполнен 16.10.2026.** Задание подготовлено 16.10.2026.

**Цель:** Один bounded поиск с public DiscoverRepeaterNeighborsAsync.

**Зависимости:** REGION7. Проверить фактические результаты перед началом.
Рекомендуемый порядок последовательный: предыдущий шаг — [REGION7](region7-discovery-wire.md).
Выполнять только этот подэтап; завершение не запускает следующий автоматически.

**Файлы/область:** MeshCoreSharp/Client/, Runtime/PacketRouter.cs, новый internal collector.
Новые типы/файлы здесь — предлагаемое место, не утверждение об их существовании.
Сначала сверить код, git status и версии схемы/API; готовые механизмы использовать повторно.

## Минимальное чтение

Общий порядок — [правила исполнения REGION](stage-region.md#порядок-работы-агента).
Из [архитектуры](region-design.md) и [протокола](../../library/protocol/regions.md)
прочитать только перечисленные ниже разделы и указанные соседние контракты:

protocol/regions.md: «1. Найти соседние репитеры»; library/architecture/packet-matching.md.

## Шаги

1. Зарегистрировать collector до CMD55, запросить full keys с repeater filter и свежим ненулевым tag.
2. Собирать несколько push до deadline, dedupe по full key, ограничить память и число активных scans; OK не закрывает окно сбора.
3. Разделить cancellation/fault и успешный пустой список, удалять waiter при завершении; поздний response остаётся диагностическим.

**За границами шага:** Запрос списка регионов, addContact и автоматический выбор региона.

## Проверки и завершение

**Обязательные проверки:** Ответ до возврата transport.SendAsync; несколько соседей; duplicate/wrong tag; prefix-only response; timeout window; cancel/disconnect/reconnect; pushes продолжаются.

**Готово:** Поиск возвращает только корректные полные identity и достоверный статус завершения.

Если зависимость отсутствует или проверка не выполнена, записать точную причину и
оставить соответствующий пункт открытым. Для аппаратных/GUI проверок различать
автоматический результат, native просмотр и пользовательское подтверждение.
Не объявлять всю REGION завершённой по успеху одного слоя.

- [x] Изменения в границах подэтапа выполнены.
- [x] Обязательные проверки выполнены; команды и фактические результаты записаны.
- [x] Ограничения и оставшиеся проверки указаны.
- [x] Статус этого файла, checklist Stage REGION и current-state обновлены.

## Результат выполнения

Добавлены DiscoverRepeaterNeighborsAsync, immutable options/result и bounded internal collector. До CMD55 установлен runtime observer; tag ненулевой и новый на каждой операции, запрос full-key/repeater-filter. Collector принимает только full32 repeater identity с нужным tag, dedupe по полному ключу, максимум1 active scan и1–4096 записей. CapacityReached отмечает отброшенные новые identity. OK не завершает окно; пустой результат — успешное наблюдение, не доказательство отсутствия репитеров.

PacketRouter получил внутренние nonblocking observers; command RX acceptance hook выполняется перед observer routing. Public push сохраняется для каждого ответа. Scope gate удерживается только на immediate CMD55/OK; сбор идёт независимо от CommandGate/ScopeGate. Cancel/fault/disconnect удаляют observer и timer, reconnect не переносит старый tag/session. Не выполняются CMD57 или contact mutations.

Проверка: dotnet run --project tests/MeshCoreSharp.Tests -c Release --no-restore — 215/215. Четыре новых cases: deterministic deadline/capacity/dedupe/wrong-tag/prefix/wrong-type; response до завершения transport write и interleaved pushes; независимая local command после OK; concurrent-scan rejection; cancel/disconnect/reconnect и late response. Аппаратный scan остаётся REGION33.

Следующий шаг — [REGION9](region9-remote-runtime.md).
