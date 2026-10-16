# REGION2 — Чтение и запись default ноды

[Stage REGION](stage-region.md) · [Общие контракты](region-design.md) · [Протокол](../../library/protocol/regions.md)

**Статус: выполнен 16.10.2026.** Задание подготовлено 16.10.2026.

**Цель:** Три публичных метода для постоянного default.

**Зависимости:** REGION1. Проверить фактические результаты перед началом.
Рекомендуемый порядок последовательный: предыдущий шаг — [REGION1](region1-models.md).
Выполнять только этот подэтап; завершение не запускает следующий автоматически.

**Файлы/область:** MeshCoreSharp/Protocol/Commands/, Parsing/, Packets/, Client/MeshCoreClient.cs.
Новые типы/файлы здесь — предлагаемое место, не утверждение об их существовании.
Сначала сверить код, git status и версии схемы/API; готовые механизмы использовать повторно.

## Минимальное чтение

Общий порядок — [правила исполнения REGION](stage-region.md#порядок-работы-агента).
Из [архитектуры](region-design.md) и [протокола](../../library/protocol/regions.md)
прочитать только перечисленные ниже разделы и указанные соседние контракты:

region-design.md: «Библиотека: безопасная отправка», «Совместимость»; protocol/regions.md: «Локальные команды Companion».

## Шаги

1. Добавить GET/SET/CLEAR default, parser 1C и matcher; формировать только полный SET либо явный однобайтовый CLEAR.
2. Сохранять фактическую пару name/key, поддерживать пустой ответ; malformed известного типа диагностировать локально, unknown packets сохранять.
3. Возвращать локальный результат команды без автоматического SET retry/readback; unsupported отличать от timeout. Постоянные методы позднее подключаются к общему gate в REGION4.

**За границами шага:** Temporary scope, отправка сообщений, startup probe и UI.

## Проверки и завершение

**Обязательные проверки:** Byte fixtures 1/48 bytes; NUL-padding; truncated SET отклонён до TX; чужой push между запросом/ответом; ERROR/unsupported; CancellationToken.

**Готово:** Default API работает через fake transport; приложение пока его автоматически не вызывает.

Если зависимость отсутствует или проверка не выполнена, записать точную причину и
оставить соответствующий пункт открытым. Для аппаратных/GUI проверок различать
автоматический результат, native просмотр и пользовательское подтверждение.
Не объявлять всю REGION завершённой по успеху одного слоя.

- [x] Изменения в границах подэтапа выполнены.
- [x] Обязательные проверки выполнены; команды и фактические результаты записаны.
- [x] Ограничения и оставшиеся проверки указаны.
- [x] Статус этого файла, checklist Stage REGION и current-state обновлены.

## Результат выполнения

Добавлены GetDefaultFloodScopeAsync, SetDefaultFloodScopeAsync и ClearDefaultFloodScopeAsync, encoder полного SET/явного CLEAR, typed packet и parser. Используются существующие single-flight dispatcher и subscribe-before-send. ERROR сохраняет MeshCoreErrorCode (UnsupportedCommand отличается от timeout); retries/readback/startup probe отсутствуют.

Проверка: dotnet run --project tests/MeshCoreSharp.Tests -c Release --no-restore — 185/185. Три новых cases покрывают 1/48-byte fixtures, malformed lengths/UTF-8/NUL, авторитетный custom key, независимое владение ключом, push, ошибки, отмену и timeout, валидацию до TX. Формат сверён с firmware 81dbae59; GET сохраняет имя до первого NUL, допускает хвост от старого имени: firmware SET делает strcpy без очистки остатка. Аппаратная проверка целевой сборки остаётся REGION32.

Следующий шаг — [REGION3](region3-scope-lifecycle.md).
