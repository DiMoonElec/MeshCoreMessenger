# D7.1 — ручной повтор сообщения канала

Рабочая версия этого отчёта сохранена в `2a0919f`. В текущей рабочей копии по запросу пользователя включён [отдельный эксперимент с прежним timestamp](channel-repeat-dedup-experiment.md).

Реализовано 04.10.2026 по запросу пользователя. Только публичные/канальные чаты;
личный повтор и оставшаяся приёмка D7 пока не реализованы.

## Поведение

ПКМ по исходящему текстовому сообщению → «Повторить отправку» выполняет ровно одну
новую передачу без подтверждения. Пользователь отдельно отменил первоначальный
план warning dialog для каналов. Повтор доступен и для AcceptedByNode: ACK каналов
нет, получение участниками не подтверждается. Входящие сообщения, бинарные данные
и legacy-записи без сохранённой попытки не получают этот пункт.

Пункт виден у пригодного сообщения, но отключён offline, для другой собственной
ноды, канала без активных слотов, во время отправки и shutdown. Доступность повтора
не зависит от текста черновика и промежуточной readiness-проверки composer.
Окончательная проверка текущей session/generation и binding выполняется в Core.

Повтор использует сохранённый TransmissionText, без новой обработки и изменения
текста. Новый черновик не загружается, не отправляется и не очищается. Если имя
собственной ноды увеличило prefix, сохранённый текст заново проверяется против
текущего UTF-8 бюджета; обрезание/перезапись не выполняются.

`PrepareChannelRepeatAsync` транзакционно добавляет SendAttempt с новым номером
к прежнему MessageId/LocalSequence. Сообщение и первая session/binding остаются
неизменными; повторная попытка содержит текущую SessionId, lease захватывает текущий
канальный target с тем же NodeId/fingerprint. CAS номера последней попытки и общий
send single-flight предотвращают случайный двойной клик. Предыдущие attempts
не переписываются; история обновляет статус существующего пузырька без дубликата.
Команда повтора привязана отдельно от persisted status presentation, поэтому
очередное уведомление БД не стирает действие.

При нескольких слотах используется выбранный в composer, иначе прежний слот
сообщения, если он ещё связан с тем же fingerprint; иначе первый текущий слот
того же канала. Другое название/канал в старом слоте не становится адресатом повтора.
Live binding/session проверяются снова перед TX. Операция удерживает общий
ConversationOperationGuard с prepare до durable terminal write; clear-history
не может удалить активную попытку.

## Дедупликация MeshCore

Исследована текущая upstream-прошивка, 04.10.2026:

- [Mesh.cpp](https://github.com/meshcore-dev/MeshCore/blob/main/src/Mesh.cpp):
  GRP_TXT/GRP_DATA проходят `wasSeen` до расшифровки и уведомления UI.
- [SimpleMeshTables.h](https://github.com/meshcore-dev/MeshCore/blob/main/src/helpers/SimpleMeshTables.h):
  кольцевая таблица хешей ранее полученных пакетов.
- [Packet.cpp](https://github.com/meshcore-dev/MeshCore/blob/main/src/Packet.cpp):
  хеш включает тип payload и payload; меняющийся маршрут обычного сообщения
  не делает ту же передачу новой.
- [BaseChatMesh.cpp](https://github.com/meshcore-dev/MeshCore/blob/main/src/helpers/BaseChatMesh.cpp):
  групповое сообщение содержит timestamp, flags и `<sender>: <body>`, затем
  шифруется. Отдельного номера попытки канального сообщения нет.

Следовательно, сетевые копии одной передачи дедуплицируются. Тот же текст с новой
временной меткой создаёт другую передачу и может появиться вторым сообщением у
получателя. Гарантии удаления повторов по одному лишь тексту в этом firmware path
нет; стороннее приложение получателя может иметь собственное отображение/фильтры.

Core резервирует UInt32 wire timestamp для каждой канальной отправки. Для повтора
он больше всех сохранённых timestamps этого сообщения и учитывает текущие часы
и предыдущую канальную передачу текущего MessageService. Timestamp сохраняется
в Sending до API вызова; при потерянном ответе остаётся в Unknown. Следующий
явный повтор учитывает его даже после пересоздания service/reconnect/restart.
В публичной библиотеке добавлен overload SendChannelTextAsync с явным timestamp;
прежний overload сохраняет автоматический timestamp. Никаких изменений RX,
framing, тайм-аутов или автоматических передач. Миграция БД не потребовалась.

## Проверки

- Release solution build: 0 предупреждений, 0 ошибок.
- Library 99/99, Core 272/272, Desktop 315/315.
- TCP Companion emulator: initial send + explicit repeat для OK и ERROR;
  ровно две команды, прежний текст, разные wire timestamps, две attempts,
  текущий черновик сохранён; shutdown ничего не повторяет.
- Core: reconnect с новой session/allocator, исходный обработанный текст,
  CAS/двойной клик, сбой INSERT без TX, repeat/clear guard, смена слота того же
  fingerprint и отказ при другом канале, Unknown с durable timestamp без replay.
- Desktop: прежний пузырёк и draft, incoming без повторов, busy/double click/offline.
- Native `--repeat-channel-only`: Light/Dark × 420/960, настоящий ПКМ menu item,
  без confirmation window, одна запись чата и сохранённый draft.
- Только loopback/fake supervisor и временная SQLite. Аппаратная приёмка ожидается.

```sh
dotnet run --project tools/MeshCoreMessenger.ViewportAudit -c Release -- --repeat-channel-only
```

Снимки native audit: `$TMPDIR/meshcore-channel-repeat/`.

## Исправление обычного запуска Desktop

04.10.2026: после добавления повтора обычный `dotnet run --project
src/MeshCoreMessenger.Desktop` падал с `InvalidOperationException: The calling
thread cannot access this object because a different thread owns it` в
`Dispatcher.InitializeUIThreadDispatcher` при инициализации Avalonia.Native.

Причина: ChannelRepeatCoordinator ставил обновление доступности меню в
Avalonia dispatcher при публикации первоначальной offline-истории. LoadAsync
выполняется до запуска UI loop, а его продолжение после чтения SQLite может идти
на worker thread. Первый доступ к Dispatcher.UIThread привязывал его к этому
потоку, после чего основной поток не мог инициализировать платформу окна.
Это отдельная ошибка, не прежний сбой CVDisplayLink при погашенном дисплее.

Обработчик теперь обновляет привязки синхронно в контексте публикации, как
обработчики коллекции сообщений и presentation. После запуска окна публикации
приходят через существующий UI dispatcher; первоначальная загрузка не создаёт
его раньше времени. Регрессия с сохранённой историей и подключённым сервисом
отправки проверяет ноль обращений к dispatcher при offline LoadAsync. Обычный
Debug-запуск с сохранённой историей проверен успешно; Desktop Release 315/315.
