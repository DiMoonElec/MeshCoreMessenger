# B7.5 — отделение transport profile от identity ноды (выполнено 28.09.2026)

[Оглавление](../../MESSENGER_PLAN.md) · [Маршрутизация чтения](../../README.md)

##### B7.5 — отделение transport profile от identity ноды (выполнено 28.09.2026)

После финального аудита пересмотрено ранее принятое в B1/B2/B6 решение хранить
ожидаемый ключ ноды в connection profile. Оно делало нормальную замену устройства на
том же Serial/TCP endpoint ошибкой `NeedsAttention` и требовало ручной перепривязки.

Новая модель:

```text
ConnectionProfile = способ подключения и reconnect policy
Node              = identity по полному SelfInfo.PublicKey
Session           = конкретная попытка с ProfileId и обнаруженным NodeId
```

Из `ConnectionProfile` и application API удалены `ExpectedNodePublicKey`, операция
его обновления, mismatch-result/exception и `UseConnectedNodeAsync`. После `APP_START`
`CompanionSession` всегда ищет или создаёт Node строго по полному 32-byte ключу,
связывает с ним текущую session и продолжает directory load/drain. Ни endpoint,
ни имя профиля, ни display name ноды в identity не участвуют. Supervisor/reconnect
каждую generation выполняет Identify заново; ошибки БД/ingest и несовместимые ответы
по-прежнему переходят в `NeedsAttention`.

SQLite schema не менялась. Nullable-колонка `ConnectionProfiles.ExpectedNodePublicKey`
остаётся legacy артефактом ранней схемы: текущая domain-модель и store её не читают,
не записывают и не используют для решений. Тест с ранее заполненной колонкой
подтверждает открытие и обновление такого профиля без миграции или потери legacy
значения. `MeshCoreSharp` и Companion protocol не изменялись.

Детерминированные fake-тесты подтверждают первую идентификацию, один NodeId для
одного ключа через Serial/TCP и при смене display name, разные NodeId для разных
ключей через один профиль и полный reconnect-сценарий `A -> B -> A`. Production-like
harness с настоящими supervisor/session/coordinator/ingestor и SQLite сохраняет
12 сообщений в двух историях: возврат A продолжает историю A, B остаётся отдельно,
а late events закрытых sessions не попадают в активную историю. Database failure
сохраняет `NeedsAttention` без retry; lifecycle, barriers, commit-before-UI и
recoverable shutdown не изменены. Физическая вторая нода для проверки не требуется.

Проверено: полный Release build без предупреждений; Core tests — 98/98, Desktop —
46/46, MeshCoreSharp regression suite — 92/92; `git diff --check` чист.
**Этап B завершён с уточнённой моделью identity 28.09.2026.**
