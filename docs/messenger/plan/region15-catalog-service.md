# REGION15 — Локальные операции каталога и канала

[Stage REGION](stage-region.md) · [Общие контракты](region-design.md) · [Протокол](../../library/protocol/regions.md)

**Статус: выполнен 16.10.2026.** Задание подготовлено 16.10.2026.

**Цель:** Offline RegionService для управления каталогом и предпочтениями.

**Зависимости:** REGION14. Проверить фактические результаты перед началом.
Рекомендуемый порядок последовательный: предыдущий шаг — [REGION14](region14-catalog-store.md).
Выполнять только этот подэтап; завершение не запускает следующий автоматически.

**Файлы/область:** Core/Application/RegionService (новый), Domain/; service tests.
Новые типы/файлы здесь — предлагаемое место, не утверждение об их существовании.
Сначала сверить код, git status и версии схемы/API; готовые механизмы использовать повторно.

## Минимальное чтение

Общий порядок — [правила исполнения REGION](stage-region.md#порядок-работы-агента).
Из [архитектуры](region-design.md) и [протокола](../../library/protocol/regions.md)
прочитать только перечисленные ниже разделы и указанные соседние контракты:

region-design.md: «Владение состоянием и модель», «Хранение и локальные операции».

## Шаги

1. Добавить create/import custom key, rename label, archive и назначение одного из трёх режимов каналу.
2. При изменении protocol name создать новую definition; использованный регион удалять только после явной замены ссылок либо отказать со списком назначений.
3. Обеспечить revision и post-commit invalidation; одинаковый secret в двух слотах сохраняет общее предпочтение, смена secret получает InheritDefault.

**За границами шага:** Default mutation ноды, discovery и внешняя синхронизация каталогов.

## Проверки и завершение

**Обязательные проверки:** Offline changes; смена label/name/secret; duplicate slots; referenced delete; storage failure без ложного success; никаких fake client вызовов.

**Готово:** Каталог и назначения полностью работают как локальные сценарии.

Если зависимость отсутствует или проверка не выполнена, записать точную причину и
оставить соответствующий пункт открытым. Для аппаратных/GUI проверок различать
автоматический результат, native просмотр и пользовательское подтверждение.
Не объявлять всю REGION завершённой по успеху одного слоя.

- [x] Изменения в границах подэтапа выполнены.
- [x] Обязательные проверки выполнены; команды и фактические результаты записаны.
- [x] Ограничения и оставшиеся проверки указаны.
- [x] Статус этого файла, checklist Stage REGION и current-state обновлены.

## Результат выполнения

Добавлены IRegionService/RegionService: offline public create, custom key import, label rename, archive/unarchive, policy assignment с expectedRevision, delete и создание replacement definition. Сервис зависит только от IRegionStore и TimeProvider, не имеет client/session/wire dependency. Changed делегируется post-commit store events. Protocol replacement с новым ключом создаёт другую entry и не меняет назначения/историю; public name/#name — одна identity. Переименование custom protocol name при том же ключе отвергается: для той же identity доступна label rename, а новая definition требует нового ключа.

Удаление используемого региона возвращает RegionInUseException с immutable списком ChannelIds, полученным атомарно внутри writer transaction. Пользователь должен явно заменить назначения; implicit widening нет. Archive сохраняет используемую policy. Same secret в двух слотах использует существующий ChannelId и общее предпочтение; другой secret получает InheritDefault/revision0.

Проверка: dotnet run --project tests/MeshCoreMessenger.Core.Tests -c Release --no-restore -- -noLogo -class MeshCoreMessenger.Core.Tests.RegionServiceTests -class MeshCoreMessenger.Core.Tests.RegionStoreTests -class MeshCoreMessenger.Core.Tests.RegionModelTests -class MeshCoreMessenger.Core.Tests.RegionMigrationTests —20/20. Четыре новых service cases: create/label/protocol/key transitions; duplicate slots и смена secret; referenced delete с явной заменой; injected storage failure без success event. Все операции offline на временной БД, Companion dependency отсутствует. Default/discovery/UI не добавлялись.

Следующий шаг — [REGION16](region16-session-read.md).
