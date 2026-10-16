# REGION13 — Миграция каталога и назначений

[Stage REGION](stage-region.md) · [Общие контракты](region-design.md) · [Протокол](../../library/protocol/regions.md)

**Статус: выполнен 16.10.2026.** Задание подготовлено 16.10.2026.

**Цель:** Добавочная SQLite миграция region-данных.

**Зависимости:** REGION12. Проверить фактические результаты перед началом.
Рекомендуемый порядок последовательный: предыдущий шаг — [REGION12](region12-core-models.md).
Выполнять только этот подэтап; завершение не запускает следующий автоматически.

**Файлы/область:** Core/Persistence/Sqlite/DatabaseMigrator.cs; migration tests.
Новые типы/файлы здесь — предлагаемое место, не утверждение об их существовании.
Сначала сверить код, git status и версии схемы/API; готовые механизмы использовать повторно.

## Минимальное чтение

Общий порядок — [правила исполнения REGION](stage-region.md#порядок-работы-агента).
Из [архитектуры](region-design.md) и [протокола](../../library/protocol/regions.md)
прочитать только перечисленные ниже разделы и указанные соседние контракты:

region-design.md: «Хранение и локальные операции»; architecture/storage.md; существующие migration patterns.

## Шаги

1. Проверить текущий schema version; добавить Regions, ChannelRegionPreferences, NodeRegionSnapshots, RegionObservations с constraints/indexes.
2. Существующим каналам обеспечить InheritDefault без wire-команд; сохранить все прежние таблицы/историю/transport profiles.
3. Использовать backup/transaction/rollback conventions; лимит observations не должен удалять пользовательский catalogue.

**За границами шага:** Реальная пользовательская БД; попытки отправки (REGION18); repositories/UI.

## Проверки и завершение

**Обязательные проверки:** Пустая/старая БД; upgrade/reopen; FK/checks/indexes; cross-node reference rejected; fault rollback и foreign_key_check.

**Готово:** Новая схема безопасно открывает существующие БД на временных fixtures.

Если зависимость отсутствует или проверка не выполнена, записать точную причину и
оставить соответствующий пункт открытым. Для аппаратных/GUI проверок различать
автоматический результат, native просмотр и пользовательское подтверждение.
Не объявлять всю REGION завершённой по успеху одного слоя.

- [x] Изменения в границах подэтапа выполнены.
- [x] Обязательные проверки выполнены; команды и фактические результаты записаны.
- [x] Ограничения и оставшиеся проверки указаны.
- [x] Статус этого файла, checklist Stage REGION и current-state обновлены.

## Результат выполнения

Добавлена schema8 migration после7: Regions, ChannelRegionPreferences, NodeRegionSnapshots, RegionObservations и индексы; существующим Channels созданы InheritDefault/revision0. Catalogue identity уникальна по node/fingerprint; key16 ненулевой BLOB, fingerprint32. Composite FK region/channel/session с NodeId отвергают cross-node references. Referenced region delete RESTRICT; observation retention не затрагивает catalogue. Snapshot различает Unknown/KnownEmpty/configured pair, не хранит ложную current-session freshness.

Использованы существующие DatabaseWorker backup и transactional migration conventions. Для новой миграции добавлена явная pre-commit foreign_key_check без повторного восстановления profile indexes/triggers (оно требуется только BLE rebuild). Старые BLE migration assertions используют CurrentVersion при полном upgrade. SendAttemptScopes остаётся REGION18; пользовательская БД не открывалась.

Проверка: dotnet run --project tests/MeshCoreMessenger.Core.Tests -c Release --no-restore -- -noLogo -class MeshCoreMessenger.Core.Tests.RegionMigrationTests -class MeshCoreMessenger.Core.Tests.BleProfileMigrationTests -class MeshCoreMessenger.Core.Tests.LocalStorageTests -class MeshCoreMessenger.Core.Tests.RegionModelTests — 45/45. Шесть новых migration cases: schema7/empty/schema1 upgrades, same-node FK и checks/UTF8 boundary, injected history failure с rollback всех новых объектов/version/preferences, preexisting FK corruption, version7 backup и reopen без повторного backup. Все проверки на временных БД.

Следующий шаг — [REGION14](region14-catalog-store.md).
