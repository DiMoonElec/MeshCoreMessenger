# BLE1 — Сборка Windows и сохранение portable target

[Stage BLE](stage-ble.md) · [Оглавление](../../MESSENGER_PLAN.md)

**Статус: реализован 15.10.2026; проверки матрицы сборки выполнены. Полные Core/Desktop suites имеют ограничения Windows, описанные ниже.**

**Цель:** Подготовить доступ к WinRT внутри единственного проекта MeshCoreSharp, сохранив сборку TCP/Serial на macOS.

**Зависимости:** Нет. Проверить результаты зависимостей перед началом.
Общие инварианты и решения — в [Stage BLE](stage-ble.md).

**Файлы/область:** `src/MeshCoreSharp/MeshCoreSharp.csproj`, `Directory.Build.props`, существующие csproj samples/tests/Desktop и их lock files.
Новые имена/папки — предлагаемое место реализации; проверить актуальные исходники.

## Шаги

1. Зафиксировать baseline: git status, выбранный SDK, Release build и команды запуска существующих тестов. Пользовательский global.json не менять.
2. Сделать MeshCoreSharp multi-target: net10.0 и net10.0-windows10.0.19041.0. WinRT-исходники включать только в Windows target; общие исходники/API должны совпадать.
3. Оставить Core на net10.0. Для Windows Desktop явно обеспечить выбор Windows-варианта MeshCoreSharp, при необходимости прямой ProjectReference. Проверить разрешение транзитивной ссылки Core: в выходной папке одна MeshCoreSharp.dll.
4. Подготовить Windows target существующего Console sample и библиотечных tests для последующих BLE-проверок. Portable target и обычный запуск без hardware-флагов сохранить.
5. Выбрать детерминированную схему lock files по TFM/платформам с учётом существующего McmWindowsDesktop; обновлять только необходимый граф. Не обновлять версии посторонних пакетов.

**Не входит:** BLE-транспорт, UI, миграция БД; переход всей Core на Windows; новые библиотечные проекты.

## Проверки и завершение

**Тесты/проверки:** Сборка обоих library targets; Windows Desktop build/publish dependency graph; portable Core и tests; restore --locked-mode после обновления locks. Проверить отсутствие WinRT/WindowsAppSDK в portable выходе. Windows API smoke — только создание типов/проверка доступности без соединения.

**Готово:** Матрица целей собирается; Windows Desktop получает Windows-библиотеку, portable потребители — portable. Команды и фактические результаты записаны в подэтап.

**Ручная проверка:** Не требуется. Native macOS build отложить до BLE18, если macOS недоступна; кросс-сборку не считать native проверкой.

- [x] Изменения в границах подэтапа выполнены.
- [x] Обязательные проверки выполнены; фактические результаты записаны ниже.
- [x] Статус подэтапа, checklist Stage BLE и current-state обновлены.

## Результат выполнения

15.10.2026: план BLE и пользовательский global.json закоммичены в `f0dfa32`.
BLE1 выполнен после этого коммита. SDK — 10.0.401; global.json не менялся.

MeshCoreSharp имеет два target assets: net10.0 и net10.0-windows10.0.19041.0;
EnableWindowsTargeting позволяет restore/build Windows target с других ОС.
Исходники Transport/Ble/Windows/**/*.cs исключены из portable target.
Core остаётся net10.0. Прямая условная ссылка Desktop на MeshCoreSharp выбирает
Windows asset, несмотря на portable зависимость Core. Новые проекты не добавлены.

Console sample и библиотечный test harness на Windows выбирают Windows target,
на других ОС — portable. На Windows portable вариант выбирается свойством
`-p:McmWindowsDesktop=false`; обычному `dotnet run --project ...` не нужен `-f`.
Для этих двух consumers добавлены отдельные packages.windows.lock.json; прежние
portable locks сохранены. Library lock содержит обе TFM. Обновлены Windows locks
Desktop.Tests и двух Desktop tools из-за новой прямой project dependency.
В ViewportAudit lock также исправлена устаревшая project version Desktop на
фактическую 0.1.0. Версии NuGet packages и Directory.Packages.props не менялись.

Исходная Windows solution build имела MSB3030 в Desktop.Tests: Windows App SDK
неявно включал семь PNG в Content, а Avalonia встраивает их в assembly. В ранней
Windows Desktop property group отключён EnableDefaultWindowsAppSdkContentItems;
AvaloniaResource сохранён. После исправления Content пуст, решение и tools собираются.

