# ЛС: P1 — явный send API и планировщик повторов

Дата: 05.10.2026. [План P1–P8](../messenger/plan/d7-private-auto-retry.md).

## Реализовано

- MeshCoreClient.SendTextAsync(recipient, text, timestamp, attempt, cancellationToken)
  для полного ключа и Contact. Один вызов — один TX, отдельно Delivery; старый
  overload сохраняет автоматический timestamp и attempt 0.
- Encoder принимает attempt 0…3, сохраняет supplied timestamp и полный UTF-8 текст
  до 160 байт; extended attempt и invalid/oversized text отклоняются до TX.
- Новый timestamp продвигает общий client floor, но повтор старого timestamp
  не переписывается и не уменьшает floor. ACK authoritative из MSG_SENT.
- ICompanionClient/production adapter передают explicit параметры без raw frames.
- Core PrivateRetryPolicy с режимами SameTimestampIncrementAttempt и
  NewTimestampResetAttempt; внутренний PrivateRetryPlan выбирает 3 исходных flood
  либо 3 known + 2 fallback flood. Сброс на четвёртой передаче; новый timestamp на
  границе фаз. AttemptNumber 1…N отличается от WireAttempt 0…2 и WireMessageOrdinal.
- ResolveTimestamp требует reservation нового timestamp > предыдущего; не читает
  часы и не резервирует БД. Возвращает прежний timestamp для same-identity repeat.

Ограничение части: coordinator, DB reservation/history, projections и incoming dedup
ещё не реализованы. Политика ещё не передаётся через PrivateSendRequest. Никаких
автоматических повторов/reset в production MessageService; schema остаётся v4.

## Проверки

Release build решения: 0 warnings, 0 errors.

- MeshCoreSharp.Tests: **105/105**, включая 6 новых сценариев wire encoding,
  160/161 UTF-8 bytes, отказ до TX, cancellation перед TX/во время Delivery,
  matching трёх явных attempts с обратными/duplicate/unrelated ACK, ACK до возврата
  transport.SendAsync, общий timestamp floor private/channel и overload Contact.
- MeshCoreMessenger.Core.Tests: **282/282**, включая 9 новых случаев планировщика/
  resolver/TCP adapter. Четыре TCP-сценария покрывают оба режима × оба initial route.
- MeshCoreMessenger.Desktop.Tests: **315/315** — прежняя отправка/UI не регрессировали.

TCP-эмулятор сохраняет wire attempt и реальный flood flag. Опционально вычисляет
ACK по формуле BaseChatMesh: SHA-256-prefix4(timestamp | attempt | UTF-8 text |
sender key 00…1F); отдельный fixture проверяет значения без recipient в digest.
Для default known/fallback с текстом «Тест 👋» и T1=1700000123/T2=1700000124
байты ACK-тегов: 908A06F6, 9AAB264B, 159A6C30, 73BCB2E7, 3FCB2794.
Тестовый driver явно вызывает каждый одиночный send/reset, затем подаёт ACK в
обратном порядке и проверяет чужие/повторные теги. Это не приёмка automatic cycle.
Прежний тест двух одинаковых сообщений подряд (одному/двум контактам) также переведён
на protocol-derived ACK вместо искусственного счётчика.

Первый Core-прогон в песочнице не смог открыть loopback port (Permission denied).
Повтор с разрешённым локальным TCP прошёл полностью; это ограничение среды.
Аппаратная нода и native GUI не запускались.

## Следующий шаг

P2: миграция cycle/wire-message/attempt/route/history, durable timestamp floor и
атомарный PreparePrivateAttempt. Затем P3: общий ACK commit/итог MessageId/analytics.
Не включать цикл на основе одного только готового API/планировщика.
