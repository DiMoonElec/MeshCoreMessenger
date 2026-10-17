# REGION — физическая проверка discovery/regions через CatCore BLE

17.10.2026. Внеплановая проверка ограниченного backend-сценария по прямому запросу
пользователя; GUI отложен. Это частичная проверка REGION33, а не завершение REGION32/33.

## Условия и границы

- Windows BLE → Nordic UART → настоящий MeshCoreClient, без stream wrapper.
- CatCore: `RnD CatCore🐈`, Heltec V3, DEVICE_INFO `v1.18.0-07a833bb`, protocol14.
- GET default: KnownEmpty. Прочитан 241 контакт. Сообщения/backlog не выгружались;
  AutoReceiveMessages=false. Default, каналы, текстовые сообщения, reboot и admin CLI
  не изменялись/не отправлялись.
- Три раунда zero-hop discovery по 8 секунд, между ними пауза15–18 секунд.
  Full32 ключи объединяются между раундами; prefix8 не принимается за identity.
- До пяти full-key zero-hop соседей по SNR; до трёх последовательных region requests
  на каждого. Один terminal wait15 секунд, перед следующим запросом пауза15–18 секунд;
  после первого корректного ответа (в том числе пустого) повторов нет.
- В изученном firmware reference `81dbae59` discovery limiter4/120s, anonymous limiter4/180s.
  Удалённые сборки неизвестны. Паузы снижают частоту запросов, но не гарантируют
  свободный эфир или отсутствие уже исчерпанного другим клиентом лимита.

## Результат

Время: 10:25–10:29 MSK. Первый поиск дал1/0/0 ответов по раундам; найден полный
ключ `0979EFCFCF20…`, `SMK Dzho🌵🤠🌵`, zero-hop, RX SNR10.75dB,
inbound11.5dB, RSSI−60dBm. Дополнительный поиск дал0/0/0: эти пустые окна не
интерпретируются как исчезновение репитера или отсутствие регионов.

Первый region request получил ответ, но выявил ошибку парсера: тот удалял только
один завершающий NUL. Маршрут FF был восстановлен даже после исключения.
Исходники Utils::encrypt/decrypt и Companion forwarding показывают AES zero-padding.
Парсер теперь снимает весь нулевой хвост и по-прежнему отвергает NUL внутри имени.

Финальный запрос к ранее обнаруженному соседу (без нового discovery) успешен с
первой попытки, ответ за2.22s: **`*`, `ru`, `ru-ros`, `ru-ros-rnd`**.
Frame34bytes, tag `6AD2B401`, clock1792222164; после CSV два нулевых байта.
Точный frame добавлен в library regression, также проверяется padding0..15bytes.
Первый опрос дал protocol error, а не radio timeout; успешный повтор не доказывает
работу RF retries на этом репитере. Timeout→retry→success доказан только self-test.

Оба временных изменения пути FF→zero-hop завершились проверенным восстановлением
FF. Конфликтов нет. GET default до/после: KnownEmpty. BLE cleanup завершён.
Число контактов241→242 между сессиями; выбранный репитер уже существовал, probe
не создавал контакт явной командой. Причину фонового изменения количества не выводим.

Локальные ignored captures: `artifacts/region-hardware-2026-10-17.log`,
`artifacts/region-hardware-2026-10-17-binary.log`,
`artifacts/region-hardware-2026-10-17-query.log`.
Он содержит UTC timestamps, выбранные публичные identities, сигналы, tags и имена
регионов; BLE OS endpoint и содержимое backlog не публикуются в git.

## Воспроизведение

```powershell
dotnet run --project samples/MeshCoreSharp.Console -c Release --no-restore -- --region-probe-self-test
dotnet run --project samples/MeshCoreSharp.Console -c Release --no-build -- --list-ble 8
dotnet run --project samples/MeshCoreSharp.Console -c Release --no-build -- --ble-region-probe '<DeviceInformation.Id CatCore из scan>'
# Повтор только для явно выбранного, ранее обнаруженного zero-hop соседа:
dotnet run --project samples/MeshCoreSharp.Console -c Release --no-build -- --ble-region-probe '<DeviceInformation.Id>' '<full-public-key-hex>'
```

Последняя команда — opt-in RF/route probe, не read-only device probe. У известного
соседа с non-zero-hop маршрутом она сохраняет полный contact, ставит zero-hop,
проверяет readback, затем условно восстанавливает исходную конфигурацию после
terminal ответа/таймаутов. При новом advert/PATH_UPDATED или отличающемся contact
восстановление пропускается с CONFLICT. При неопределённом SET/proof слепого отката
нет. Firmware может создать неизвестный contact; такой контакт сохраняется.
Поиск не обходит всю mesh и не проверяет доступность скрытых репитеров.

Retries относятся только к явно запущенному diagnostic probe. Production discovery,
отправки и библиотека не получили автоматических retry/replay.

## Автоматические проверки и среда запуска

- Новый self-test через loopback: первый remote timeout→вторая попытка успешна,
  implicit contact сохранён, полный route restore/readback; отдельный PATH_UPDATED
  conflict сохраняет новый маршрут. PASS.
- Library regression после исправления padding:228/228; Release solution build0 warnings/errors.
- В sandbox Windows FindAllAsync изначально выбросил FileNotFoundException; повторный
  scan вне sandbox успешно нашёл CatCore. Console `--list-ble` теперь перехватывает
  неожиданные исключения с type/HRESULT/message вместо необработанного CLR crash-окна.

Не проверены channel/private RF и принимающая сторона, firmware-generated PATH/ACK
при temporary scope, reboot persistence, TCP/Serial hardware transport parity,
crash посередине route mutation, полнота удалённого каталога и другие местности.
