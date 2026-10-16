# REGION11 — приёмка библиотечного фундамента

16.10.2026. REGION1–10 проверены без реального устройства. Firmware wire reference:
`81dbae592ffad40b4212bc0b37e82b38da08f4bc`, локальный игнорируемый checkout
`tmp/region-firmware`; это не утверждение об идентичности установленной сборки CatCore.

## Результаты

| Проверка | Результат |
| --- | --- |
| Windows library suite | 225/225 |
| Portable library suite | 223/223; два Windows platform cases исключены штатным runner |
| Core suite | 460/460 |
| Release solution, Windows | 0 warnings / 0 errors |
| Release solution, portable | 0 warnings / 0 errors |
| Locked restores обеих веток | Успешно; package pins/locks не изменены |
| Сборки библиотеки | Одна project-owned MeshCoreSharp.dll на каждый framework |

Команды из корня (portable-вариант — с `-p:McmWindowsDesktop=false`):

```powershell
dotnet restore MeshCoreSharp.sln --locked-mode -p:NuGetAudit=false --ignore-failed-sources
dotnet build MeshCoreSharp.sln -c Release --no-restore -p:UsedAvaloniaProducts= -v quiet
dotnet run --project tests/MeshCoreSharp.Tests -c Release --no-restore
dotnet run --project tests/MeshCoreMessenger.Core.Tests -c Release --no-restore -- -noLogo
```

NuGetAudit отключён только для этих offline restore-команд: feed был недоступен
(NU1900). Обычный solution build первоначально остановился на AvaloniaStatsTask,
пытавшемся писать вне workspace в AppData/AvaloniaUI/BuildServices/buildtasks.log.
Пустой UsedAvaloniaProducts отключает только этот необязательный build telemetry
target; исходники и настройки проекта не менялись. Обе Release-ветки после этого
собраны полностью.

## Сценарии и найденные регрессии

REGION1–10 cases проверяют immutable keys, exact UTF-8 derivation, default GET/SET/CLEAR,
scope cancellation/cleanup, concurrent channel/private/default операции, ACK admission,
ранний ACK, MTU/admission snapshots, discovery full/prefix identity, CSV/path codec,
bounded collection и MSG_SENT→binary matching с remote single-flight.

Новый сквозной сценарий использует существующий Serial transport и FakeSerialConnection.
RX marker и UInt16 header фрагментируются, хвост payload и следующие frames объединены.
На одном client выполняются channel A / force-unscoped B / private InheritDefault,
MSG_SENT+ACK, автоматический incoming drain, neighbor scan и directed region request.
Connect/start/device/default reads не выполняют scope/config writes и не повторяют sends.

Расширенный Core прогон обнаружил два пересечения:

- Общий sample fake server не знал CMD54; теперь валидирует две короткие mode-формы
  и полную key16-форму, отвечает OK/IllegalArgument. Эмулятор не доказывает radio scope.
- Ошибка ACK Bind после уже принятого MSG_SENT ошибочно считалась неопределённым
  immediate ответом и закрывала соединение. Transaction теперь отмечает полученный
  ответ до внутреннего hook; dispatcher закрывает admission только при неопределённости.
  Duplicate-tag ambiguity по-прежнему завершает обе Delivery ошибкой, без ложного ACK.

Дополнительная регрессия подтверждает, что ArgumentException транспорта означает
локальную проверку до TX (это явно записано в transport contract): после успешного SET
выполняется bounded RESET, здоровая сессия остаётся доступна. Timeout/cancel после
возможного TX без definite response по-прежнему закрывают admission до release gate.

## Ограничения

Hardware, reboot persistence, реальная изоляция autonomous firmware PATH/ACK,
radio delivery scope и асимметричные маршруты не проверялись. Они остаются REGION32/33.
Пустой discovery не доказывает отсутствие соседей/регионов; region CSV потенциально
неполон. CMD57 для неизвестного full key может вызвать firmware addContact; library
не выполняет собственные route/contact mutations. Core preflight/ownership — REGION22/23.
GUI и прикладное хранение регионов этим отчётом не объявляются реализованными.
