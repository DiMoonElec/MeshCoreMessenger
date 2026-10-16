# BLE8 — Проверка транспорта на выделенной ноде

[Stage BLE](stage-ble.md) · [Оглавление](../../MESSENGER_PLAN.md)

**Статус: probe реализован 16.10.2026; default stock read-only обмен проверен;
разрешённый drain с RX до166bytes проверен; аппаратная граница RX174–176 не достигнута.**

**Цель:** Проверить главный аппаратный риск до интеграции БД и UI.

**Зависимости:** BLE1–BLE7. Проверить результаты зависимостей перед началом.
Общие инварианты и решения — в [Stage BLE](stage-ble.md).

**Файлы/область:** Существующий `samples/MeshCoreSharp.Console`; отчёт `docs/testing/ble8-device-probe.md` создать при выполнении.
Новые имена/папки — предлагаемое место реализации; проверить актуальные исходники.

## Шаги

1. Уточнить модель, firmware version/build, Bluetooth-имя/выбранный endpoint; PIN вводится пользователем в Windows, не в отчёт. Телефон/другой Companion-клиент отключить от этой ноды.
2. Добавить явный диагностический режим sample с выбором BLE. Обычный запуск sample не должен сам искать/подключать ноду.
3. Перед железом выполнить fake tests BLE2–BLE7. Затем scan → сопряжение Windows при необходимости → connect/CCCD/MTU.
4. Через существующий MeshCoreClient выполнить Identify/DeviceInfo/DeviceTime, GetContacts/GetChannels. Не включать auto-drain, SET_TIME, advert или передачу сообщений в этом probe.
5. Зафиксировать длины реальных кадров, особенно длинный контакт/ответ, MTU, read-only reconnect и закрытие. Успешная короткая команда не доказывает работоспособность длинных кадров.

**Не входит:** Отправка радиосообщений, изменение конфигурации/часов, слив пользовательского backlog, включение BLE в Desktop.

## Проверки и завершение

**Тесты/проверки:** Проверить CLI parsing без hardware; на ноде — реальные notifications, полный ответ >20 bytes, multi-frame contacts, повторное открытие. Сопоставить public API с командами firmware.

**Готово:** Есть воспроизводимый отчёт модели/прошивки/Windows/драйвера/MTU и успешного read-only обмена; иначе явно описан blocker и закрытие этапа не заявляется.

**Ручная проверка:** Обязательна. Если нода/данные недоступны, аппаратная часть остаётся ожидающей; независимые BLE9–BLE11 могут выполняться отдельно.

- [x] Изменения в границах подэтапа выполнены.
- [ ] Обязательные проверки выполнены; фактические результаты записаны ниже.
- [x] Статус подэтапа, checklist Stage BLE и current-state обновлены.

## Результат выполнения

16.10.2026: по новому запросу пользователя добавлен отдельный opt-in
`--ble-drain-probe <device-id> <new-local-capture.jsonl>`. После явного разрешения
читать всю очередь получены60 сообщений (3 ЛС,57 канальных),61 SYNC_NEXT_MESSAGE
до NO_MORE_MESSAGES, без ошибок. Тестовые цифровые строки150bytes в ЛС и137bytes
тела канала сравнены целиком; кадры166/160bytes соответственно. Кириллица и концы
сохранены. Read-only reconnect после drain:237 контактов,40slots, clean cleanup.
Windows/portable sample self-tests прошли; capture остаётся локальным/ignored.
[Подробности](../../testing/ble-stock-firmware.md#аппаратный-приём-подготовленных-сообщений).
Кадров>=173 не было из-за доступных длин отправки телефона, поэтому recovery полного
TX value и RX174–176 аппаратно не подтверждены; этот остаточный пункт не закрывается
по успешному приёму166bytes. RF send/config writes не выполнялись. Старый запрет
drain в исходном scope ниже не относится к этой отдельно разрешённой проверке.

После первоначального probe пользователь явно потребовал совместимость без изменения
firmware и без theoretical max-frame gate. [Актуальная реализация/проверки](../../testing/ble-stock-firmware.md):
dynamic BLE TX budgets, отдельный полный TX read для пограничного RX, default native
probe при MTU176 без override. Library 179/179 Windows, 177/177 portable. TCP/Serial
не менялись. Full-size incoming cases проверены fake, аппаратная граница пока нет:
на исходном checkpoint пользовательский backlog не выгружался. Последующий явно
разрешённый drain описан выше; ниже сохранён первый checkpoint.

16.10.2026: добавлены явные `--ble-probe <device-id> [cycles=2] [--frame-limit N]`
и `--ble-probe-self-test` в существующий console sample. CLI parsing и command allowlist
проверены без hardware. В Windows device access выполняется через STA message-loop
host, pairing оставлено Windows. Библиотека получила read-only MaxPduSize и regression
fix: внешний open deadline сохраняет InsufficientMtu вместо общего DeviceUnreachable.

Пользователь сообщил Heltec V3 / 1.18, выполнил сопряжение сам. Native scan подтвердил
paired=true/NUS=true. Default connect остановлен до APP_START: actual MTU176 < required179.
Реальное DEVICE_INFO затем прочитано при явном diagnostic frame limit173:
`v1.18.0-07a833bb`, protocol 14. Два read-only цикла завершились успешно: по 238 контактов,
40 слотов каналов (4 заполнены), time/device info, disconnect и physical cleanup,
повторный Connect того же transport/client. Получены 476 CONTACT notifications по 148 bytes.
Ни drain, ни RF send, ни config/time writes не выполнялись.

Firmware commit `07a833bbbe7c0431f652e32a835db1205ca412ed` подтверждает
ESP32 setMTU(MAX_FRAME_SIZE), где MAX_FRAME_SIZE=176. Уменьшенный diagnostic budget 173
не подтверждает целостность полного firmware диапазона 174–176. Default политики,
long-write/reassembly и production DI не менялись. BLE8 checklist целиком не закрыт;
BLE12 production gate сохраняется до решения wire-контракта и аппаратной проверки.

Library harness: **166/166 Windows**, **164/164 portable**; обе solution Release
builds и locked restore успешны без warnings/errors, sample probe self-test зелёный
на обеих ветках. Native macOS не проверялся. BLE9 и далее не начаты.

Подробные команды, native metadata/длины, источники и варианты разрешения blocker:
[аппаратный отчёт BLE8](../../testing/ble8-device-probe.md).
