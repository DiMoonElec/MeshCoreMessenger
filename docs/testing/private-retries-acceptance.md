# P8 — итоговая приёмка повторов ЛС

05.10.2026. Автоматическая и native UI части P8 завершены. Аппаратная часть
остаётся открытой: пользователь подтвердил отправку ближней ноде и работу GUI,
но прохождение до дальней ноды пока не установлено. Этот отчёт закрывает
проверяемую без аппаратных нод часть блока P1–P8, а не весь Stage D.

База приёмки: коммит `d71a332` (ручная отправка ЛС как нового сообщения) и
добавленные в P8 проверки очереди/ACK/native menu. Production код и схема БД
в P8 не изменены. Использованы временные SQLite, loopback TCP Companion emulator
и реальные Avalonia controls; пользовательская БД и эфир не используются.

## Автоматическая матрица

Все перечисленные наборы повторно выполнены полными Release-прогонами.

| Сценарий | Проверки / результат |
| --- | --- |
| Flood 3 TX; ACK на 1/2/3; ранний ACK во время поздней попытки | `PrivateDeliveryCoordinatorTests`: один пузырёк, остановка цикла, независимые ACK tags. Добавлен явный ACK самой третьей попытки |
| Known 3 + reset + flood 2 | `PrivateFallbackTcpEmulationTests`: direct и multi-hop; T1/0,1,2 → T2/0,1, T2 > T1; reset перед четвёртой передачей |
| Оба RetryMode | API/store/coordinator/fallback: сохранение timestamp с attempt либо новый timestamp/attempt=0; без attempt=4 |
| Очередь и изменение маршрута | FIFO одного контакта, параллельное ожидание других контактов и публичная отправка. Добавлены четыре сочетания known↔flood × RetryMode: budget берётся при старте job, выбранный при enqueue режим сохраняется |
| Поздний ACK и collisions | `PrivateAcknowledgementStoreTests`, TCP private/fallback: ACK первой фазы после начала второй, Unconfirmed → Delivered, reverse/duplicate/foreign tags, честная неоднозначность |
| Ошибки маршрута и гонки | Fallback/readback TCP: неожиданный MSG_SENT mode, изменение пути, reset/readback failure, ACK во время reset, отмена; без повторного радио или mutation ради SQLite retry |
| Persistence/lifecycle/restart | Store/coordinator/session gateway: durable prepare, сбои записи до/после TX, остановка активной/ожидающей работы, restart/reconnect без автоматического replay, timestamp floor после restart/очистки/перевода часов |
| UTF-8 и черновик | Валидатор/encoder/service/UI: 160/161 байт, emoji, immutable capture и сохранение нового draft после клика |
| Успешные доставки | ACK commit/read API/readback/card: идемпотентный успех, время ПК, configured/readback provenance, все ambiguous candidates, пагинация и защита от устаревшего результата |
| Очистка | `ContactDeliveryHistoryClearTests` и clear integration: удаление сообщений/ACK/аналитики только по выбранным NodeId/full ContactPublicKey, rollback, отсутствие восстановления удалённой истории поздним событием; активный цикл защищён |
| Входящие повторы | Legacy/V3 TCP drain + store/UI: одинаковые sender/timestamp/text дают один пузырёк/unread с event aliases; T2 с тем же текстом — отдельное сообщение. Это моделирует возможность двух сообщений после fallback при потерянном ACK, а не поведение реального радио |
| Ручная отправка ЛС | `PrivateResendStoreTests`, `PrivateResendTcpTests`, `PrivateResendUiTests`: новый цикл/timestamp/attempt=0, актуальный маршрут, исходник и draft сохранены, отдельные ACK, двойной клик/смена workspace, атомарный отказ для уже Delivered |
| Публичные повторы | Прежние store/TCP/UI проверки обоих действий: «Повторить доставку» и «Отправить как новое» |
| Библиотечный ACK tracker | Полный library runner: capacity, ранние/поздние/переставленные ACK и конфликтующие tags; single-flight команд не удерживается на всё ожидание ACK |

