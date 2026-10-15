# BLE12 — Подключение транспорта через DI

[Stage BLE](stage-ble.md) · [Оглавление](../../MESSENGER_PLAN.md)

**Статус: запланирован 15.10.2026; реализация не начата.**

**Цель:** Создавать BLE-сессию через прежний MeshCoreClient и CompanionSession.

**Зависимости:** BLE7, BLE11; аппаратный результат BLE8 проверить до production включения. Проверить результаты зависимостей перед началом.
Общие инварианты и решения — в [Stage BLE](stage-ble.md).

**Файлы/область:** `Core/Application/MeshCoreClientFactory.cs`, `Desktop/Bootstrap/AppBootstrap.cs`, Desktop platform provider; Core/Desktop tests.
Новые имена/папки — предлагаемое место реализации; проверить актуальные исходники.

## Шаги

1. Ввести узкую injectable transport factory (предлагаемое имя ICompanionTransportFactory). Общий provider создаёт TCP/Serial, Windows provider добавляет BLE.
2. Сохранить общий MeshCoreClientAdapter и единое создание MeshCoreClientOptions; AutoReceiveMessages=false и ACK timeout mapping не менять.
3. Core остаётся net10.0 и не знает WinRT. Windows provider в Desktop только выбирает/создаёт библиотечный транспорт, не реализует GATT или протокол.
4. Сохранить существующий parameterless MeshCoreClientFactory для callers/tests либо совместимый default constructor; новую DI-регистрацию проверить на реальном composition root.
5. На unsupported platform BLE даёт typed unsupported result без попыток подключения; TCP/Serial остаются доступны. Новый транспорт создаётся на каждую попытку.

**Не входит:** UI выбора, retry policy, дублирование client adapter, новый проект.

## Проверки и завершение

**Тесты/проверки:** DI graph Windows/portable; выбор всех transport kinds; одинаковые client options; два connect создают независимые экземпляры; unsupported BLE не ломает startup/history.

**Готово:** CompanionSession использует BLE без нового protocol runtime и без изменения Identify → sync → drain.

**Ручная проверка:** Реальный Desktop connect пока не нужен; fake session + временная БД.

- [ ] Изменения в границах подэтапа выполнены.
- [ ] Обязательные проверки выполнены; фактические результаты записаны ниже.
- [ ] Статус подэтапа, checklist Stage BLE и current-state обновлены.

## Результат выполнения

Пока отсутствует. При выполнении записать дату, изменения, команды и результаты
проверок, оставшиеся ограничения. Не заменять реальные результаты планом.
