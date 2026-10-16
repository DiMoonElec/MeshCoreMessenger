# REGION9 — Общий gate и matcher удалённых запросов

[Stage REGION](stage-region.md) · [Общие контракты](region-design.md) · [Протокол](../../library/protocol/regions.md)

**Статус: выполнен 16.10.2026.** Задание подготовлено 16.10.2026.

**Цель:** Небольшой runtime для MSG_SENT→BINARY_RESPONSE.

**Зависимости:** REGION8. Проверить фактические результаты перед началом.
Рекомендуемый порядок последовательный: предыдущий шаг — [REGION8](region8-neighbors.md).
Выполнять только этот подэтап; завершение не запускает следующий автоматически.

**Файлы/область:** MeshCoreSharp/Runtime/PacketRouter.cs, новый internal remote tracker; runtime tests.
Новые типы/файлы здесь — предлагаемое место, не утверждение об их существовании.
Сначала сверить код, git status и версии схемы/API; готовые механизмы использовать повторно.

## Минимальное чтение

Общий порядок — [правила исполнения REGION](stage-region.md#порядок-работы-агента).
Из [архитектуры](region-design.md) и [протокола](../../library/protocol/regions.md)
прочитать только перечисленные ниже разделы и указанные соседние контракты:

library/architecture/concurrency.md; library/protocol/remote-requests.md; region-design.md: «Библиотека: безопасная отправка».

## Шаги

1. Ввести MeshRequestGate на весь remote exchange; CommandGate удерживать только до immediate ответа.
2. Установить deferred ожидатель до TX и связать tag в nonblocking RX hook MSG_SENT до completion; не кэшировать произвольные старые responses.
3. Определить bounded remote deadline и cleanup; зафиксировать единый порядок gates и обязательность того же remote gate для будущих login/status/path API.

**За границами шага:** Реализация всех remote commands библиотеки и UI login.

## Проверки и завершение

**Обязательные проверки:** MSG_SENT+final push одним чтением; wrong/late tag; immediate ERROR; final timeout; disconnect; локальная команда и ACK во время remote wait.

**Готово:** Remote state не теряет ранний ответ и не блокирует непрерывный RX.

Если зависимость отсутствует или проверка не выполнена, записать точную причину и
оставить соответствующий пункт открытым. Для аппаратных/GUI проверок различать
автоматический результат, native просмотр и пользовательское подтверждение.
Не объявлять всю REGION завершённой по успеху одного слоя.

- [x] Изменения в границах подэтапа выполнены.
- [x] Обязательные проверки выполнены; команды и фактические результаты записаны.
- [x] Ограничения и оставшиеся проверки указаны.
- [x] Статус этого файла, checklist Stage REGION и current-state обновлены.

## Результат выполнения

Добавлены session-owned MeshRequestCoordinator и BinaryResponseWaiter. Один MeshRequestGate удерживается до terminal binary response; observer установлен до TX. Tag привязывается в nonblocking MSG_SENT RX acceptance hook до completion; ответы до bind или с другим tag не кэшируются. Deadline начинается на RX bind, включает slow transport completion и ограничен1–60000ms (без override: firmware suggestion +1000ms, clamp1000–60000ms). Timer/observer удаляются на всех выходах; session token отменяет текущую операцию и очередь.

Порядок для всех будущих remote login/status/path/binary API: MeshRequestGate → ScopeOperationGate → CommandGate. Scope/command gate освобождаются после immediate phase, radio wait держит только remote gate. В этом шаге добавлен runtime, public CMD57 соединяется в REGION10.

Проверка: dotnet run --project tests/MeshCoreSharp.Tests -c Release --no-restore — 219/219. Четыре новых cases: отсутствие кеша/wrong/late tag и deterministic timeout; gate до final с независимой local command и ACK; immediate ERROR, final timeout, cancel/disconnect/queued cancellation; MSG_SENT hook + final push до возврата transport write. Hardware не требуется.

Следующий шаг — [REGION10](region10-request-regions.md).
