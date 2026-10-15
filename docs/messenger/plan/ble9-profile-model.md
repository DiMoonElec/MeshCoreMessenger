# BLE9 — Модель и валидация BLE-профиля

[Stage BLE](stage-ble.md) · [Оглавление](../../MESSENGER_PLAN.md)

**Статус: запланирован 15.10.2026; реализация не начата.**

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

- [ ] Изменения в границах подэтапа выполнены.
- [ ] Обязательные проверки выполнены; фактические результаты записаны ниже.
- [ ] Статус подэтапа, checklist Stage BLE и current-state обновлены.

## Результат выполнения

Пока отсутствует. При выполнении записать дату, изменения, команды и результаты
проверок, оставшиеся ограничения. Не заменять реальные результаты планом.
