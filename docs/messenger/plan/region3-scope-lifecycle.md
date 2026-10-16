# REGION3 — Жизненный цикл составной scope-операции

[Stage REGION](stage-region.md) · [Общие контракты](region-design.md) · [Протокол](../../library/protocol/regions.md)

**Статус: выполнен 16.10.2026.** Задание подготовлено 16.10.2026.

**Цель:** Внутренний coordinator, конечные состояния и обработка неопределённого ответа.

**Зависимости:** REGION2. Проверить фактические результаты перед началом.
Рекомендуемый порядок последовательный: предыдущий шаг — [REGION2](region2-default-api.md).
Выполнять только этот подэтап; завершение не запускает следующий автоматически.

**Файлы/область:** MeshCoreSharp/Runtime/, Client/, Exceptions/ и существующие fake transport tests.
Новые типы/файлы здесь — предлагаемое место, не утверждение об их существовании.
Сначала сверить код, git status и версии схемы/API; готовые механизмы использовать повторно.

## Минимальное чтение

Общий порядок — [правила исполнения REGION](stage-region.md#порядок-работы-агента).
Из [архитектуры](region-design.md) и [протокола](../../library/protocol/regions.md)
прочитать только перечисленные ниже разделы и указанные соседние контракты:

region-design.md: «Библиотека: безопасная отправка», «Ошибки, отмена, восстановление»; library/architecture: immediate-transactions, api-and-errors, concurrency.

## Шаги

1. Ввести internal ScopeOperationGate и encoders команды54; определить результат отдельно для SET, принятия SEND и RESET, чтобы cleanup failure не терял факт принятой отправки.
2. Реализовать cancellation до TX, bounded cleanup на здоровом command stream и закрытие admission при неопределённом immediate ответе; не разрешать позднему OK завершить новую команду.
3. Подключить fault/teardown к существующему client lifecycle, завершать ожидающие операции; провести coordinator через тестовый внутренний send delegate без нового публичного raw API.

**За границами шага:** Переустройство всего runtime, reconnect/replay и production text overloads.

## Проверки и завершение

**Обязательные проверки:** Отмена на каждой границе; SET error → zero SEND; late OK после timeout; accepted SEND + failed RESET; disconnect; ожидающий gate освобождается без deadlock.

**Готово:** Таблица исходов и fake-тесты доказывают безопасный выход из каждой фазы.

Если зависимость отсутствует или проверка не выполнена, записать точную причину и
оставить соответствующий пункт открытым. Для аппаратных/GUI проверок различать
автоматический результат, native просмотр и пользовательское подтверждение.
Не объявлять всю REGION завершённой по успеху одного слоя.

- [x] Изменения в границах подэтапа выполнены.
- [x] Обязательные проверки выполнены; команды и фактические результаты записаны.
- [x] Ограничения и оставшиеся проверки указаны.
- [x] Статус этого файла, checklist Stage REGION и current-state обновлены.

## Результат выполнения

Добавлены внутренний ScopeOperationCoordinator, ScopeOperationResult<T> и три формы команды54. Scope gate принадлежит сессии; command dispatcher закрывает admission до освобождения CommandGate при неопределённом scope-ответе. В существующий client lifecycle подключены Faulted, остановка pump/RX и отслеживаемый disconnect. Explicit reconnect начинает новую generation, старые queued commands не входят в неё. Публичного raw API нет.

| Фаза/исход | Действие |
| --- | --- |
| Отмена до SET / в очереди gate | Нет TX |
| SET ERROR | Нет SEND; gate освобождён |
| SET timeout/cancel после TX | Admission закрыт; disconnect; RESET не отправляется |
| SET принят, SEND не выполнен или отвергнут | Bounded RESET с независимым от caller token |
| SEND принят, RESET подтверждён | Возвращается принятый результат |
| SEND принят, RESET ERROR/timeout | Принятый результат и CleanupError; admission закрыт, disconnect |
| Session stop | Прерывает работу и ожидание scope gate |

Проверка: dotnet run --project tests/MeshCoreSharp.Tests -c Release --no-restore — 194/194, девять новых lifecycle cases. Проверены cancellation до TX/в очереди/во время SET/перед SEND/после acceptance, SET error, late OK с queued ordinary command, RESET error/timeout, disconnect и explicit reconnect. Общие legacy timeout recovery tests сохранились: invalidation включается только для новых guarded exchanges. Production send overloads подключаются в REGION4/5; их SEND delegates обязаны использовать guarded dispatcher. Hardware не требуется.

Следующий шаг — [REGION4](region4-channel-send.md).
