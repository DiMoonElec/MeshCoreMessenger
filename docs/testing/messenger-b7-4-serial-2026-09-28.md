# Аппаратная приёмка MeshCoreMessenger B7.4 — 28.09.2026

Приёмка выполнена на macOS 26.6.2 arm64 (build 25G83), .NET SDK
10.0.301, через `/dev/cu.usbserial-0001`, `115200/8N1`, `DTR=true`, `RTS=true`.
Нода: `RnD CatCore🐈`, Heltec V3, firmware `v1.17.1-d929643`, protocol 13,
build `14-Aug-2026`.

Все аппаратные сценарии были ограничены локальными Companion-командами. Не
выполнялись отправка сообщений, advert, изменение контактов/каналов, установка
времени или другие mutations. Секреты каналов и тексты принятой истории не
выводились.

## Выполненные проверки

1. Guarded no-reset probe разрешал только `APP_START` и `GET_STATS(core)`.
   Два последовательных открытия дали uptime 401 и 404 секунды. Значение выросло,
   поэтому reset при close/open не наблюдался.
2. Guarded read-only probe разрешал `APP_START`, device/time/battery, contacts,
   channels и stats. Radio TX counters до и после остались равны:
   `sent=1`, `flood=0`, `direct=1`. Получены 121 контакт и 4 именованных канала;
   channel secrets не печатались.
3. Production Messenger pipeline запускался на временной SQLite через настоящие
   `ConnectionSupervisor`, `ConnectionAttempt`, `CompanionSession`,
   `ReceiveCoordinator`, `MessageIngestor` и Serial transport. Выполнены два
   последовательных запуска с повторным открытием той же БД.
4. Оба запуска прошли точную последовательность
   `Connecting -> Identifying -> Synchronizing -> Online -> Disconnecting -> Offline`.
   Полный NodeId остался тем же; созданы и завершены две session-записи.
5. Первый initial drain закоммитил 3 накопленных сообщения в 2 conversations;
   после второго запуска осталось 3 сообщения, то есть уже удалённая из очереди
   история не дублировалась. После каждого shutdown `PendingMessageCount=0`.
6. После завершения `lsof /dev/cu.usbserial-0001` не обнаружил владельца порта.
   Постоянная БД пользователя не изменялась; временная БД удалена после проверки.
7. Для unplug/replug настоящий production pipeline был доведён до устойчивого
   `Online` в generation 1, после чего USB был физически отключён. Потеря транспорта
   перевела supervisor в `RetryWaiting`; старую session закрыли до следующей attempt.
8. Пока нода отсутствовала, supervisor последовательно выполнил generation 2–5 с
   задержками 2, 4, 8 и 15 секунд после первой задержки 1 секунда. Перекрывающихся
   attempts и `NeedsAttention` не наблюдалось.
9. После физического подключения generation 6 прошла
   `Connecting -> Identifying -> Synchronizing -> Online`. NodeId совпал с исходным:
   `e010ac28-e41d-4984-b491-bd7b14b70e42`; создана новая session. Затем штатный
   shutdown прошёл через `Disconnecting -> Offline`, обе наблюдавшиеся session
   получили durable end, `PendingMessageCount=0`, процесс завершился с `PASS`.
10. Unplug/replug harness использовал только production lifecycle, directory read и
    initial drain. Он не имел сценариев отправки сообщений, advert или изменения
    конфигурации. После проверки порт снова освобождён, временные данные удалены.

## Ограничение окружения

- TCP к этой же ноде не проверялся: доступный TCP endpoint не указан.
  Это был условный пункт «по возможности»; Serial-приёмка, включая физический
  unplug/replug, завершена.
