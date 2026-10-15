# BLE3: обнаружение устройств Windows

15.10.2026. [План и реализация BLE3](../messenger/plan/ble3-discovery.md).

## Среда и действия

Windows ноутбук, встроенный Realtek Bluetooth Adapter, Microsoft Bluetooth LE Enumerator,
.NET SDK 10.0.401. Пользователь сообщил о подготовленной BT-ноде. Точная модель и
firmware пока не установлены; соединения и команды Companion не выполнялись.

После прохождения fake tests выполнен только scan и чтение metadata Windows:

```powershell
dotnet run --project samples/MeshCoreSharp.Console -c Release --no-build -- --list-ble 3
```

Финальный результат: **один кандидат, MeshCore-RnD CatCore🐈, NUS=true, paired=false**.
Получен OS DeviceInformation.Id, выведен в локальный лог artifacts/ble3/native-scan-final.log.
ID/адреса не используются как MeshCore identity; полный ключ ноды не проверялся.
Пользователь подтвердил: нода найдена корректно («Да, ноду нашел корректно»).
Повторный scan после исправления native lookup дал тот же кандидат за около 4,3 с
вместе с запуском dotnet. Bluetooth radio не переключался, pairing не менялся.

## Особенность Windows enumeration

Первоначальный вариант resolving через DeviceInformation.FindAllAsync с typed address
selector не успевал завершить metadata phase ни за 5 с, ни за диагностические 15 с.
Проверка адаптера и сам advertisement watcher работали. Диагностика показала, что
paired enumeration завершается, NUS advertisement приходит, а address selector Windows
включает inquiry и его FindAllAsync ожидает полный результат enumeration.

Исправлен сам способ ожидания, а не увеличен постоянный timeout: resolving использует
DeviceWatcher, сопоставляет DeviceAddress/AddressType из RequestedProperties и завершается
при появлении нужного endpoint. Watcher останавливается с отпиской Added/Updated/Removed/
EnumerationCompleted/Stopped. LookupTimeout оставлен **5 с**. Временная диагностическая
команда удалена; в sample осталась только штатная --list-ble.

## Автоматические проверки

- Windows solution Release: 0 warnings/errors, библиотечный harness **122/122**.
- Portable solution Release: 0 warnings/errors, библиотечный harness **121/121**.
- Locked restore обеих веток успешен; dependency versions не менялись.
- Восемь новых cases проверяют объединение advertisement/scan response, typed addresses,
  совпадающие имена, offline saved/paired candidates, все ветви stop/cancel/failure,
  metadata deadline и поздние callbacks/results. Windows hardware не используется
  при обычном запуске tests.

## Ограничения

- Не выполнялись GATT open, CCCD, MTU negotiation, чтение firmware/полного ключа,
  message drain, передача сообщений или изменение конфигурации.
- При отсутствии разрешимого OS endpoint результат не содержит выдуманный ID.
  Paired/saved endpoint остаётся кандидатом с неподтверждённым сервисом.
- Windows radio off/no adapter/access denied и cancel/restart проверены fake;
  настоящее отключение Bluetooth во время scan в этой проверке не выполнялось.
- Native macOS не проверялся; portable target возвращает UnsupportedPlatform для
  native discovery. Подключение Desktop остаётся будущими подэтапами.

Источники: [Microsoft advertisement sample](https://github.com/microsoft/Windows-universal-samples/blob/main/Samples/BluetoothAdvertisement/cs/Scenario1_Watcher.xaml.cs),
[typed address selector](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.bluetoothledevice.getdeviceselectorfrombluetoothaddress),
[DeviceAddress type property](https://learn.microsoft.com/en-us/windows/win32/properties/props-system-devices-aep-bluetooth-le-addresstype),
[enumeration sample](https://github.com/Microsoft/Windows-universal-samples/blob/main/Samples/BluetoothLE/cs/Scenario1_Discovery.xaml.cs).
