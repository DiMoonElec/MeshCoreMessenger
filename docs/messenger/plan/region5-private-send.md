# REGION5 — Scope-aware ЛС и ACK admission

[Stage REGION](stage-region.md) · [Общие контракты](region-design.md) · [Протокол](../../library/protocol/regions.md)

**Статус: выполнен 16.10.2026.** Задание подготовлено 16.10.2026.

**Цель:** Поддержка scope в существующих перегрузках ЛС.

**Зависимости:** REGION4. Проверить фактические результаты перед началом.
Рекомендуемый порядок последовательный: предыдущий шаг — [REGION4](region4-channel-send.md).
Выполнять только этот подэтап; завершение не запускает следующий автоматически.

**Файлы/область:** MeshCoreSharp/Client/MeshCoreClient.cs, Runtime/AckTracker.cs; private send tests.
Новые типы/файлы здесь — предлагаемое место, не утверждение об их существовании.
Сначала сверить код, git status и версии схемы/API; готовые механизмы использовать повторно.

## Минимальное чтение

Общий порядок — [правила исполнения REGION](stage-region.md#порядок-работы-агента).
Из [архитектуры](region-design.md) и [протокола](../../library/protocol/regions.md)
прочитать только перечисленные ниже разделы и указанные соседние контракты:

region-design.md: «Библиотека: безопасная отправка»; protocol/regions.md: «Уточнение: ЛС, ACK и асимметричные маршруты».

## Шаги

1. Добавить explicit scope к перегрузке с timestamp/attempt; остальные overloads направить в общий путь с InheritDefault.
2. Ждать private capacity до scope gate; установить ACK matching до отправки, вернуть Delivery отдельно от завершения scope cleanup.
3. Не менять known/direct выбор прошивки, формат ACK и число попыток; представить ошибки scope preparation отдельно от неопределённой отправки текста.

**За границами шага:** Per-contact GUI, автоматические повторы библиотеки и изменение routing алгоритма.

## Проверки и завершение

**Обязательные проверки:** ACK сразу после MSG_SENT, несколько Delivery waiters, capacity waiting, channel send во время ACK wait, SET failure без private TX, cleanup failure после MSG_SENT.

**Готово:** Все public private-text пути сохраняют ACK-контракт и входят в общий scope coordinator.

Если зависимость отсутствует или проверка не выполнена, записать точную причину и
оставить соответствующий пункт открытым. Для аппаратных/GUI проверок различать
автоматический результат, native просмотр и пользовательское подтверждение.
Не объявлять всю REGION завершённой по успеху одного слоя.

- [x] Изменения в границах подэтапа выполнены.
- [x] Обязательные проверки выполнены; команды и фактические результаты записаны.
- [x] Ограничения и оставшиеся проверки указаны.
- [x] Статус этого файла, checklist Stage REGION и current-state обновлены.

## Результат выполнения

Добавлены explicit scope overloads SendTextAsync для full key и Contact с timestamp/attempt. Legacy overloads направлены в тот же путь с InheritDefault. AckTracker admission/circular window ожидаются до ScopeGate, RX Bind выполняется до обработки следующего ACK. Delivery запускается после MSG_SENT до RESET и возвращается независимо от cleanup outcome; ошибки preparation идентифицируются по SetFloodScopeKey / NotSupportedException, guarded SEND timeout сохраняет SendTextAsync operation и закрывает admission. Формат private command/ACK, direct/flood routing и число попыток не менялись.

Проверка: dotnet run --project tests/MeshCoreSharp.Tests -c Release --no-restore — 204/204. Четыре новых cases: MSG_SENT+ACK до завершения transport write; timestamp/attempt; восемь concurrent Delivery с девятым capacity waiter и независимым channel send; SET error без private TX; принятый MSG_SENT и подтверждённый ACK при RESET error. Предыдущие private cancellation/window/duplicate-tag tests также проходят. Аппаратная проверка и асимметричные маршруты остаются REGION33.

Следующий шаг — [REGION6](region6-text-budget.md).
