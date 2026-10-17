# REGION21 — Budget и admission для выбранного канала

[Stage REGION](stage-region.md) · [Общие контракты](region-design.md) · [Протокол](../../library/protocol/regions.md)

**Статус: выполнен 17.10.2026.** Задание подготовлено 16.10.2026.

**Цель:** Core запрос лимита с recipient и scope revision.

**Зависимости:** REGION20. Проверить фактические результаты перед началом.
Рекомендуемый порядок последовательный: предыдущий шаг — [REGION20](region20-private-application.md).
Выполнять только этот подэтап; завершение не запускает следующий автоматически.

**Файлы/область:** SessionCommandGateway.GetTextByteLimit, SendReadinessReader, OutgoingTextProcessor; Core budget tests.
Новые типы/файлы здесь — предлагаемое место, не утверждение об их существовании.
Сначала сверить код, git status и версии схемы/API; готовые механизмы использовать повторно.

## Минимальное чтение

Общий порядок — [правила исполнения REGION](stage-region.md#порядок-работы-агента).
Из [архитектуры](region-design.md) и [протокола](../../library/protocol/regions.md)
прочитать только перечисленные ниже разделы и указанные соседние контракты:

region-design.md: «Отправки, история, повторы и лимиты»; REGION6 API и REGION19/20 snapshots.

## Шаги

1. Расширить прежний nodeId/isChannel query до target/policy snapshot, пригодного для composer и send admission.
2. Перепроверять limit при фактической отправке после ожиданий; invalidated revision возвращает обновлённую оценку либо понятный отказ.
3. Использовать library budget, а не дублировать формулы; stale/unknown default консервативен, draft не обрезается.

**За границами шага:** Правка XAML и самостоятельное повышение transport limits.

## Проверки и завершение

**Обязательные проверки:** Два канала с разными scopes; MTU/default/name changed; Unicode limit; после ожидания limit уменьшился; offline snapshot; TCP/Serial regression.

**Готово:** Core выдаёт единую оценку composer/send с явной freshness.

Если зависимость отсутствует или проверка не выполнена, записать точную причину и
оставить соответствующий пункт открытым. Для аппаратных/GUI проверок различать
автоматический результат, native просмотр и пользовательское подтверждение.
Не объявлять всю REGION завершённой по успеху одного слоя.

- [x] Изменения в границах подэтапа выполнены.
- [x] Обязательные проверки выполнены; команды и фактические результаты записаны.
- [x] Ограничения и оставшиеся проверки указаны.
- [x] Статус этого файла, checklist Stage REGION и current-state обновлены.

## Результат выполнения

MessageService.GetTextBudgetAsync принимает полный OutgoingRecipient с binding/version и возвращает immutable оценку: node/session/generation, policy/default revisions, effective scope, имя отправителя, freshness и support evidence. SendReadinessReader.ReadBudgetAsync объединяет readiness и оценку и не публикует её для другой session generation. Offline оценка не обещает transport byte limit. Старый nodeId/isChannel query сохранён для совместимости; переход composer UI на новый query относится к REGION30.

Scope resolution и library GetTextBudget выполняются под region gate; Core не копирует BLE-формулы. Admission сначала получает budget выбранной политики, затем один раз обрабатывает текст. После ожидания перед invocation повторно проверяются binding и текущий library limit. При уменьшении MTU слишком длинный текст получает отказ до SEND; original text/draft не обрезается. Exact repeat сохраняет effective scope, обновляя только transport assessment текущей сессии.

Проверки через настоящий BLE transport/client поверх loopback GATT facade: два канала с разными scopes, сравнение с library budget, Unicode ровно на границе, превышение без обрезания, уменьшение MTU во время admitted ожидания, default revision, смена имени/session при reconnect и offline freshness. Numeric limits разных scopes могут совпадать из-за radio/cipher-block ограничений — Core сохраняет формулу библиотеки. BLE integration13/13; end-to-end+BLE14/14; Release build0 warnings/errors. Финальные Core/Desktop результаты — в current-state.

Расширенный прогон также выявил тестовые races: RetryWaiting публикуется до регистрации контролируемого таймера (fixture теперь ждёт регистрацию); fake UI dispatcher теперь сериализует callbacks, а resend test освобождает gate через finally. Нативная GUI/железо не проверялись; TCP/Serial regression покрывается существующими Core emulation tests.

Следующий шаг — [REGION22](region22-discovery-preflight.md).
