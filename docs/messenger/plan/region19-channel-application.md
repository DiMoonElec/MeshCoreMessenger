# REGION19 — Region scope в отправке и повторе канала

[Stage REGION](stage-region.md) · [Общие контракты](region-design.md) · [Протокол](../../library/protocol/regions.md)

**Статус: не начат.** Задание подготовлено 16.10.2026.

**Цель:** Подключение channel preference к существующему MessageService.

**Зависимости:** REGION18. Проверить фактические результаты перед началом.
Рекомендуемый порядок последовательный: предыдущий шаг — [REGION18](region18-attempt-storage.md).
Выполнять только этот подэтап; завершение не запускает следующий автоматически.

**Файлы/область:** MessageService, SessionCommandLease, Directory read contracts; channel send/repeat tests.
Новые типы/файлы здесь — предлагаемое место, не утверждение об их существовании.
Сначала сверить код, git status и версии схемы/API; готовые механизмы использовать повторно.

## Минимальное чтение

Общий порядок — [правила исполнения REGION](stage-region.md#порядок-работы-агента).
Из [архитектуры](region-design.md) и [протокола](../../library/protocol/regions.md)
прочитать только перечисленные ниже разделы и указанные соседние контракты:

region-design.md: «Отправки, история, повторы и лимиты»; d5-channel-send.md и текущий channel repeat workflow.

## Шаги

1. Разрешить preference/default snapshot вместе с binding/generation до wire вызова и записать его через REGION18.
2. Передать scope в library overload; mapping обязан отличать SET failure до SEND, accepted SEND и cleanup uncertainty.
3. Повторить с исходным resolved snapshot; send-as-new использует текущие настройки. Изменение настройки в очереди не меняет уже admitted попытку.

**За границами шага:** Новая UI-настройка канала, изменение дедупликации и автоматический retry каналов.

## Проверки и завершение

**Обязательные проверки:** A/B scopes; duplicate slots; preference changed during wait; default changed after capture; error до SEND; accepted+cleanup failure; legacy repeat предлагает новое действие без фиктивного snapshot.

**Готово:** Существующие channel send/repeat сохраняют историю и используют точную scope-политику.

Если зависимость отсутствует или проверка не выполнена, записать точную причину и
оставить соответствующий пункт открытым. Для аппаратных/GUI проверок различать
автоматический результат, native просмотр и пользовательское подтверждение.
Не объявлять всю REGION завершённой по успеху одного слоя.

- [ ] Изменения в границах подэтапа выполнены.
- [ ] Обязательные проверки выполнены; команды и фактические результаты записаны.
- [ ] Ограничения и оставшиеся проверки указаны.
- [ ] Статус этого файла, checklist Stage REGION и current-state обновлены.

## Результат выполнения

Ожидает выполнения. Следующий шаг после завершения — [REGION20](region20-private-application.md).
