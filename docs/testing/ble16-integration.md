# BLE16 — автоматическая приёмка полного пути

16.10.2026. [Подэтап](../messenger/plan/ble16-integration.md).

## Реализация

Добавлены `BleIntegrationTests` и тестовый `BleCompanionEmulator` в Core tests.
Путь: fake GATT → настоящий `BleFrameTransport` → `MeshCoreClient` → стандартный
adapter/session → supervisor/directory/receive/ingest → временная SQLite.
`BleFrameTransport` — общий механизм, которому Windows `BleMeshCoreTransport`
делегирует работу; WinRT, системное сопряжение и физическое радио здесь не вызываются.
Для доступа к существующему internal seam добавлен friend Core test assembly,
публичная поверхность и поведение production-кода не менялись.

Эмулятор GATT переиспользует существующий `FakeCompanionServer` через loopback.
Stream framing добавляется/снимается только внутри тестового адаптера, в BLE
передаются исключительно целые logical values. MTU176, реальные BLE очереди,
лимиты, lifecycle и write semantics сохраняются. В проверке identity TCP использует
настоящий loopback TCP transport, Serial заменён на loopback в transport factory;
это проверка профиля/identity, а не аппаратная проверка Serial.

## Сценарии

- Identify, contacts/channels, interleaved MessagesWaiting, initial drain трёх
  сообщений. Каждый post-commit callback проверяет видимость строки независимому
  SQLite reader; нет второго event consumer или повторной записи одного события.
- Одна полная identity через TCP/Serial/BLE даёт одну историю; изменение только
  последнего байта ключа при том же BLE endpoint создаёт отдельную историю.
  Возврат исходного ключа возвращает прежний NodeId. Существующая дедупликация
  одинаковых входящих ЛС при повторном drain учитывается отдельно от каналов.
- Drop при GetContacts и SyncNextMessage завершает owning session; следующая
  явная попытка использует новый транспорт и нормально синхронизируется.
- Отмена открытия закрывает запись сессии и ресурсы эмулятора, команд нет.
- Переполнение RX — terminal failure, typed ReceiveQueueOverflow доставляется
  в BackgroundError; после teardown сессия завершена, Online не сохраняется.
- ЛС: Accepted не считается доставкой; поздний ACK даёт Delivered, дубликат ACK
  не создаёт попытку. Drop во время write сохраняет Unknown; автоматический
  reconnect создаёт свежую GATT-сессию и не повторяет передачу.
- Канал: Accepted/NotExpected сохраняется в БД; слишком большой UTF-8 draft
  отклоняется до GATT write и сохраняется целиком.
- Shutdown дожидается библиотечных событий, ingest и окончания session;
  временную БД закрывают после supervisor. Дополнительные детерминированные
  гонки shutdown/late callbacks покрываются существующими suites.

## Проверки

Логи — ignored `artifacts/ble16/`. Полные suites запускаются актуальными runners,
старые checkpoint counts не используются как результат этого этапа.

| Проверка | Результат |
| --- | --- |
| Новые BleIntegrationTests | 9/9 |
| Полный Core suite | 460/460 |
| Library Windows / portable | 179/179 / 177/177 |
| Полный Desktop Windows / portable, parallelMode none | 417/417 / 417/417 |
| Ранее проблемный private UI test отдельно | 3/3 Windows |
| Locked restore / Release solution build обеих веток | Успешно, 0 warnings/errors |

```powershell
dotnet run --project tests/MeshCoreMessenger.Core.Tests -c Release -- -class MeshCoreMessenger.Core.Tests.BleIntegrationTests
dotnet run --project tests/MeshCoreMessenger.Core.Tests -c Release --no-build
dotnet run --project tests/MeshCoreSharp.Tests -c Release -f net10.0-windows10.0.19041.0
dotnet run --project tests/MeshCoreSharp.Tests -c Release -p:McmWindowsDesktop=false
dotnet run --project tests/MeshCoreMessenger.Desktop.Tests -c Release -- -parallelMode none
dotnet restore MeshCoreSharp.sln --locked-mode -p:McmWindowsDesktop=false
dotnet build MeshCoreSharp.sln -c Release --no-restore -p:McmWindowsDesktop=false
dotnet run --project tests/MeshCoreMessenger.Desktop.Tests -c Release --no-build -p:McmWindowsDesktop=false -- -parallelMode none -longRunning 30 -reporter verbose
dotnet restore MeshCoreSharp.sln --locked-mode
dotnet build MeshCoreSharp.sln -c Release --no-restore
```

Windows restore возвращён последним. Полный Windows Desktop занял265с, portable253с;
длительные C10 и fake-data сценарии со100000 сообщениями прошли, не были исключены.
Старое ограничение о незавершённом Desktop suite относится к историческому прогону;
текущие полные последовательные прогоны зелёные. Нативный macOS запуск не выполнялся.

## Ограничения

При RX overflow причина в supervisor может быть общей «client is not connected»:
ошибка drain и исходная BLE-ошибка конкурируют; Offline может уже очистить reason.
Typed overflow в BackgroundError
не теряется, разрыв и teardown обязательны; тест проверяет обе гарантии.
Улучшение выбора первичной причины для UI остаётся отдельным исправлением.

Аппаратный BLE8 RX174–176, оставшаяся визуальная/клавиатурная приёмка BLE15,
Windows hardware BLE17 и native macOS этим этапом не подтверждаются.
Пользовательская БД и BLE-нода не использовались, RF передач не было.
