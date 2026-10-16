# REGION14 — Хранилище и read projections регионов

[Stage REGION](stage-region.md) · [Общие контракты](region-design.md) · [Протокол](../../library/protocol/regions.md)

**Статус: выполнен 16.10.2026.** Задание подготовлено 16.10.2026.

**Цель:** Реализация repository contracts на SQLite.

**Зависимости:** REGION13. Проверить фактические результаты перед началом.
Рекомендуемый порядок последовательный: предыдущий шаг — [REGION13](region13-catalog-schema.md).
Выполнять только этот подэтап; завершение не запускает следующий автоматически.

**Файлы/область:** Core/Persistence/Sqlite/, LocalStorage.cs; новые store tests.
Новые типы/файлы здесь — предлагаемое место, не утверждение об их существовании.
Сначала сверить код, git status и версии схемы/API; готовые механизмы использовать повторно.

## Минимальное чтение

Общий порядок — [правила исполнения REGION](stage-region.md#порядок-работы-агента).
Из [архитектуры](region-design.md) и [протокола](../../library/protocol/regions.md)
прочитать только перечисленные ниже разделы и указанные соседние контракты:

region-design.md: «Хранение и локальные операции»; REGION12 contracts и REGION13 schema.

## Шаги

1. Реализовать catalogue CRUD/archive, preferences с revision и compare/update, default cache, observations upsert.
2. Все mutations проводить через DatabaseWorker; read projections отделить от UI, ограничить запросы и retention observations.
3. Сохранять provenance нескольких репитеров, не удалять назначение при исчезновении имени из discovery; исключить cross-node read/write.

**За границами шага:** RegionService workflow, подключение ноды и UI.

## Проверки и завершение

**Обязательные проверки:** CRUD/reopen; idempotent upsert; duplicate keys и labels; stale revision; два профиля/одна нода; два NodeId; archive с references; writer failure.

**Готово:** Хранение работает без сетевых операций и публикует только committed данные.

Если зависимость отсутствует или проверка не выполнена, записать точную причину и
оставить соответствующий пункт открытым. Для аппаратных/GUI проверок различать
автоматический результат, native просмотр и пользовательское подтверждение.
Не объявлять всю REGION завершённой по успеху одного слоя.

- [x] Изменения в границах подэтапа выполнены.
- [x] Обязательные проверки выполнены; команды и фактические результаты записаны.
- [x] Ограничения и оставшиеся проверки указаны.
- [x] Статус этого файла, checklist Stage REGION и current-state обновлены.

## Результат выполнения

Реализован SqliteRegionStore, доступный через LocalStorage.Regions: catalogue add/read/label/archive/delete, key dedupe внутри NodeId, assignment projections и CAS revision, offline default cache с защитой от более старого observed time, observation upsert с first/last evidence и bounded retention. Каждая mutation проходит DatabaseWorker и transaction; Changed публикуется после commit, callback exceptions не превращают сохранённую запись в failed result. Query/catalogue/observation bounds4096, новых wire API нет.

Referenced catalogue delete отклоняется; archive сохраняет назначения и не позволяет новые назначения архивному региону. Same-node channel/region/session checks исключают cross-node data. Reader missing preference возвращает InheritDefault/revision0 только для существующего канала этой ноды. Readback cache всегда offline; actual custom pair не пересчитывается. Несколько repeater источников одного имени сохраняются раздельно; retention удаляет только observations, а отсутствие имени в новом ответе не удаляет assignments/catalogue.

Проверка: dotnet run --project tests/MeshCoreMessenger.Core.Tests -c Release --no-restore -- -noLogo -class MeshCoreMessenger.Core.Tests.RegionStoreTests -class MeshCoreMessenger.Core.Tests.RegionModelTests -class MeshCoreMessenger.Core.Tests.RegionMigrationTests —16/16. Пять новых store cases: CRUD/reopen, duplicate keys/labels, CAS conflict, archive/reference/delete и node isolation, custom default freshness/older-write rejection, multi-source/retention, injected writer failure и batch rollback без публикации. Existing identity fixture покрывает два профиля одной ноды. В test callback tracked reads дожидаются завершения до удаления временной БД.

Следующий шаг — [REGION15](region15-catalog-service.md).
