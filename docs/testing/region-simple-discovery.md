# Простой поиск и каталог регионов: реализация и проверки

17.10.2026. [Архитектурный план](../messenger/plan/region-simple-discovery.md).
Первоначально реализация оставлена без коммита по запросу пользователя.
Затем пользователь подтвердил «Теперь Ок» и поручил закоммитить изменения.
Это подтверждение работы UI; незавершённую аппаратную матрицу оно не закрывает.
Позже пользователь сообщил результаты конкретных проверок и поручил зафиксировать
REGION32/33 как есть: [ручная приёмка и итог REGION34](region-user-acceptance.md).

## Поведение

В Settings сохранена отдельная карточка default ноды. Карточка каталога содержит
только поиск, отмену, бегунок и строки регионов с +/−. Каталог доступен offline.
Найденные публичные имена объединяются по полному ключу региона с сохранёнными
определениями; поиск не добавляет их в Regions автоматически. Удалённая строка
остаётся с + до ухода из Settings. Повторное открытие читает только каталог.
Сохранённые private/custom/archived записи видны; private delete/re-add сохраняет
исходный ключ. Для назначенного каналу региона Delete отклоняется уведомлением,
назначение не сбрасывается. Default и per-channel policy работают через прежние
сервисы и post-commit обновления.

Core preset DirectNeighbors: scan30s, только full-key Repeater zero-hop ответы,
RSSI/SNR/key sort, максимум четыре цели до preflight, отсутствие fallback на
сохранённые контакты. Исходящий путь для свежего соседа — zero-hop, обратный тоже
ZeroHop. Старые flood/multi-hop пути готовятся временно и восстанавливаются
условно; новые advert/path изменения не затираются. Для implicit firmware contact
типа None достаточно свежего full-key Repeater ответа; тип контакта не меняется.

До трёх попыток по15s, пауза5s, повтор только timeout RemoteBinaryResponse.
Одна ownership-операция охватывает подготовку, попытки и cleanup. Нет общего
минутного deadline; scan/attempt/cleanup ограничены отдельно. Промежуточный progress
содержит owner/run и полученные имена; отмена сохраняет их. Новый поиск блокируется
до cleanup. Закрытие Settings/смена ноды очищает временные строки и отсекает поздние
результаты; disconnect сохраняет уже показанное и отменяет эфирную операцию.

## Выполненные проверки

- Release build изолированного Desktop/tests с актуальными project references:
  ноль ошибок и предупреждений.
- Полная Desktop suite на основной реализации: **430/430** (257,597s), включая
  два сценария по100000 сообщений. После финальных session/owner guards и
  дополнительных тестов выполнен свежий build в regionsimplefinal и адресные
  **12/12** для RegionCatalogSearchTests/RegionUiTests и **3/3** для интеграции
  Settings lifecycle, channel assignment и composer budget. Полная suite после
  этих последних правок повторно не объявляется выполненной.
- Library **223/223**: добавлена fake-clock проверка ответа discovery на27-й секунде
  30-секундного окна; проверен отказ принять старый tag в следующей попытке.
- Core: основной полный прогон **567/567**. После регрессии implicit contact type
  и финальных изменений выполнены адресные **10/10**, затем итоговый полный
  прогон **568/568** (107,847s).
- Native Light/Dark × 560/1040: **4/4**, реальный MainWindow, focus/bounds,
  минимальный состав карточки и per-channel submenu. Fake supervisor, временная
  БД, ConnectCalls=0. Скриншоты: artifacts/region-ui-native.
- TCP loopback: первый запрос регионов timeout, второй успешен; wire `39 key32
  01 00`, исходящий zero-hop, восстановлен исходный multi-hop, сохранён один
  terminal outcome. Сохранённый, но не обнаруженный репитер не запрашивается.
- SQLite/UI: union/повторный scan, дедупликация public/#public по ключу, +/−,
  повторное открытие, private exact key, архивные записи, ошибка writer,
  RegionInUse, отмена с удержанием busy до cleanup, disconnect/replacement session,
  смена ноды и игнорирование позднего результата.

Промежуточные сбои исправлены: native assertion первоначально проверял также
внутренний TextBox шаблона соседнего default ComboBox; ограничен границей карточки.
SQLite error fixture первоначально оставлял pooled connection с файловым lock;
для тестового подключения установлен Pooling=false. Это не production RF failures.

## Воспроизведение

Команды из корня, PowerShell. Изолированные выходные папки не затрагивают
работающее приложение. Не собирать заново в папку выполняющегося test runner.

```powershell
dotnet build tests/MeshCoreMessenger.Core.Tests -c Release --no-restore -v quiet
dotnet tests/MeshCoreMessenger.Core.Tests/bin/Release/net10.0/MeshCoreMessenger.Core.Tests.dll -noLogo -parallelMode none -longRunning 40
dotnet build tests/MeshCoreMessenger.Desktop.Tests -c Release --no-restore -p:UsedAvaloniaProducts= -o artifacts/regionsimplefinal -v quiet
dotnet artifacts/regionsimplefinal/MeshCoreMessenger.Desktop.Tests.dll -noLogo -parallelMode none -class MeshCoreMessenger.Desktop.Tests.RegionCatalogSearchTests -class MeshCoreMessenger.Desktop.Tests.RegionUiTests
dotnet artifacts/regionsimplefinal/MeshCoreMessenger.Desktop.Tests.dll -noLogo -parallelMode none -method '*RegionCardFollows*' -method '*RegionMenuOffline*' -method '*RegionPolicyChanges*'
dotnet build tests/MeshCoreSharp.Tests -c Release --no-restore -v quiet
dotnet tests/MeshCoreSharp.Tests/bin/Release/net10.0/MeshCoreSharp.Tests.dll
dotnet build tools/MeshCoreMessenger.ViewportAudit -c Release --no-restore -p:UsedAvaloniaProducts= -o artifacts/regionsimplenative -v quiet
dotnet artifacts/regionsimplenative/MeshCoreMessenger.ViewportAudit.dll --region-only
```

Полный Desktop запуск: тот же свежий build, runner без фильтров классов/методов.
Native runner требует доступ к Windows UI; железо и пользовательская БД не нужны.
Специализированный hardware console-тест остаётся только opt-in.

## Ограничения приёмки

В этом изменении аппаратных RF-команд и подключения к CatCore не было.
Предыдущие аппаратные отчёты не подтверждают новый end-to-end GUI сценарий.
Пользователь подтвердил работу обновлённого UI. Для полной аппаратной матрицы
нужен отдельный согласованный hardware запуск. Пользовательское приложение
не закрывалось, его данные не менялись.