Основные отчёты: [coordinator](private-delivery-coordinator.md),
[fallback](private-fallback-retries.md), [ACK commit](private-delivery-ack-commit.md),
[storage/recovery](private-delivery-storage.md), [route history](private-delivery-history-reader.md),
[incoming dedup](private-incoming-retries.md), [manual resend](private-manual-resend.md),
[scoped history clear](contact-delivery-history-clear.md).

## Native UI

macOS preflight прошёл: `main=1, active=1, CoreGraphics=0, CVDisplayLink=0`.
Состояние дисплея и блокировки не менялось; защита диагностического startup
описана [отдельно](viewport-audit-startup.md).

Запуск: `dotnet run --project tools/MeshCoreMessenger.ViewportAudit -c Release --no-build -- <режим>`.

| Режим | Результат |
| --- | --- |
| `--private-send-only` | Light/Dark × 420/960: Enter — одна отправка, Shift+Enter/IME — ноль; Unconfirmed → реальный пункт «Отправить еще раз» → новый pending bubble без диалога; draft сохранён; поздний ACK исходника меняет только его статус и скрывает повтор |
| `--modal-only` | Public/Private × Light/Dark × 420/960: карточки, bounds/resize/focus, Escape и крестик |
| `--history-clear-only` | Public/Private × Light/Dark × 420/960: cancel сохраняет историю, confirm очищает, draft остаётся |
| `--route-reset-only` | Light/Dark × 420/960: один вызов reset, post-commit flood в UI, история и draft сохранены |
| `--repeat-channel-only` | Light/Dark × 420/960: оба пункта реального меню, прежний/новый пузырёк и сохранённый draft |
| `--switch-only` | Light/Dark: A send → B send → A send без входящего события для разблокировки адресата |

Native отправки используют UI test service: протокол/full delivery cycle
проверяется отдельно production-сервисами через loopback TCP. Нативный сценарий
ЛС дополнен в P8 для проверки настоящего MenuItem и независимых статусов двух
пузырьков. Screenshot Dark/420 просмотрен: оба статуса и неизменённый draft видны.
Снимки сохраняются в `$TMPDIR/meshcore-d6-private-send/`, `meshcore-modal-cards/`,
`meshcore-history-clear/`, `meshcore-route-reset/`, `meshcore-channel-repeat/`.

Release solution и native tool build: **0 warnings, 0 errors**.
Library: **105/105**, Core: **384/384**, Desktop: **328/328**;
ошибок, пропусков и невыполненных тестов нет.

## Оставшаяся аппаратная приёмка

Эмулятор проверяет Companion frames и заданные ACK/маршруты. Он не доказывает
радиопрохождение, crypto packet hash или дедупликацию штатной прошивки/приложения.
Ранее [аппаратный эксперимент публичных повторов](channel-repeat-hardware-dedup.md)
подтвердил канальное поведение; переносить его результат на ЛС нельзя.

Пользователю для завершения аппаратной части P8:

1. Проверить дальнюю ноду с известным multi-hop маршрутом: при отсутствии ACK
   три known попытки, один сброс, две flood попытки и итог 5/5.
2. Сопоставить radio payload/hash: первые три T1/attempt 0…2, последние две
   T2/attempt 0…1; проверить изменения hash и реальные ACK tags.
3. Проверить случай «текст принят, ACK потерян»: повторы T1 и fallback T2,
   число сообщений в штатном приёмнике; возможный дубликат T2 допустим.
4. Получить поздний ACK T1 во время/после flood: прекращение последующих попыток,
   Delivered того же пузырька, корректные candidates/time/route в карточке контакта.
5. Отправить два ЛС подряд, сверить ACK/историю маршрутов каждого сообщения;
   проверить отсутствие автоматических TX после disconnect/reconnect.

Дополнительных пояснений для автоматической части не потребовалось. До этих
аппаратных результатов P8 имеет статус «автоматическая/native часть завершена;
аппаратная приёмка ожидается».
