# REGION22 — Проверка целей discovery

[Stage REGION](stage-region.md) · [Общие контракты](region-design.md) · [Протокол](../../library/protocol/regions.md)

**Статус: выполнен 17.10.2026.** Задание подготовлено 16.10.2026.

**Цель:** Read-only классификация репитеров и побочных эффектов запроса.

**Зависимости:** REGION21. Проверить фактические результаты перед началом.
Рекомендуемый порядок последовательный: предыдущий шаг — [REGION21](region21-application-budget.md).
Выполнять только этот подэтап; завершение не запускает следующий автоматически.

**Файлы/область:** Core/Application/ новый discovery preflight, DirectoryService/ContactRouteService read API.
Новые типы/файлы здесь — предлагаемое место, не утверждение об их существовании.
Сначала сверить код, git status и версии схемы/API; готовые механизмы использовать повторно.

## Минимальное чтение

Общий порядок — [правила исполнения REGION](stage-region.md#порядок-работы-агента).
Из [архитектуры](region-design.md) и [протокола](../../library/protocol/regions.md)
прочитать только перечисленные ниже разделы и указанные соседние контракты:

region-design.md: «Discovery в приложении»; protocol/regions.md: раздел discovery; текущая contact route ownership.

## Шаги

1. Объединять full-key соседей и известные repeater contacts, читать актуальный путь через session lease; prefix недостаточен.
2. Вернуть план: ReadyDirect, RequiresContactCreation, RequiresTemporaryZeroHop, NoUsableReplyPath; path reversal помечать предположением, а подтверждённый асимметричный return path принимать явно.
3. Описать какие mutations потребуются и что будет восстановлено; сам preflight ничего не пишет на ноду, result привязан к generation/revision.

**За границами шага:** Исполнение route mutations, агрегация регионов и GUI согласование.

## Проверки и завершение

**Обязательные проверки:** Known zero/multi-hop; неизвестный сосед; известный flood contact; stale plan; full/prefix collisions; empty contacts; zero mutation calls.

**Готово:** Discovery больше не маскируется под read-only, если нужен addContact или смена пути.

Если зависимость отсутствует или проверка не выполнена, записать точную причину и
оставить соответствующий пункт открытым. Для аппаратных/GUI проверок различать
автоматический результат, native просмотр и пользовательское подтверждение.
Не объявлять всю REGION завершённой по успеху одного слоя.

- [x] Изменения в границах подэтапа выполнены.
- [x] Обязательные проверки выполнены; команды и фактические результаты записаны.
- [x] Ограничения и оставшиеся проверки указаны.
- [x] Статус этого файла, checklist Stage REGION и current-state обновлены.

## Результат выполнения

IRegionDiscoveryPreflight объединяет full32-key соседей и известные repeater contacts, затем читает каждый известный контакт через lease той же session generation. Prefix8 остаётся диагностикой, не целью. Plans ограничены maximumTargets и явно сообщают число пропущенных целей. Preflight выполняет только GET contacts/contact; catalogue/route/device mutations и Mesh region requests отсутствуют.

RegionDiscoveryPlan различает ReadyDirect, RequiresContactCreation (implicit firmware addContact и необходимый refresh), RequiresTemporaryZeroHop (mutation/readback + conditional restore/readback) и NoUsableReplyPath. Multi-hop reverse выполняется по целым hashes и имеет AssumedReverse evidence; явно подтверждённый асимметричный ReplyPath принимается независимо. Snapshot полного контакта скопирован и имеет revision fingerprint для обнаружения нового route/advert state; owner содержит NodeId/SessionId/generation.

Проверки: пять focused cases — known zero/multi-hop, неизвестный сосед, известный flood contact, prefix collisions, explicit asymmetric reply path, свежий readback вместо cached route, ограничение целей, пустой каталог, stale owner, неверный полный ключ и поздний отменённый readback; zero mutation/TX counters. Общие Core/Release/Desktop результаты записаны в current-state. Никаких аппаратных запросов не выполнялось; исполнение маршрутов остаётся REGION23.

Следующий шаг — [REGION23](region23-discovery-route.md).
