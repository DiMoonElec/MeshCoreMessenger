# REGION17 — Изменение default ноды с readback

[Stage REGION](stage-region.md) · [Общие контракты](region-design.md) · [Протокол](../../library/protocol/regions.md)

**Статус: выполнен 17.10.2026.** Задание подготовлено 16.10.2026.

**Цель:** Online RegionService workflow через session lease.

**Зависимости:** REGION16. Проверить фактические результаты перед началом.
Рекомендуемый порядок последовательный: предыдущий шаг — [REGION16](region16-session-read.md).
Выполнять только этот подэтап; завершение не запускает следующий автоматически.

**Файлы/область:** RegionService, SessionCommandGateway/Lease, default store; fake session tests.
Новые типы/файлы здесь — предлагаемое место, не утверждение об их существовании.
Сначала сверить код, git status и версии схемы/API; готовые механизмы использовать повторно.

## Минимальное чтение

Общий порядок — [правила исполнения REGION](stage-region.md#порядок-работы-агента).
Из [архитектуры](region-design.md) и [протокола](../../library/protocol/regions.md)
прочитать только перечисленные ниже разделы и указанные соседние контракты:

region-design.md: «Хранение и локальные операции», «Ошибки, отмена, восстановление»; REGION16 adapter.

## Шаги

1. Реализовать Set/Clear default только для активного owner; после принятой команды выполнить GET и сохранить наблюдённое состояние.
2. Сериализовать изменение со scope resolution/send; публиковать новый revision только после проверки/readback/commit.
3. Различать SET rejected, accepted/readback failed, mismatch и storage failure; не повторять автоматически и не откатывать ноду вслепую.

**За границами шага:** GUI; проверка flash persistence без reboot на железе.

## Проверки и завершение

**Обязательные проверки:** SET/CLEAR success; mismatch; отмена/timeout между фазами; stale session; write failure; одновременно запрошенная отправка использует определённый snapshot.

**Готово:** Сервис сообщает фактический результат настройки и сохраняет неопределённость при сбое.

Если зависимость отсутствует или проверка не выполнена, записать точную причину и
оставить соответствующий пункт открытым. Для аппаратных/GUI проверок различать
автоматический результат, native просмотр и пользовательское подтверждение.
Не объявлять всю REGION завершённой по успеху одного слоя.

- [x] Изменения в границах подэтапа выполнены.
- [x] Обязательные проверки выполнены; команды и фактические результаты записаны.
- [x] Ограничения и оставшиеся проверки указаны.
- [x] Статус этого файла, checklist Stage REGION и current-state обновлены.

## Результат выполнения

SetNodeDefaultAsync/ClearNodeDefaultAsync требуют точный активный SessionId/generation через lease. Общий session gate сериализует SET → GET → durable snapshot → публикацию и text SEND. Во время возможного изменения текущее состояние Unknown; только после commit появляется новый session-local Revision. Новые leases захватывают обновлённые данные, ранее выданные owner snapshots неизменяемы. Постоянный кеш остаётся offline observation.

Результаты различают Applied, Rejected, CommandUncertain, ReadbackFailed, ReadbackMismatch, PersistenceFailed и SessionClosed. Mismatch сохраняет фактический readback; неопределённость после возможной передачи сохраняется без повторного SET/rollback. Ошибка БД приостанавливает admission существующим durable tracker. GET timeout/cancellation после TX закрывает library command admission до reconnect.

Повторная проверка REGION16 обнаружила race: фоновый drain может увидеть loss of readiness до lifecycle callback. Identified session теперь переводит такой InvalidOperationException в transport failure, сохраняя NeedsAttention для ошибок здоровой сессии. Есть детерминированный regression test.

Проверки: RegionDefaultServiceTests (8 случаев) — SET/CLEAR, mismatch, отказ, отмена/timeout, неизвестный исход SET, сериализация, stale owner/shutdown, SQLite write failure; RegionSessionTests (6). Library 226/226, Desktop bootstrap 6/6; Release solution build без предупреждений/ошибок. Полный Core результат указан в current-state. Аппаратные настройки и flash persistence не проверялись (REGION32–33).

Следующий шаг — [REGION18](region18-attempt-storage.md).
