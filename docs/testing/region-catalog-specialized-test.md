# Специализированный пример: поиск репитеров и каталог регионов

Пример вызывается **только по явному запросу пользователя**. Он не подключён
к общему runner, CI, startup приложения или обычному `--self-test`.
Создание примера не означает разрешение агенту запускать его на железе в следующих задачах.

```powershell
# При необходимости определить BLE endpoint:
dotnet run --project samples/MeshCoreSharp.Console -c Release --no-build -- --list-ble 8
# Только по явному запросу пользователя; JSON-файл должен быть новым:
dotnet run --project samples/MeshCoreSharp.Console -c Release --no-build -- --ble-region-catalog-test '<DeviceInformation.Id CatCore>' 'artifacts/regions-result.json'
```

`RegionCatalogTest.cs` использует public library API через `RegionMeshProbe`:

1. До трёх discovery-попыток с окном8s и паузами15–18s. После первого раунда,
   содержащего full32-key zero-hop репитеров, дальнейший поиск прекращается.
2. Обрабатываются **все** репитеры этого раунда, без ограничения старого probe в5целей.
   Для каждого до трёх последовательных запросов: ожидание ответа15s, пауза15–18s.
   Первый корректный ответ, включая пустой список, завершает опрос этого репитера.
   Timeout и malformed допускают повтор; явный command rejection фиксируется отдельно.
3. Успешные имена объединяются с сохранением полного ключа каждого источника.
   Полученные данные выводятся в консоль и записываются в UTF-8 JSON.

Это поиск соседей, не обход всей mesh. Prefix8 не подменяет identity. Ответ может
быть неполным; пустое окно/timeout не доказывают отсутствие репитера или регионов.
Имена `$…` не содержат приватного ключа. Firmware rate limits и эфир могут мешать
ответам даже при повторных попытках.

Для известного non-zero-hop контакта выполняется временная подготовка zero-hop и
условное восстановление полного contact/readback. Новые advert/PATH_UPDATED или
изменения contact запрещают затирать новый маршрут. Неизвестный сосед может быть
добавлен firmware; такой контакт не удаляется. Default, каналы, сообщения и backlog
не изменяются. При потере соединения или неопределённом cleanup работа останавливается
с частичным отчётом. Ctrl+C отменяет операцию, cleanup ограничен отдельным deadline.
Общего12-минутного ограничения старого probe здесь нет: все обнаруженные цели
обрабатываются последовательно, а каждый запрос ограничен timeout.

## Как агент читает результат

```powershell
Get-Content -Raw -Encoding utf8 artifacts/regions-result.json
$regionResult = Get-Content -Raw -Encoding utf8 artifacts/regions-result.json | ConvertFrom-Json
$regionResult.Regions
$regionResult.Repeaters
```

JSON schema1: `NodePublicKey`, `StartedUtc`, `CompletedUtc`, `Completed`, `Error`,
`PotentiallyIncomplete`, `Scans`, `Repeaters`, `Regions`.
`Regions` содержит точные `Name` и массивы `SourcePublicKeys`.
`Repeaters` содержит полный `PublicKey`, `Name`, `Attempts`, `Status`, `Regions`,
`Tag`, `RemoteClock`, `Restoration`, `Error`. Статусы отличают успех с пустым списком,
timeout, malformed, rejection, отмену и не начатые цели. `Completed=true` означает,
что проход закончился, а не что все репитеры ответили. Проверять также статусы целей,
restoration и root Error. Итоговый JSON печатается между маркерами
`REGION_CATALOG_JSON_BEGIN` / `REGION_CATALOG_JSON_END`.

Файл создаётся до подключения и сохраняется в `finally`, в том числе при ошибке
или отмене; существующий результат не перезаписывается. Читать после завершения
процесса. Аварийное завершение процесса/отказ диска не гарантируют финальную запись.
Результат остаётся в указанном локальном файле (artifacts игнорируется git), агент
может использовать его позже, не запуская радиооперации заново.

Exit codes:0 — все цели ответили без конфликта восстановления;3 — нет результатов,
частичный успех или route conflict;1 — ошибка;130 — отмена.

## Проверка реализации без радио

Только отдельная ручная команда (не общий runner):

```powershell
dotnet run --project samples/MeshCoreSharp.Console -c Release --no-restore -- --region-catalog-self-test
```

Проверено17.10.2026 на loopback: первая discovery-попытка пустая, вторая даёт7целей,
третья не вызывается; опрошены все7, первый timeout повторён до успеха,
JSON читается и сохраняет7источников, исходный маршрут восстановлен. PASS.
Физический запуск нового catalog-test при его разработке не выполнялся.
