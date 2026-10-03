# Ручной сброс маршрута ЛС — анализ 04.10.2026

Статус: предложение реализации по запросу пользователя; функция пока не реализована.
D7 и остальные незавершённые подэтапы этим анализом не начинаются.

## Поведение и протокол

В меню приватной переписки нужен пункт «Сбросить маршрут». Нажатие меняет маршрут
контакта на подключённой собственной ноде. Оно не отправляет и не повторяет сообщение,
не меняет историю и черновик. Следующая отправка использует маршрут, актуальный на ноде.

В [Companion firmware MyMesh.cpp](https://github.com/meshcore-dev/MeshCore/blob/main/examples/companion_radio/MyMesh.cpp)
`RESET_PATH (13)` принимает полный 32-байтовый public key: `0D | key[32]`.
У существующего контакта устанавливает `out_path_len = OUT_PATH_UNKNOWN`, назначает
отложенное сохранение контактов и возвращает OK; отсутствующий ключ даёт NOT_FOUND.
Старые байты `out_path` не обнуляются, `lastmod` не обновляется; данный обработчик
не отправляет уведомление PATH_UPDATED. Поэтому incremental sync по lastmod здесь
недостаточен, а просмотр только hex пути вводит в заблуждение.

В [BaseChatMesh.cpp](https://github.com/meshcore-dev/MeshCore/blob/main/src/helpers/BaseChatMesh.cpp)
`sendMessage` при неизвестном маршруте использует flood, иначе direct.
Маршрут может снова обновиться при приёме return-path (в том числе с вложенным ACK).
Это ручной сброс сохранённого пути, а не постоянный режим принудительного flood.
Свежий входящий return-path может изменить путь даже между сбросом и отправкой.
Источники проверены на текущем upstream main; перед аппаратной приёмкой сверить
с фактической версией прошивки пользовательской ноды.

## Что уже есть и чего не хватает

- В библиотеке есть `CommandType.ResetPath`, но нет encoder и публичного метода.
  Добавить `MeshCoreClient.ResetPathAsync(fullPublicKey, CancellationToken)` по образцу
  RemoveContactAsync: проверка длины, копирование аргумента, обычный dispatcher,
  subscribe-before-send, ожидание OK/ERROR и существующие timeout/cancellation.
  Все protocol детали остаются в одном MeshCoreSharp.dll.
- В Core добавить метод в ICompanionClient/production adapter и SessionCommandLease.
  Принимать только ContactCommandTarget, полный ключ копируется и фиксируется вместе
  с владельцем NodeId/SessionId/Generation. UI не получает MeshCoreClient/raw commands.
- Новый сценарий Core (например, ContactRouteService) выполняет проверку адресата,
  ResetPath, readback и локальное сохранение в одном owned lease.RunAsync workflow.
  Допускается только Online session той же ноды и известный личный контакт,
  присутствующий в справочнике ноды; unknown prefix-only адресат недопустим.
  Смена выбранного чата/ноды не перенаправляет начатую операцию на новый контакт.
- Library Contact уже содержит OutPathLength; DirectoryContactSnapshot, Contacts
  и read projections его теряют. Добавить nullable encoded descriptor в SQLite
  и протянуть через snapshot/projections. Для старых записей NULL означает «не знаем»,
  а не 0/FF; реальный readback заполнит поле. FF — неизвестный путь/flood;
  0 — известный direct путь без ретрансляторов. Для остальных значений учитывать
  число хешей и размер хеша, а не трактовать весь байт как число хопов.
- IDirectoryStore пока имеет только полное ApplySnapshot(contacts, channels).
  Нужен отдельный путь обновления контакта с проверкой node/session, не затрагивающий
  каналов и их binding generations. Для минимального readback можно взять
  существующий GetContactsAsync без since и выбрать полный ключ. Альтернатива:
  отдельно добавить GetContactByKeyAsync (command 30); сейчас готового API тоже нет.
  Не применять неполный список к полной snapshot-операции: это пометит другие
  контакты отсутствующими. Не вызывать общую синхронизацию каналов ради сброса пути.
- После OK, но при ошибке readback/SQLite, показать «Маршрут сброшен, не удалось
  обновить локальные данные», а не сообщать, что сброс не произошёл. После timeout
  результат может быть неизвестен. Автоповтора/replay после reconnect нет.
  UI принимает только post-commit обновление с проверкой текущего контекста.

## Конкуренция

Один dispatcher сериализует immediate-команды, но этого недостаточно для полного
сценария: pending ACK/return-path предыдущего ЛС может вновь установить путь.
Использовать общий ConversationOperationGuard и обобщить BeginClear в admission
эксклюзивной операции, сохранив существующий контракт очистки. Сброс недоступен,
пока этому адресату выполняется send/ACK workflow или очистка; новая отправка
этому адресату не допускается до завершения reset/readback/local commit.
Другие переписки продолжают работать. Поздние unsolicited return-path всё равно
могут изменить маршрут: обещать неизменный flood до следующего сообщения нельзя.

## Разные меню Public/Private

Сейчас ConversationView.axaml содержит один жёстко заданный MenuFlyout для обоих
workspace; маршрутные пункты — заглушки. Уже существуют два независимых workspace,
создаваемые ChatWorkspacesViewModel: Channels и Personal.

Предложение: при инициализации каждого ChatWorkspaceViewModel сформировать
ConversationMenuViewModel с постоянным набором item models и привязать команды
сценариев. Common actions включают очистку истории; Private добавляет сброс маршрута,
Public не содержит маршрутных пунктов. Размещение и тематическая иконка остаются общими.
ConversationView только отображает коллекцию и показывает диалог при необходимости.

Состав меню и handlers создаются один раз при инициализации движка. CanExecute,
busy и пояснения обновляются при смене selection/session. Цель команды захватывается
при нажатии, а не при создании меню: иначе при смене чата останется старый адресат.
Асинхронные availability результаты проверяют revision, как существующий HistoryClear.
Запросы пользователя не требуют добавлять подтверждение сброса маршрута.

## Предлагаемый порядок и проверки

1. Типизированная библиотечная команда + тесты wire layout/полного ключа,
   OK/NOT_FOUND/timeout/cancellation и push между request/response.
2. Descriptor migration, точечный contact readback/store, owned Core service/guard.
3. Menu composition при инициализации Public/Private и приватная reset command;
   доступность offline/unknown/pending ACK, busy/error/post-commit metadata.
4. Loopback Companion: известный direct → reset → unknown/flood → обычная отправка;
   отслеживать команды и node contact state. Сама reset command должна отправить
   ноль сообщений. Проверить последующее route learning.
5. Два контакта/две ноды, selection switch/reconnect, pending ACK, readback/DB failure,
   старый snapshot, сохранность history/draft, отсутствие пункта у Public.
   Для obsolete snapshots определить порядок/ревизии, чтобы они не перезаписали
   более свежий route readback. PATH_UPDATED пока enum/raw fallback: typed обработчик
   и автоматическое readback можно добавить отдельно; не выполнять команды в RX callback.
6. Аппаратную приёмку выполняет пользователь.
