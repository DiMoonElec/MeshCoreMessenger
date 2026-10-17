# REGION19 — Region scope в отправке и повторе канала

[Stage REGION](stage-region.md) · [Общие контракты](region-design.md) · [Протокол](../../library/protocol/regions.md)

**Статус: выполнен 17.10.2026.** Задание подготовлено 16.10.2026.

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

- [x] Изменения в границах подэтапа выполнены.
- [x] Обязательные проверки выполнены; команды и фактические результаты записаны.
- [x] Ограничения и оставшиеся проверки указаны.
- [x] Статус этого файла, checklist Stage REGION и current-state обновлены.

## Результат выполнения

MessageService разрешает node-scoped channel preference и default под session region gate до durable preparation. Сохранённый effective scope передаётся typed library overload; изменение назначения/default после capture не переписывает admitted попытку. Дубли слотов используют одно предпочтение существующего ChannelId; binding generation не меняется.

Exact repeat читает первый immutable attempt scope и сохраняет прежний effective key/unscoped; send-as-new разрешает текущую политику. LegacyUnknown/Unknown exact repeat отказывается с RegionReplayUnavailableException и указанием действия send-as-new. На старой прошивке inherited KnownEmpty использует InheritDefault только при неизменившемся текущем default под gate; явно выбранный unsupported режим не подменяется.

Библиотека сохраняет тип исходной MeshCoreException, добавляя typed evidence TextSendWasInvoked=false при сбое до SEND; cancellation до SEND имеет отдельный subtype OperationCanceledException. Core отличает такие ошибки от возможной передачи. Accepted SEND + RESET failure остаётся Accepted с ScopeCleanupFailed в истории и отдельным ScopeCleanupError в результате, без retry.

Проверки: полный Core509/509 до дополнительного legacy compatibility case; финальный targeted7/7. Библиотека227/227, включая SET timeout→no SEND evidence; Release solution build0 warnings/errors. Покрыты A/B, duplicate slots, preference/default change после capture, exact repeat/as-new, отказ до SEND и accepted+cleanup failure. Нативная GUI/аппаратная приёмка остаётся REGION31–33; scope-aware composer budget — REGION21.

Следующий шаг — [REGION20](region20-private-application.md).
