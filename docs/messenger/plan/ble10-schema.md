# BLE10 — Миграция SQLite для третьего транспорта

[Stage BLE](stage-ble.md) · [Оглавление](../../MESSENGER_PLAN.md)

**Статус: реализован 16.10.2026.**

**Цель:** Расширить CHECK и столбцы, сохранив профили и ссылки Sessions.

**Зависимости:** BLE9. Проверить результаты зависимостей перед началом.
Общие инварианты и решения — в [Stage BLE](stage-ble.md).

**Файлы/область:** `Core/Persistence/Sqlite/DatabaseMigrator.cs`, существующие migration tests.
Новые имена/папки — предлагаемое место реализации; проверить актуальные исходники.

## Шаги

1. Проверить фактическую последнюю версию схемы перед правкой; добавить следующую миграцию, не редактировать уже применённую.
2. Добавить BleDeviceId/BleDeviceName и разрешить Transport=2 с отдельной веткой CHECK. Существующая таблица ограничивает Transport значениями (0,1), одного ADD COLUMN недостаточно.
3. При rebuild ConnectionProfiles использовать проверенный SQLite migration pattern. Учесть Sessions foreign key, indexes/triggers и текущую транзакцию: PRAGMA foreign_keys внутри транзакции не переключается.
4. Скопировать все текущие столбцы/ID/flags/timestamps без пересоздания профилей; не восстанавливать удалённую старую expected-key привязку.
5. Использовать существующий backup/migration error handling. Проверить rollback при failure и foreign_key_check перед успешным завершением.

**Не входит:** Реальная пользовательская БД, перестройка других таблиц, UI/store API.

## Проверки и завершение

**Тесты/проверки:** Пустая БД; старая БД с TCP/Serial, Settings и связанными Sessions/Nodes; upgrade/reopen; invalid mixed rows; interrupted migration/rollback; повторный startup без повторной миграции.

**Готово:** Миграция атомарна, старые данные и ссылки сохранены, допускаются только корректные transport rows.

**Ручная проверка:** Не требуется; только временные БД/копии fixtures.

- [x] Изменения в границах подэтапа выполнены.
- [x] Обязательные проверки выполнены; фактические результаты записаны ниже.
- [x] Статус подэтапа, checklist Stage BLE и current-state обновлены.

## Результат выполнения

16.10.2026: добавлена миграция **v7**, прежние миграции не изменены. ConnectionProfiles
перестраивается с nullable BleDeviceId/BleDeviceName и CHECK для трёх транспортов.
BLE требует непустой ID и отсутствия TCP/Serial fields; прежние transports запрещают
оба BLE fields. Все старые значения копируются явно, включая игнорируемую legacy
колонку ExpectedNodePublicKey: runtime-привязка к ключу не возвращается.

Использован [рекомендованный SQLite порядок перестройки](https://www.sqlite.org/lang_altertable.html#making_other_kinds_of_table_schema_changes):
foreign_keys отключается до транзакции, создаётся новая таблица, копируются данные,
старая удаляется, новая переименовывается. Индексы/триггеры таблицы восстанавливаются;
foreign_key_check выполняется до commit. FK setting возвращается и после rollback.
Sessions/Nodes/Settings не перестраиваются. Существующий backup-before-migration
и обработка ошибок сохранены. В текущей схеме нет views, зависящих от профилей.

Проверки на временных БД: targeted `BleProfileMigrationTests`, `LocalStorageTests`,
`ConnectionProfileTests` — **47/47**. Покрыты новая БД, v6→v7, старые поля/flags/timestamps,
связанные Sessions/Nodes, legacy key bytes, CHECK, indexes/triggers, fault после rebuild,
rollback schema/data/history/user_version, foreign_key_check failure, backup v6 и reopen.
Миграция после rollback повторно проходит; повторный startup не создаёт новую миграцию/backup.
Обновлён старый incoming retry fixture: при имитации v5 удаляется вся более новая
история миграций, ожидается CurrentVersion. Первый полный прогон выявил только это
устаревшее ожидание (410/411); после актуализации полный Core suite — **411/411**.
Пользовательская БД не открывалась. Store mapping и UI остаются для следующих этапов.
