# BLE: зависание повторного запуска с сохранённым профилем

16.10.2026. Регрессия BLE15, обнаруженная пользователем после BLE16:
обычный `dotnet run --project src/MeshCoreMessenger.Desktop -c Release`
оставлял процесс без окна, Ctrl+C не завершал зависшее приложение.

## Причина и подтверждение

Сняты managed stacks и heap dump зависшего Desktop PID4528. В памяти обнаружено
исключение Avalonia `The calling thread cannot access this object because a
different thread owns it` из `Dispatcher.InitializeUIThreadDispatcher` при
`StartWithClassicDesktopLifetime`. Async stacks показали два незавершённых пути:
`SynchronizeBleEndpointAsync → AvaloniaUiDispatcher.InvokeAsync` и
`ShutdownAttemptAsync → MainWindowViewModel.StopAsync → Profiles.StopAsync → InvokeAsync`.

Program загружает offline ViewModel до инициализации Avalonia. Восстановление
сохранённого BLE-профиля из storage continuation впервые обращалось к UIThread,
создавая dispatcher на фоновом потоке. Затем настоящий UI startup падал на STA
потоке, а его finally зависал на очистке, ожидающей незапущенный dispatcher.
Первоначальное исключение не успевало выйти в консоль из-за этого finally.
Зависший процесс остановлен после диагностики; пользовательская БД не редактировалась.
Диагностические файлы остались локально в ignored artifacts, не входят в Git.

## Исправление

Initial profile restore теперь непосредственно строит offline BLE projection,
как существующие startup-проекции истории и composer. На время восстановления
профилей подавлена повторная постановка endpoint update в UI dispatcher;
изменения выбранного endpoint защищены прежним loading guard. Поиск, последующий
выбор профиля, отмена и работа уже открытого UI продолжают использовать dispatcher.
Program/startup presentation, транспорт и сохранённые профили не менялись.

## Проверки

- Новый `AppBootstrapTests.SavedBleProfileRestoresBeforeUiLoopWithoutCreatingDispatcher`
  проверяет полный root LoadAsync с выбранным BLE и dispatcher, запрещающим UI
  работу до старта: ноль обращений, восстановленный endpoint, чистый draft, Offline.
- Целевые Bootstrap/BLE scan/editor/connection tests:45/45 Windows и45/45 portable.
- `NativeBleStartupAudit`: два запуска настоящего Desktop executable с одной
  временной БД, сохранённым BLE-профилем и историей. Окно появляется, закрытие
  штатно завершает процесс с exit0, история после повторного запуска сохранена.
  AutoConnect/Realtime BLE выключены, пользовательская БД не открывается.
- Существующий native BLE editor audit прошёл: TCP→Serial→BLE→TCP→BLE,
  поля/save/readback, fake discovery, Light/Dark и узкое/широкое окно.
- Windows/portable locked restore и Release build проверены; полный Core/library
  suite повторно не запускался, их production-код этим исправлением не менялся.

Прежние native editor audits и unit tests уже имели работающий или подменённый
dispatcher, поэтому не воспроизводили обычный запуск. Новая native проверка
создаёт отдельный процесс до любой инициализации Avalonia в нём:

```powershell
dotnet run --project tools/MeshCoreMessenger.ViewportAudit -c Release -- --ble-startup-only src/MeshCoreMessenger.Desktop/bin/Release/net10.0-windows10.0.19041.0/win-x64/MeshCoreMessenger.Desktop.exe
```

Это проверка запуска и штатного закрытия через окно. Общая обработка Ctrl+C
для Windows GUI-процесса отдельно не добавлялась и не заявляется проверенной.
