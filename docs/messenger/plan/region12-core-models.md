# REGION12 — Прикладные модели и контракты

[Stage REGION](stage-region.md) · [Общие контракты](region-design.md) · [Протокол](../../library/protocol/regions.md)

**Статус: выполнен 16.10.2026.** Задание подготовлено 16.10.2026.

**Цель:** Node-scoped DTO и store/service contracts без реализации SQL.

**Зависимости:** REGION11. Проверить фактические результаты перед началом.
Рекомендуемый порядок последовательный: предыдущий шаг — [REGION11](region11-library-acceptance.md).
Выполнять только этот подэтап; завершение не запускает следующий автоматически.

**Файлы/область:** MeshCoreMessenger.Core/Domain/, Persistence/ и Application/.
Новые типы/файлы здесь — предлагаемое место, не утверждение об их существовании.
Сначала сверить код, git status и версии схемы/API; готовые механизмы использовать повторно.

## Минимальное чтение

Общий порядок — [правила исполнения REGION](stage-region.md#порядок-работы-агента).
Из [архитектуры](region-design.md) и [протокола](../../library/protocol/regions.md)
прочитать только перечисленные ниже разделы и указанные соседние контракты:

region-design.md: «Владение состоянием и модель», «Хранение и локальные операции»; architecture/identity.md.

## Шаги

1. Определить region catalogue, channel preference/revision, default snapshot/freshness, observations и immutable send scope.
2. Привязать данные к LocalNodeId и channel-secret identity; отделить label от protocol name, Unknown от KnownEmpty и discovery от сохранённого каталога.
3. Описать небольшие repository/read contracts, CHECK-инварианты и правила удаления; сформировать fixtures для двух нод и двух профилей одной ноды.

**За границами шага:** SQL, session wiring, новые сервисы отправки и UI.

## Проверки и завершение

**Обязательные проверки:** Валидация mode/reference; defensively copied keys; stable channel identity; legacy/unknown representation; DTO не раскрывают секреты через ToString.

**Готово:** Модели достаточно определены для двух следующих storage-шагов.

Если зависимость отсутствует или проверка не выполнена, записать точную причину и
оставить соответствующий пункт открытым. Для аппаратных/GUI проверок различать
автоматический результат, native просмотр и пользовательское подтверждение.
Не объявлять всю REGION завершённой по успеху одного слоя.

- [x] Изменения в границах подэтапа выполнены.
- [x] Обязательные проверки выполнены; команды и фактические результаты записаны.
- [x] Ограничения и оставшиеся проверки указаны.
- [x] Статус этого файла, checklist Stage REGION и current-state обновлены.

## Результат выполнения

Добавлены RegionCatalogEntry, ChannelRegionPreference, NodeRegionSnapshot, RegionObservation и ResolvedRegionScope, небольшие IRegionReader/IRegionStore contracts. Catalogue использует валидированную immutable library RegionDefinition; label отделён от protocol name/key. NodeId — существующий полный-key NodeRecord ID; ChannelId — существующий secret-fingerprint channel ID, без scope/slot/profile. Observations не являются catalogue entries; private discovery имя не порождает ключ. Default явно различает Unknown/KnownEmpty и current-session/offline freshness. Immutable attempt capture сохраняет key16 или LegacyUnknown/Unknown, без catalogue FK; replay разрешён только для known scope. ToString не выводит key material.

CHECK-контракт для REGION13: mode IN(0,1,2); RegionId IS NOT NULL только при ExplicitRegion(1); revision>=0; region/channel ссылки должны принадлежать тому же NodeId. Catalogue key16 ненулевой, fingerprint32 уникален внутри node. Node snapshot support/state/session/date согласованы; KnownEmpty не подменяет Unknown. Observation repeater key32, exact name<=30UTF8 bytes, first<=last, uniqueness по node/full-key/name. Delete referenced region отклоняется; archive не изменяет preference/default/history. Missing preference проецируется InheritDefault/revision0; CAS записи увеличивает revision. Все mutations идут через DatabaseWorker с post-commit publication, без device queue.

Проверка: dotnet run --project tests/MeshCoreMessenger.Core.Tests -c Release --no-restore -- -noLogo -class MeshCoreMessenger.Core.Tests.RegionModelTests — 5/5. Проверены mode/reference/revision, defensive keys, redacted DTO, known/legacy/offline, prefix rejection и два профиля одной ноды/две ноды с переименованием и переносом канала в существующем directory store. Core Release build:0 warnings/errors. Новые SQL/migrations/services/UI отсутствуют.

Следующий шаг — [REGION13](region13-catalog-schema.md).
