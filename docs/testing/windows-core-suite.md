# Windows: восемь ошибок Core suite

15.10.2026: разбор по запросу пользователя после BLE2. Исправления выполнены поверх
изменений BLE2, без изменения его контракта и без аппаратной ноды.
**Итог: Core 389/389; исходные восемь сбоев устранены.** Native macOS не проверялся.

## Воспроизведение

Windows, .NET SDK 10.0.401, прежние package pins и global.json latestFeature.
Команда из корня:

```powershell
dotnet run --project tests/MeshCoreMessenger.Core.Tests -c Release --no-build -- -noLogo
```

До исправлений повторно получены **387 tests / 8 failed / 379 passed** (44,942 с),
совпадающие с baseline BLE1. Это не ошибка BLE-транспорта: его production реализации
ещё нет. Локальный лог: artifacts/windows-core-investigation/baseline.log.

| Исходный тест | Причина |
| --- | --- |
| DirectoryServiceTests.RenameWithSameSecretKeepsChannelAndBindingIdentity | Пул тестового read-only SQLite connection удерживает файл при удалении fixture |
| DirectoryServiceTests.ServiceStoresOnlyFingerprintAndRecognizesVerifiedHashtag | Такой же pooling; он маскировал ещё и конфликт sharing mode при raw-чтении открытого файла |
| DirectoryServiceTests.ChangedSecretCreatesPendingTransitionThenNewGeneration | Пул тестового SQLite connection |
| DirectoryServiceTests.SameSecretInTwoSlotsSharesChannelButDifferentSecretsWithSameNameDoNot | Пул тестового SQLite connection |
| CompanionSessionTests.ClientCreationFailureStillEndsSessionRecord | Пул вспомогательного соединения ReadAllSessionsAsync |
| SessionCommandGatewayTests.TcpConfirmedResetWithFailedReadbackOrLocalCommitReportsPartialSuccessWithoutRetry(failSql: true) | Пул соединения тестовой установки failure trigger |
| SessionCommandGatewayTests.TcpShutdownCancelsBlockedRouteReadBeforeClosingSession | Ожидаемый TCP abort после shutdown считался ошибкой эмулятора |
| HistoryPagingTests.CancellationInterruptsAnActiveSqliteSearch | Отмена могла вызвать interrupt до sqlite3_step; callback теста сигнализировал слишком рано, а рабочий reader терял эту отмену |

## Что изменено

### SQLite connections в тестах

Три helper sites Core (DirectoryServiceTests, CompanionSessionTests,
ContactRouteTcpEmulationTests) теперь явно используют Pooling=false через
SqliteConnectionStringBuilder. Аналогичный прямой connection в Desktop
HistoryClearIntegrationTests тоже исправлен. Dispose закрывает физический handle,
поэтому очистка fixture больше не зависит от финализации пула.

Рабочий SqliteDatabase.CreateConnection уже имел Pooling=false; глобальный
ClearAllPools, GC.Collect, игнорирование IOException и ослабление assertions не добавлялись.

В raw scan теста отсутствия channel secret File.ReadAllBytesAsync заменён чтением
через FileStream с FileShare.ReadWrite | FileShare.Delete. Тест читает файлы после
завершённых операций, но SQLite writer остаётся открыт. Default sharing mode
File.ReadAllBytesAsync несовместим с этим handle на Windows. Проверка byte sequence
в main DB/WAL сохранена. В первом прогоне после снятия pooling проявилась именно
эта ранее замаскированная ошибка: 389 tests / 1 failed. После изменения чтения — 0.

### Ожидаемое закрытие TCP-эмулятора

FakeCompanionServer теперь завершает receive loop при IOException с внутренней
SocketException только для ConnectionReset/ConnectionAborted/OperationAborted.
Во время shutdown peer может закрыть соединение с непрочитанным push/response и
получить reset вместо EOF. На воспроизведении Windows возвращал abort 10053.
Malformed frame/неверный marker и произвольные IOException по-прежнему не скрываются.
Рабочие TCP transport/runtime не менялись.