## Проверки 15.10.2026

Команды выполнены из корня репозитория. Доступ вне sandbox понадобился для
Avalonia build log, NuGet cache и тестовых временных файлов/локальных сокетов.

| Проверка | Команда / способ | Результат |
| --- | --- | --- |
| Windows locked restore | `dotnet restore MeshCoreSharp.sln --locked-mode` | Успешно |
| Windows Release solution | `dotnet build MeshCoreSharp.sln -c Release --no-restore` | 0 warnings/errors; оба library targets |
| Portable locked restore | `dotnet restore MeshCoreSharp.sln -p:McmWindowsDesktop=false --locked-mode` | Успешно; portable locks не изменились |
| Portable Release solution | `dotnet build MeshCoreSharp.sln -c Release -p:McmWindowsDesktop=false --no-restore` | 0 warnings/errors; проверено на Windows, не native macOS |
| Library Windows tests | `dotnet run --project tests/MeshCoreSharp.Tests -c Release --no-build` | 107/107 |
| Library portable tests | `dotnet run --project tests/MeshCoreSharp.Tests -c Release -p:McmWindowsDesktop=false --no-build` | 106/106 |
| Windows API smoke | BuildTargetTests внутри Windows harness | ApiInformation видит BluetoothLEDevice/GattSession; нет scan/connect |
| Library asset | BuildTargetTests + SHA256 output DLL | Desktop совпадает с Windows asset; Core — с portable |
| Portable dependencies | Core deps.json и portable assembly references | Нет WinRT/Windows SDK/WindowsAppSDK dependency |
| Sample TCP regression | `dotnet run --project samples/MeshCoreSharp.Console -c Release --no-build -- --self-test` | SELF-TEST PASSED; только localhost |
| Core profile tests | `dotnet run --project tests/MeshCoreMessenger.Core.Tests -c Release --no-build -- -noLogo -class MeshCoreMessenger.Core.Tests.ConnectionProfileTests` | 13/13 |
| Desktop profile tests | `dotnet run --project tests/MeshCoreMessenger.Desktop.Tests -c Release --no-build -- -noLogo -class MeshCoreMessenger.Desktop.Tests.ConnectionProfilesViewModelTests` | 8/8 |
| Tools Windows locks/build | `dotnet restore tools/<project> --locked-mode`, `dotnet build tools/<project> -c Release --no-restore`, для ViewportAudit/SendUiPreview | Оба успешно; 0 warnings/errors |
| Windows self-contained publish | `dotnet publish src/MeshCoreMessenger.Desktop -c Release -r win-x64 --self-contained true --no-restore -p:PublishTrimmed=false -p:PublishSingleFile=false -o artifacts/ble1/publish` | Успешно; одна MeshCoreSharp.dll, SHA256 совпадает с Windows asset; WinRT.Runtime.dll присутствует |
| Whitespace | `git diff --check` | Успешно |

После portable проверки восстановлен Windows dependency graph через locked restore.
Локальные библиотечные логи — artifacts/ble1/library-windows.log и
library-portable.log, Desktop run — artifacts/ble1/desktop.log; artifacts игнорируются Git.

## Известные ограничения

- Baseline до правок BLE1: библиотека 105/105; полная Core suite — 387 tests,
  8 failures (379 passed). Ошибки очистки SQLite messenger.db при открытых Windows
  handles, reset/close localhost TCP fixture и timing cancellation SQLite search.
  Эти failures наблюдались до изменения targets; они не исправлялись в BLE1.
- Полная Desktop suite запущена после исправления сборки; получен FAIL в
  FailedHistoryClearPreservesVisibleSearchDraftAndOffersAnotherAttempt при удалении
  SQLite messenger.db из fixture. Общий итог не получен: процесс не завершился и
  был остановлен после 2 мин 32 с. Причина незавершения не установлена; suite нельзя
  считать успешной. Targeted profile tests после остановки прошли. Первый build
  ViewportAudit столкнулся с DLL lock этого тестового процесса; после его остановки
  оба tools повторно собраны без предупреждений.
- Native macOS, запуск GUI из опубликованного EXE и реальный BLE не проверялись.
  Native macOS приёмка остаётся в BLE18; transport/device probe — BLE2–BLE8.
- Полные Core/Desktop suites не являются зелёными. Это отдельное ограничение
  регрессии Windows; успешность BLE1 относится к указанной матрице targets,
  locked restore, publish и адресным проверкам. BLE2 не начат.
