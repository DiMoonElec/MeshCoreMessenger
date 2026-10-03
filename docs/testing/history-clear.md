# Очистка локальной переписки — D14/D15

03.10.2026. Реализовано по отдельному запросу пользователя после меню переписки,
раньше D7–D13. Эти подэтапы не реализовывались в рамках очистки.

## Поведение

В заголовке Public/Private → меню → «Удалить историю сообщений» открывает
подтверждение с неизменяемыми NodeId/ConversationId и названиями ноды/чата.
Начальный фокус — «Отмена»; Escape отменяет. Удаление выполняется только после
«Удалить». Для пустой или ещё не материализованной переписки пункт недоступен.
Причина недоступности видна в tooltip, в том числе у disabled пункта.

Core HistoryClearService использует отдельный SQLite store. Единственный writer
проверяет node/conversation ownership и незавершённые attempts, захватывает
MAX(LocalSequence), удаляет Messages до этой границы и каскадные SendAttempts,
обновляет read watermark в одной транзакции. AUTOINCREMENT не сбрасывается.
Directory identity, contacts, channels, sessions и draft сохраняются. Миграция
схемы не нужна. Сообщения, сохранённые после cutoff, остаются непрочитанными.
Протокольных команд сервис не вызывает; очистка доступна offline.

ConversationOperationGuard — общий singleton для отправки и очистки. Send admission
регистрируется до gateway/draft flush/Prepare и удерживается до завершения lease,
включая detached ACK observer. Clear запрещён при активной отправке этой переписки,
Sending/Accepted с ожидаемым или неизвестным ACK. Активный Prepared защищён admission;
сохранённый после аварии Prepared без активного владельца можно удалить без передачи.
Accepted без ожидания ACK разрешён после завершения отправки. При paused read/outgoing persistence
очистка недоступна до восстановления сохранения. Будущий D7 обязан использовать
этот же admission для повтора и учитывать отложенные durable writes.

Read tracker сериализует очистку с записью read watermark и игнорирует поздние
позиции до cutoff вместо попытки обновить удалённое сообщение. После commit UI
инвалидирует версии страниц/search, clears buffered history/anchor, перечитывает
проекции и выбранную историю. Старые incoming notifications до cutoff игнорируются.
Смена выбранной переписки не меняет цель подтверждённой операции. Clear workflow
входит в отслеживаемые задачи MainWindow lifetime.

## Выполненные проверки

- Solution Debug/Release build: 0 warnings, 0 errors.
- Core Debug/Release: **243/243**. Добавлено 13 проверок: cascade, draft/identity,
  другая переписка/нода, reopen, пустая история, rollback транзакции, pending
  Prepared/Sending/ACK, Accepted без ACK, входящие после cutoff, поздние read
  writes, cancellation, paused persistence, admission до Prepare.
- Production sender + loopback TCP Companion: до ACK clear отвергается, после ACK
  и завершения lease разрешён; outgoing transmissions не увеличиваются, session
  остаётся Online. Исходная incoming история эмулятора также удаляется.
- Desktop Debug/Release: **291/291**. Добавлено 7 проверок: empty/search/preview/
  unread/draft/restart/другая нода, отмена подготовки подтверждения и смена секции,
  SQL failure, late page/old commit, неизменяемый target, stale availability,
  double operation/busy и честное сообщение при ошибке UI refresh после commit.
- `git diff --check` пройден.

При параллельном запуске Debug/Release финальный Debug runner один раз упал в
существующем `HistoryPagingTests.CancellationInterruptsAnActiveSqliteSearch`
(ожидалась отмена, запрос вернул результат). Отдельный повтор всего Core Debug
runner прошёл **243/243**. Код этого теста и DatabaseReader не изменялись; это
ограничение проверки отмены сохранено в отчёте, успешный повтор не скрывает сбой.

Native audit добавлен:

```sh
dotnet run --project tools/MeshCoreMessenger.ViewportAudit -c Release -- --history-clear-only
```

Он использует production ConversationView/MenuFlyout/HistoryClearDialog и временную
SQLite для Public/Private × Light/Dark × 420/960, проверяет cancel/confirm, draft и
сохраняет screenshots в `$TMPDIR/meshcore-history-clear/`.
**В этой сессии audit не выполнен:** два запуска остановились до открытия окна
в `AppBuilder.SetupWithoutStarting` с Avalonia.Native RenderTimer **-6661**.
Скриншоты и успешная native приёмка не заявляются. Production renderer не менялся.
Аппаратная нода и пользовательская БД для испытаний не использовались.

## Ручная приёмка — ожидается

На disposable переписке: обе темы, узкое окно и длинные названия; Escape/«Отмена»
сохраняют сообщения; подтверждение показывает пустую историю и сохраняет черновик;
после перезапуска история остаётся очищенной; новые входящие появляются нормально.
Во время ожидания ACK пункт недоступен с пояснением. Проверить Public и Private.
