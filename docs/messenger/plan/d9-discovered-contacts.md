# D9 — обнаруженные контакты (read-only)

[Stage D](stage-d.md) · **Статус: не начат.**

**Цель:** заполнить D8 реальными typed NEW_ADVERT без авто-добавления на ноду.

**Scope:** AdvertisementReceived routing внутри единственного ReceiveCoordinator
consumer; отдельная bounded immutable projection кандидатов, node/session scoped,
dedupe по полному key, свежий advert и typed пригодность для Add. Не приписывать
неполный advert выдуманному ключу. Lifecycle/generation projection не конкурирует
за Events. Advert не доказывает Online. Предпочесть bounded session-memory candidates;
новая SQLite таблица только при обосновании, offline discovery persistence не требуется.

**Не входит:** wire Add/Remove, auto-add всех нод, active scanning,
переклассификация исторических unknown по новому справочнику.
**Зависимости:** D4, D8; AdvertisementInfo/session events.

**Тесты:** repeated/updated advert, full key, Chat/Service, incomplete/raw fallback;
bounded memory; late A после B, reset/reconnect; RX/barriers; zero mutation calls;
candidates не меняют историю/Contacts.PresentOnNode.

**Готово:** Add UI получает безопасных кандидатов, command остаётся disabled до D10.
**Ручная проверка: обязательна** — injected adverts, update/empty/offline/длинные
имена; реальное радио необязательно.
