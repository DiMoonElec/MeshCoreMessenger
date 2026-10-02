# B7.3 — отменяемое и восстанавливаемое закрытие (выполнено 28.09.2026)

[Оглавление](../../MESSENGER_PLAN.md) · [Маршрутизация чтения](../../README.md)

##### B7.3 — отменяемое и восстанавливаемое закрытие (выполнено 28.09.2026)

- Перестать подавлять persistence failure в shutdown contract. Закрытие должно
  сообщать результат и сохранять возможность повторить durable session-end/ingest
  flush до уничтожения writer.
- Перехватить Avalonia shutdown/closing: обычное закрытие отменяется на время
  quiesce/barriers/commit. При ошибке окно, pending DTO, SQLite и instance lock
  остаются живыми; после явного retry закрытие повторяется. Параллельные запросы
  закрытия объединяются в одну операцию. OS shutdown остаётся документированным
  best-effort сценарием.

Тесты B7.3: сообщение на границе shutdown; успешный и неуспешный flush; retry без
потери pending DTO; повтор закрытия; session end; точный порядок disposal; освобождение
БД и instance lock только после успеха; история доступна после рестарта.

Реализовано: `DesktopShutdownCoordinator` объединяет параллельные запросы закрытия и
выполняет единственную последовательность `stop UI scenarios -> shutdown supervisor ->
ingest commit barrier -> session-end barrier`. `MainWindow.Closing` отменяет обычное
закрытие до успешного результата. При persistence failure окно остаётся открытым,
показывает явную ошибку и следующая попытка закрытия повторяет только незавершённые
durable операции, не перезапуская quiesce/supervisor shutdown.

`MessageIngestor.FlushAsync` теперь завершается ошибкой как для уже приостановленного
writer, так и при сбое, возникшем во время ожидания барьера; текущий DTO остаётся в
очереди до `RetryAsync`. Добавлен singleton `SessionCompletionTracker`, сохраняющий в
памяти неудавшиеся записи `Sessions.EndedUtc`/причины и повторяющий их по явному retry.
После успешных барьеров Avalonia выходит из main loop, затем `Program` закрывает DI,
SQLite и instance lock в прежнем порядке. При OS shutdown выполняется best-effort
финальный прогон без обещания отменить завершение системы; этот неизбежный риск
остаётся явно отделён от штатного закрытия.

Проверено: полный Release build без предупреждений; Core tests — 96/96, Desktop —
46/46, MeshCoreSharp regression suite — 92/92. Fake-тесты подтверждают точный порядок,
одну операцию для параллельных запросов, успешный/неуспешный flush, сохранение pending
message/session-end до retry и отсутствие повторного quiesce. Интеграционный тест с
временной SQLite и настоящим instance lock подтверждает, что после ошибки они остаются
доступны, а после успешного retry освобождаются; сохранённая история читается после
повторного открытия. Существующий barrier-тест подтверждает приём сообщения во время
session shutdown. Физическая нода не использовалась; schema и MeshCoreSharp/protocol
не изменялись.
