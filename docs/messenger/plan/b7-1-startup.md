# B7.1 — composition root и неблокирующий startup (выполнено 28.09.2026)

[Оглавление](../../MESSENGER_PLAN.md) · [Маршрутизация чтения](../../README.md)

##### B7.1 — composition root и неблокирующий startup (выполнено 28.09.2026)

- Зарегистрировать attempt factory, supervisor, retry policy и platform power events;
  `ReceiveCoordinator` не регистрировать singleton, поскольку он принадлежит одной
  attempt и создаётся её factory.
- Добавить единственного Desktop lifecycle owner с идемпотентными startup/shutdown.
  Локальную историю загрузить до сетевой политики, а `StartAutoConnect` вызвать после
  показа окна. Ожидание сети/Serial не должно блокировать UI startup.
- При обычном завершении сначала остановить UI-сценарии, затем supervisor, после него
  асинхронно закрыть DI services, SQLite и instance lock. Отменяемое закрытие при
  ошибке commit будет добавлено в B7.3.

Тесты B7.1: полный DI graph; один lifecycle/supervisor; ровно один startup/shutdown;
новый coordinator на attempt; отсутствие transport attempt при выключенном
`AutoConnect`; заблокированный connect не задерживает возврат startup-команды.

Реализовано: Desktop DI регистрирует production attempt factory, failure classifier,
delay/jitter, platform power events и единственный supervisor. Лишняя singleton-
регистрация `ReceiveCoordinator` удалена: coordinator по-прежнему создаётся factory
отдельно для каждой attempt. `DesktopConnectionLifecycle` объединяет повторные
startup/shutdown вызовы и при гонке дожидается принятого startup перед shutdown.

`Program` сначала открывает lock/SQLite и загружает локальную историю. Lifecycle
создаётся только после этого, а `StartAutoConnect` вызывается обработчиком первого
`Window.Opened`; создание transport/client остаётся фоновой работой supervisor и не
задерживает UI. При обычном выходе останавливаются ViewModels, затем supervisor,
после чего DI services закрываются через `DisposeAsync`, далее SQLite и instance lock.
Отмена уже начавшегося закрытия при persistence failure намеренно остаётся B7.3.

Проверено: полный Release build без предупреждений; Core tests — 94/94, Desktop —
26/26, MeshCoreSharp regression suite — 92/92. Новые fake-тесты подтверждают один
startup/shutdown, их порядок, возврат startup при заблокированном создании connection
attempt и отсутствие attempt при `AutoConnect=false`; bootstrap test проверяет полный
DI graph и отсутствие singleton coordinator. Физическая нода не использовалась.
