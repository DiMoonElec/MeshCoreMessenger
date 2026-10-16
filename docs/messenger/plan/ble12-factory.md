# BLE12 — Подключение транспорта через DI

[Stage BLE](stage-ble.md) · [Оглавление](../../MESSENGER_PLAN.md)

**Статус: реализован 16.10.2026.**

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

- [x] Изменения в границах подэтапа выполнены.
- [x] Обязательные проверки выполнены; фактические результаты записаны ниже.
- [x] Статус подэтапа, checklist Stage BLE и current-state обновлены.

## Результат выполнения

16.10.2026: MeshCoreClientFactory принимает ICompanionTransportFactory; прежний
parameterless constructor сохранён. Portable CompanionTransportFactory создаёт
TCP/Serial и даёт typed UnsupportedPlatform для BLE. Desktop provider на Windows
создаёт библиотечный BleMeshCoreTransport с уже существующим UI access dispatcher;
на portable target делегирует общему provider. DI зарегистрирован в AppBootstrap.
Constructors не сканируют и не подключают устройства. Каждая попытка получает новый
transport/client, общий adapter, client options, ACK mapping и AutoReceiveMessages=false
сохранены. Core остаётся net10.0 без WinRT; GATT/протокол не копировались в Desktop.

Проверки: Core `CompanionTransportFactoryTests`, `ConnectionProfileTests`,
`CompanionSessionTests` — **26/26**; Desktop `AppBootstrapTests` — **5/5 Windows**
и **5/5 portable**. Две последовательные BLE-profile сессии через injectable
localhost transport используют общий MeshCoreClient/CompanionSession, идентифицируют
одну ноду, читают справочник, выполняют explicit drain и закрываются с persisted
Session.EndedUtc. Это fake integration, не native BLE. DI создаёт независимые
disconnected BLE clients на Windows; portable даёт typed unsupported, сохранённый
профиль и offline supervisor не меняются.

Обе solution Release builds и locked restore успешны, 0 warnings/errors; locks не
менялись, Windows graph восстановлен последним. Ручной Desktop connect не запускался.
По новому указанию пользователя после успешного аппаратного drain до166bytes
продолжена application integration. BLE8 RX174–176 остаётся непроверенным аппаратно;
готовность production UI по этому остатку не заявляется. Следующий этап — BLE13.
