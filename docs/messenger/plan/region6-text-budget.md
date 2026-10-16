# REGION6 — Библиотечный budget с учётом scope

[Stage REGION](stage-region.md) · [Общие контракты](region-design.md) · [Протокол](../../library/protocol/regions.md)

**Статус: выполнен 16.10.2026.** Задание подготовлено 16.10.2026.

**Цель:** Расчёт размера текста для конкретной scope-политики.

**Зависимости:** REGION5. Проверить фактические результаты перед началом.
Рекомендуемый порядок последовательный: предыдущий шаг — [REGION5](region5-private-send.md).
Выполнять только этот подэтап; завершение не запускает следующий автоматически.

**Файлы/область:** MeshCoreSharp/Transport/Ble/BleTextBudget.cs, Client/, Models/; budget tests.
Новые типы/файлы здесь — предлагаемое место, не утверждение об их существовании.
Сначала сверить код, git status и версии схемы/API; готовые механизмы использовать повторно.

## Минимальное чтение

Общий порядок — [правила исполнения REGION](stage-region.md#порядок-работы-агента).
Из [архитектуры](region-design.md) и [протокола](../../library/protocol/regions.md)
прочитать только перечисленные ниже разделы и указанные соседние контракты:

region-design.md: «Отправки, история, повторы и лимиты»; текущий ble-stock-firmware отчёт только раздел лимитов.

## Шаги

1. Передать known scoped/unscoped/unknown в существующий расчёт бюджета, согласовать public query и send validation.
2. Учесть UTF-8 sender prefix и 4-byte radio overhead; Unknown остаётся консервативным. Не принимать stale snapshot как подтверждённый unscoped.
3. Определить metadata resolved scope/result, пригодную для Core; TCP/Serial лимиты сохранить до отдельного доказанного изменения.

**За границами шага:** Composer и DB; глобальный пересмотр размеров TCP/Serial.

## Проверки и завершение

**Обязательные проверки:** MTU boundary, UTF-8 имя, три режима scope, unknown default, текст ровно limit и limit+1, никакого trim.

**Готово:** Public budget и фактическое admission дают одинаковый результат для одного snapshot.

Если зависимость отсутствует или проверка не выполнена, записать точную причину и
оставить соответствующий пункт открытым. Для аппаратных/GUI проверок различать
автоматический результат, native просмотр и пользовательское подтверждение.
Не объявлять всю REGION завершённой по успеху одного слоя.

- [x] Изменения в границах подэтапа выполнены.
- [x] Обязательные проверки выполнены; команды и фактические результаты записаны.
- [x] Ограничения и оставшиеся проверки указаны.
- [x] Статус этого файла, checklist Stage REGION и current-state обновлены.

## Результат выполнения

Добавлены EffectiveFloodScope, SendScopeSnapshot и MessageTextBudget. Публичный GetTextBudget(kind,scope) не выполняет I/O; результат содержит session ID, время, selection/effective state, имя/ключ и fingerprint либо UnknownReason. Accepted send results сохраняют фактический budget. BLE helper принимает scoped/unscoped/unknown; unknown консервативен, 4-byte region overhead относится только к radio budget. TCP/Serial формулы сохранены.

Default cache наполняется только GET в RX acceptance hook; SET/CLEAR инвалидируют наблюдение до освобождения immediate gate. Reconnect начинает Unknown и новую session ID. Сохранённый прежний snapshot immutable; send не принимает его как свежий: повторно вычисляет admission после ожидания scope gate и перед текстовым TX. Текущая firmware формула даёт одинаковый text maximum при scoped/unscoped, поэтому увеличения лимитов нет.

Проверка: dotnet run --project tests/MeshCoreSharp.Tests -c Release --no-restore — 208/208. Четыре новых cases проверяют MTU23/160/176/247, UTF-8 prefix, overhead, все scope selections, unknown/known-empty/invalidation/reconnect, limit/limit+1, metadata и изменение MTU во время scope wait без text TX/trim. Real BLE fixtures прежнего stock suite также проходят. Hardware limits проверяются отдельно в REGION32/33.

Следующий шаг — [REGION7](region7-discovery-wire.md).
