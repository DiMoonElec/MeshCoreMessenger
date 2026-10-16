# REGION4 — Scope-aware отправка в канал

[Stage REGION](stage-region.md) · [Общие контракты](region-design.md) · [Протокол](../../library/protocol/regions.md)

**Статус: выполнен 16.10.2026.** Задание подготовлено 16.10.2026.

**Цель:** Одна канальная отправка с атомарным относительно других команд выбором scope.

**Зависимости:** REGION3. Проверить фактические результаты перед началом.
Рекомендуемый порядок последовательный: предыдущий шаг — [REGION3](region3-scope-lifecycle.md).
Выполнять только этот подэтап; завершение не запускает следующий автоматически.

**Файлы/область:** MeshCoreSharp/Client/MeshCoreClient.cs, scope coordinator; channel send tests.
Новые типы/файлы здесь — предлагаемое место, не утверждение об их существовании.
Сначала сверить код, git status и версии схемы/API; готовые механизмы использовать повторно.

## Минимальное чтение

Общий порядок — [правила исполнения REGION](stage-region.md#порядок-работы-агента).
Из [архитектуры](region-design.md) и [протокола](../../library/protocol/regions.md)
прочитать только перечисленные ниже разделы и указанные соседние контракты:

region-design.md: «Библиотека: безопасная отправка»; library/protocol/outgoing-messages.md.

## Шаги

1. Добавить scope overload SendChannelTextAsync и подключить SET→SEND→RESET через coordinator; сохранять supplied timestamp.
2. Включить legacy channel/private sends и SET/CLEAR default в тот же gate как минимальную защиту от interleaving; explicit private overload остаётся REGION5.
3. Оставить RX/drain/безопасные локальные чтения независимыми. На unsupported старом устройстве legacy path сохраняется, explicit region не заменяется молча.

**За границами шага:** Новая политика ACK, Core/GUI, увеличение текстовых лимитов; изоляция автономного firmware ACK не обещается.

## Проверки и завершение

**Обязательные проверки:** Каналы A/B с разными scopes; legacy private между SET и SEND; default mutation в гонке; unscoped при заданном default; accepted + cleanup failure.

**Готово:** Scope канальной передачи не протекает в другую управляемую клиентом отправку.

Если зависимость отсутствует или проверка не выполнена, записать точную причину и
оставить соответствующий пункт открытым. Для аппаратных/GUI проверок различать
автоматический результат, native просмотр и пользовательское подтверждение.
Не объявлять всю REGION завершённой по успеху одного слоя.

- [x] Изменения в границах подэтапа выполнены.
- [x] Обязательные проверки выполнены; команды и фактические результаты записаны.
- [x] Ограничения и оставшиеся проверки указаны.
- [x] Статус этого файла, checklist Stage REGION и current-state обновлены.

## Результат выполнения

Добавлен scope overload SendChannelTextAsync(slot,text,timestamp,scope,ct), выполнен SET→SEND→RESET. Legacy channel/private и SET/CLEAR default используют общий scope gate. Результаты канала/ЛС сохраняют ScopeCleanupError отдельно от acceptance. Private ACK waiter запускается до RESET; capacity ожидается до scope gate. Shutdown ждёт tracked scope teardown.

Совместимость: DEVICE_INFO сохраняет protocol version в текущей session; explicit scope при отсутствии версии выполняет read-only query. v8+ допускает temporary key, v12+ force-unscoped. Подтверждённый UNSUPPORTED_CMD отключает temporary scope на сессию; explicit возвращает NotSupportedException, legacy продолжает прежний путь. До получения protocol version обычные legacy sends сохраняют прежний wire path, всегда внутри общего gate; они не заявляют известный effective scope. Application startup уже читает DEVICE_INFO; расширенные capabilities остаются REGION16.

Матрица transmit API: channel/private text проходят coordinator; default SET/CLEAR проходят gate; advertisement использует firmware default key напрямую (MyMesh.cpp CMD_SEND_SELF_ADVERT), временный override его не определяет. Безопасные reads, incoming drain и push не берут scope gate. Discovery transmit добавляется REGION8/10; публичного raw transmit API нет. Автономные firmware ACK по-прежнему не изолируются gate.

Проверка: dotnet run --project tests/MeshCoreSharp.Tests -c Release --no-restore — 200/200. Шесть новых cases: concurrent A/B scopes с timestamps; legacy private и mutation между SET/SEND; force-unscoped при default; accepted+RESET error; old firmware; unsupported explicit; safe read и push между фазами. Hardware проверка будет REGION32/33.

Следующий шаг — [REGION5](region5-private-send.md).
