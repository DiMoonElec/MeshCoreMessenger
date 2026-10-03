# D8 — UI управления контактами

[Stage D](stage-d.md) · **Статус: не начат.**

**Цель:** согласовать discovery/add/edit/remove без изменения ноды.

**Scope:** в приватных чатах список обнаруженных Chat-кандидатов/форма добавления;
форма изменения разрешённых полей контакта, full identity для проверки,
подтверждение «Удалить с ноды; история останется». Общие части применять к карточке
устройства при допустимом type; composer там не добавлять. Busy/error/readback-required/
offline состояния и collision warning в preview.

**Не входит:** advert consumption/mutations, авто-добавление, эфирный поиск нод,
редактор всех raw полей, local history clear. Production actions disabled.
**Зависимости:** D1 preview, D7; UI3, contact projection/identity.

**Тесты:** full key не заменяется именем; edit/cancel без client/store writes;
Chat/non-Chat routing; disabled/offline; длинные имена/collision/errors preview;
Light/Dark resources/typed bindings.

**Готово:** формы готовы к D9/D10, remove-from-node отделён от clear-local.
**Ручная проверка: обязательна** — narrow/wide, keyboard, ключи/имена,
add/edit/cancel/confirmation на fake presentation data.
