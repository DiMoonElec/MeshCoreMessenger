# D10 — contact mutations и readback

[Stage D](stage-d.md) · **Статус: не начат.**

**Цель:** явное добавление/редактирование/удаление без потери истории.

**Scope:** Core service → AddOrUpdateContactAsync/RemoveContactAsync через D4,
full key/type validation, проверенный readback после команды. Partial refresh не
заменяет snapshot пустым. Remove сохраняет contact/history с PresentOnNode=false;
identity неизменна, name только display. Prefix ambiguity запрещает send.
Подключить D8, обновлять UI post-commit; ERROR/timeout/readback/storage failures
различают отказ и неопределённость.

**Не входит:** backup restore на ноду, bulk import, secret/radio settings,
автоматическая переклассификация historical unknown.
**Зависимости:** D4, D8–D9; DirectoryService/IDirectoryStore/library contact API.

**Тесты:** add/edit/remove/readback; absent candidate/collision;
OK + readback mismatch/БД отказ; partial refresh; гонка send/remove;
late completion/node switch; история/draft остаются; snapshot/reconnect/shutdown.

**Готово:** только явные изменения, directory commit до UI; unknown result виден,
не повторяется скрыто. **Ручная проверка: обязательна** — fake все исходы и история
после remove. Hardware — только согласованный тестовый контакт с восстановлением.