### Реальная гонка отмены чтения

В DatabaseReader был только callback sqlite3_interrupt. По контракту SQLite вызов,
когда ни один statement не исполняется, ничего не делает. Отмена между регистрацией
callback и началом ExecuteScalar оставляла последующий scan работающим и возвращала
успешный результат. Это дефект общего кода; он возможен и на macOS при соответствующем
порядке потоков, хотя прошлый прогон Mac мог не попасть в это окно.

Теперь reader сохраняет interrupt для уже активной операции, дополнительно
устанавливает sqlite3_progress_handler с проверкой token через 1000 VM instructions,
проверяет token перед и после action и снимает progress handler в finally.
Registration освобождается до закрытия connection. SQL остаётся на фоновом worker;
UI/RX, writer и schema не менялись.

Существующий cancellation test сигнализирует о старте через SQLite UDF изнутри
sqlite3_step, а не до вызова ExecuteScalar. Добавлены два regression tests:

- Отмена внутри read delegate перед первым SQL statement: запрос прерывается,
  не просматривает все 100000 строк; следующая операция reader работает.
- Отмена после последнего statement перед возвратом delegate: устаревший успешный
  результат не возвращается, exception сохраняет исходный CancellationToken.

### Почему Mac мог быть зелёным

Pool сохраняет физические handles независимо от ОС. Различия Windows/macOS в удалении
и открытии уже открытых файлов позволяют ошибкам тестового владения проявиться
по-разному. EOF/reset при TCP shutdown и порядок потоков в cancellation test тоже
не гарантированы одинаковыми на разных системах. Успешный Mac checkpoint не является
доказательством недостоверного отчёта; текущую macOS-проверку здесь не выполняли.

## Проверки

| Проверка | Фактический результат |
| --- | --- |
| `dotnet build MeshCoreSharp.sln -c Release --no-restore` | Успешно, 0 warnings/errors |
| Core suite после всех исправлений | **389/389**, 49,365 с; artifacts/windows-core-investigation/core-final.log |
| HistoryPagingTests отдельно, `-class MeshCoreMessenger.Core.Tests.HistoryPagingTests` | **11/11**, 3,575 с, включая оба новых regression tests |
| Desktop исходный history-clear test, `-method MeshCoreMessenger.Desktop.Tests.UiWorkspaceIntegrationTests.FailedHistoryClearPreservesVisibleSearchDraftAndOffersAnotherAttempt` | **1/1**, 0,584 с |
| Console sample, `dotnet run --project samples/MeshCoreSharp.Console -c Release --no-build -- --self-test` | SELF-TEST PASSED; только localhost |

Логи находятся в игнорируемой artifacts/windows-core-investigation; пользовательская
БД и реальные подключения не использовались. По запросу пользователя исправления
фиксируются отдельным коммитом после самостоятельного коммита BLE2.

## Отдельное ограничение Desktop

Дополнительный запуск класса UiWorkspaceIntegrationTests получил timeout в
PrivateSendUpdatesSameBubbleAfterSwitchingWorkspaceAndPreservesNewDraft для Unknown:
ожидание AwaitingAck в PrivateSendUiTests.cs:30 не завершилось. После FAIL общий
прогон класса не завершился; собственный процесс тестов остановлен через 1 мин 46 с.
Причина этого UI ожидания в рамках разбора восьми Core failures не установлена.
Отдельный исходный history-clear тест прошёл; **полная Desktop suite не объявляется
зелёной**. Лог: artifacts/windows-core-investigation/desktop-workspace.log.

## Источники

- [Microsoft: pooling включён по умолчанию](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/connection-strings#pooling).
- [SQLite: interrupt без активных statements — no-op](https://www.sqlite.org/c3ref/interrupt.html).
- [SQLite: progress handler и прерывание операции](https://www.sqlite.org/c3ref/progress_handler.html).
