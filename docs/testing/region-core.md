# REGION25 — сквозная приёмка backend

17.10.2026. Проверки используют временные SQLite, loopback Companion и реальные
MeshCoreClient/encoders/router/session/services. BLE-сценарий использует GATT seam
эмулятора; это не аппаратная проверка CatCore.

## Новые сквозные сценарии

`tests/MeshCoreMessenger.Core.Tests/RegionCoreAcceptanceTests.cs`:

- Каталог A/B → SET/GET default A → policy канала B → SEND в B → ЛС в A → discovery
  трёх full-key репитеров. Во время remote запроса принимается и сохраняется ЛС.
  Пустой ответ отличается от отсутствия ответа; два источника Area объединяются;
  `$secret` остаётся именем без ключа. Shutdown сохраняет историю и закрывает admission.
- Explicit B → private A → ForceUnscoped → InheritDefault A → точный повтор B →
  отправка как нового A. Проверяются wire keys, immutable attempt scopes и прежний
  channel binding/generation. Канальная identity не зависит от региона.
- Flood contact → SET zero-hop/readback → anonymous request → restore/readback.
  Новый PATH_UPDATED запрещает восстановление старой конфигурации. Malformed CSV
  и terminal timeout сохраняют успешные результаты другого репитера без replay.
- Неизвестный сосед при TableFull даёт отдельный terminal outcome; ранее полученные
  observations остаются, контакт не удаляется и запрос автоматически не повторяется.
- BLE → offline → TCP для одного полного ключа сохраняет catalogue/preferences,
  но читает изменённый внешним клиентом default. Другая нода в том же BLE endpoint
  получает собственный пустой каталог/default. Подключения не отправляют сообщения
  и не записывают scope устройства.

Эмулятор проверяет реальные CMD9/54/55/57/63/64 и отвечает CONTACT/MSG_SENT/
CONTROL_DATA/BINARY_RESPONSE через stream framing. Имитация firmware не является
независимым доказательством совместимости с конкретным firmware build.

## Остальная обязательная матрица

| Контракт | Регрессии |
| --- | --- |
| Две ноды/профили, offline, внешний default | новый RegionWireReconnectAcrossProfiles…; RegionSessionTests; RegionStoreTests |
| Storage pause/retry и отсутствие replay | StorageFailureStopsRemainingQueriesAndRetryDoesNotReplayMeshRequests; DurableFailurePreservesAcceptedThenCleanupOrderAndPreventsNewClientUntilRetry |
| Scope uncertainty/поздний OK | library RegionChannelTests/RegionDefaultTests; RegionDefaultServiceTests |
| Late callbacks, replacement, teardown barriers | StaleSessionRejectsLateNamesAndNeverWritesToReplacementOwner; IncomingEventsDuringDisconnectCommitBeforeReplacement; CompleteWorkflowAndAckObserverRemainOwnedWhileTeardownWaits |
| История scope и точные повторы | новый scope sequence; RegionAttemptStoreTests; RegionChannelServiceTests; RegionPrivateServiceTests (реальные TCP retries/fallback) |
| Restore conflict/uncertain preparation | новый wire conflict; RegionDiscoveryRouteTests |
| Bounded storage, миграция и backup | RegionDiscoveryStorageTests; RegionMigrationTests; RegionStoreTests |

## Команды и результаты

- `dotnet build MeshCoreSharp.sln -c Release --no-restore -p:UsedAvaloniaProducts= -v quiet`: 0 warnings/errors.
- Полный Core Release `-- -noLogo -parallelMode none`: **558/558** после исправления fixture; до него 557/558 (BLE overflow race).
- Целевой Core `-method '*ReceiveOverflowIsTerminal*' -method '*RegionWire*'`: **8/8**, включая семь новых wire acceptance cases.
- `dotnet run --project tests/MeshCoreSharp.Tests -c Release --no-restore`: **228/228**.
- Desktop Release bootstrap `-class '*Bootstrap*'`: **6/6**; полный Desktop **418/418** на checkpoint REGION22, production UI этим этапом не менялся.

Повторный полный прогон выявил race существующего BLE overflow fixture: поток
`MessagesWaiting` будил SYNC, который мог первым заметить закрытый transport;
ручной Disconnect затем опережал доставку исходной RX-диагностики. Тест теперь
подаёт inert unknown pushes и ожидает конкретный ReceiveQueueOverflow до ручного
Disconnect. Проверка причины и terminal lifecycle сохранена; production код не менялся.

## Готовые GUI contracts и ограничения

`IRegionService` предоставляет локальные catalogue/preferences и explicit online
default changes; `IRegionDiscoveryService` — bounded operation с progress/cancel,
consent, source names и per-target outcomes. `IMessageService.GetTextBudgetAsync`
возвращает recipient-aware budget/freshness; attempt scope читается из
`IOutgoingMessageStore.GetAttemptScopeAsync`. UI получает post-commit уведомления,
не подписывается на RX вместо Core и не определяет region входящего сообщения.

REGION26 требует пользовательского выбора компоновки. Hardware RF, persistent
default после reboot и scope автоматически отправленных firmware PATH/ACK остаются
REGION32/33. Эмуляция проверяет владение и wire последовательность приложения,
но не приём сообщения удалённой нодой и не firmware CAS (которого нет).
