# BLE9 — Модель и валидация BLE-профиля

[Stage BLE](stage-ble.md) · [Оглавление](../../MESSENGER_PLAN.md)

**Статус: реализован 16.10.2026.**

**Цель:** Добавить endpoint BLE в domain без изменения хранилища.

**Зависимости:** BLE2. Проверить результаты зависимостей перед началом.
Общие инварианты и решения — в [Stage BLE](stage-ble.md).

**Файлы/область:** `Core/Domain/ConnectionTransportKind.cs`, `ConnectionProfile.cs`, `ConnectionProfileDraft.cs`, `ConnectionProfileValidator.cs`, profile manager; `ConnectionProfileTests`.
Новые имена/папки — предлагаемое место реализации; проверить актуальные исходники.

## Шаги

1. Добавить Ble=2, не менять Tcp=0/Serial=1.
2. Добавить nullable BleDeviceId и BleDeviceName: ID обязателен для BLE, name только display hint. PIN и ожидаемый публичный ключ не сохранять.
3. Для TCP/Serial BLE-поля должны быть пустыми; для BLE TcpHost/TcpPort/SerialPortName/BaudRate пустые. Сохранить существующие timeout/auto-connect/reconnect flags.
4. Обновить преобразования draft/profile и очистку неактивных полей. Имя устройства не становится identity ноды.
5. Временно не показывать BLE в production editor до полной интеграции.

**Не входит:** SQLite, transport creation, новый UI.

## Проверки и завершение

**Тесты/проверки:** Valid BLE; пустой ID; смешанные настройки; старые TCP/Serial; draft roundtrip; два профиля с одинаковым именем устройства и разными ID.

**Готово:** Общие модели portable, validator однозначен; существующие профили имеют прежнюю семантику.

**Ручная проверка:** Не требуется.

- [x] Изменения в границах подэтапа выполнены.
- [x] Обязательные проверки выполнены; фактические результаты записаны ниже.
- [x] Статус подэтапа, checklist Stage BLE и current-state обновлены.

## Результат выполнения

16.10.2026: добавлен Ble=2 без изменения значений TCP/Serial, nullable BleDeviceId
и BleDeviceName в profile/draft. Validator требует непустой BLE ID и запрещает
смешанные поля. Manager нормализует поля выбранного транспорта и очищает остальные;
таймауты, flags, ID и дата создания сохраняют прежнюю семантику. Имя — display hint,
не identity. WinRT, БД, transport mapper и production editor не менялись.

Проверки: `dotnet run --project tests/MeshCoreMessenger.Core.Tests -c Release --
-class MeshCoreMessenger.Core.Tests.BleProfileModelTests
-class MeshCoreMessenger.Core.Tests.ConnectionProfileTests` — **18/18**.
BLE проверяется с in-memory store: разные ID при одинаковом имени, обязательность ID,
смешанные поля, нормализация draft/update и очистка полей при смене транспорта;
прежние TCP/Serial tests прошли. SQLite roundtrip относится к BLE11.
`dotnet build MeshCoreSharp.sln -c Release --no-restore` — успешно, 0 warnings/errors.
Ручная проверка не требуется. Следующий подэтап — BLE10.
