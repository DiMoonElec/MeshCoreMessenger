# REGION — последовательный план внедрения регионов

[Оглавление](../../MESSENGER_PLAN.md) · [Маршрутизация чтения](../../README.md)

16.10.2026: **REGION0–REGION15 выполнены; REGION16–REGION34 не начаты.**
Анализ и исходный проект сохранены в commit `2d4e5fe`.
По запросу пользователя первоначальные десять крупных блоков заменены небольшими
последовательными подэтапами. Номера REGION1–REGION10 теперь означают новые задания;
реализованных работ под прежними номерами не было.

- [Архитектура и общие контракты](region-design.md) — владельцы, модели, API, БД,
  отправки, повторы, discovery и ограничения.
- [Поведение ноды и wire](../../library/protocol/regions.md) — проверенные исходники,
  default/override, ЛС/ACK, регионы репитеров и протокол обнаружения.
- GUI будет спроектирован **совместно с пользователем в REGION26**.
  REGION27–30 задают функциональные границы, их компоновка уточняется после этого.

## Стратегия разбиения

Использован формат [BLE](stage-ble.md) и [D](stage-d.md): один небольшой результат,
один основной слой, отдельный файл задания, зависимости, 3 конкретных шага,
обязательные проверки и явный критерий завершения. Нужные runtime- и storage-
изменения сделаны отдельными заданиями, а не спрятаны внутри GUI.
Существующие проекты, транспорта, DI и выполненные этапы не пересоздавать.

| Блок | Подэтапы | Результат |
| --- | --- | --- |
| Библиотека | REGION1–11 | Scope API, безопасные отправки и две отдельные discovery операции |
| Приложение | REGION12–25 | Каталог/БД, default, назначения, snapshots, повторы и discovery |
| Совместный дизайн | REGION26 | Согласованные сценарии и представление в GUI |
| Desktop | REGION27–31 | Небольшие UI-интеграции и автоматическая приёмка |
| Железо и завершение | REGION32–34 | Проверка ноды/сети, восстановление настроек, инструкция |

Внутри последовательности сначала pure types/codec, затем runtime/fake,
далее Core contracts/schema/store/workflow и только потом GUI.
Это уменьшает число незавершённых зависимостей в каждом задании.
Два промежуточных рубежа (REGION11 и REGION25) проверяют готовый слой
до перехода к следующему. Аппаратные шаги не подменяются fake-тестами.

## Порядок и checklist

Идти по номерам. Зависимость от предыдущего шага означает, что результаты более
ранних этапов уже доступны; конкретные внешние условия приведены в файле шага.
REGION0 — завершённое исследование; выполненные подэтапы отмечены ниже.

| Готово | Этап | Ограниченный результат |
| --- | --- | --- |
| ☑ | [REGION1](region1-models.md) | Типы scope и ключи регионов |
| ☑ | [REGION2](region2-default-api.md) | Чтение и запись default ноды |
| ☑ | [REGION3](region3-scope-lifecycle.md) | Жизненный цикл составной scope-операции |
| ☑ | [REGION4](region4-channel-send.md) | Scope-aware отправка в канал |
| ☑ | [REGION5](region5-private-send.md) | Scope-aware ЛС и ACK admission |
| ☑ | [REGION6](region6-text-budget.md) | Библиотечный budget с учётом scope |
| ☑ | [REGION7](region7-discovery-wire.md) | Форматы поиска соседей и регионов |
| ☑ | [REGION8](region8-neighbors.md) | Сбор ответов соседних репитеров |
| ☑ | [REGION9](region9-remote-runtime.md) | Общий gate и matcher удалённых запросов |
| ☑ | [REGION10](region10-request-regions.md) | Запрос регионов одного репитера |
| ☑ | [REGION11](region11-library-acceptance.md) | Регрессия библиотеки REGION |
| ☑ | [REGION12](region12-core-models.md) | Прикладные модели и контракты |
| ☑ | [REGION13](region13-catalog-schema.md) | Миграция каталога и назначений |
| ☑ | [REGION14](region14-catalog-store.md) | Хранилище и read projections регионов |
| ☑ | [REGION15](region15-catalog-service.md) | Локальные операции каталога и канала |
| ☐ | [REGION16](region16-session-read.md) | Capabilities и чтение default в сессии |
| ☐ | [REGION17](region17-default-service.md) | Изменение default ноды с readback |
| ☐ | [REGION18](region18-attempt-storage.md) | Хранение scope исходящих попыток |
| ☐ | [REGION19](region19-channel-application.md) | Region scope в отправке и повторе канала |
| ☐ | [REGION20](region20-private-application.md) | Default scope в цикле ЛС |
| ☐ | [REGION21](region21-application-budget.md) | Budget и admission для выбранного канала |
| ☐ | [REGION22](region22-discovery-preflight.md) | Проверка целей discovery |
| ☐ | [REGION23](region23-discovery-route.md) | Временный direct route и восстановление |
| ☐ | [REGION24](region24-discovery-service.md) | Полный поиск и локальные наблюдения |
| ☐ | [REGION25](region25-core-acceptance.md) | Сквозная приёмка Core |
| ☐ | [REGION26](region26-gui-design.md) | Совместное проектирование GUI |
| ☐ | [REGION27](region27-catalog-ui.md) | GUI каталога регионов |
| ☐ | [REGION28](region28-selection-ui.md) | GUI default ноды и выбора канала |
| ☐ | [REGION29](region29-discovery-ui.md) | GUI поиска регионов |
| ☐ | [REGION30](region30-send-ui.md) | Composer и сведения о scope отправки |
| ☐ | [REGION31](region31-desktop-acceptance.md) | Сквозная автоматическая приёмка Desktop |
| ☐ | [REGION32](region32-hardware-local.md) | Аппаратная проверка default и транспортов |
| ☐ | [REGION33](region33-hardware-mesh.md) | Аппаратная проверка discovery, ЛС и каналов |
| ☐ | [REGION34](region34-completion.md) | Завершение REGION и инструкция |

Отсутствие реального устройства не блокирует REGION1–31: используются fake transport,
fake session и временные БД. До production включения нужен итог REGION31;
до объявления аппаратной совместимости — REGION32/33.
Если не хватает данных целевой сборки, не подменять ими reference commit:
зафиксировать сверенную версию в fixtures и pending проверку оборудования.

REGION23 реализует только нужное discovery владение route/mutation/readback.
Если необходимые примитивы отсутствуют, добавить их в этом ограниченном scope;
не начинать всю ветку D10 и не объявлять её завершённой.
При обнаружении более крупной обязательной зависимости обновить план явно,
сохранив возможность проверить текущий законченный результат.

## Порядок работы агента

1. Прочитать AGENTS.md, docs/README.md, этот индекс и ровно файл запрошенного шага.
   Для Core/Desktop также прочитать current-state и обязательные ownership/identity/
   agent-rules разделы из README. Не загружать все 34 задания.
2. Прочитать перечисленные в задании разделы region-design/protocol и фактический
   результат предшествующего шага. Старые checkpoints не заменяют актуальный код.
3. Проверить git status и существующие API/tests/schema. Не переписывать чужие
   изменения и не расширять scope ради будущих шагов.
4. Реализовать три пункта задания, используя существующие fake seams и runner.
   Сначала targeted проверки; полный suite — на рубеже слоя или если изменение
   реально затрагивает общие механизмы.
5. Проверить diff и записать actual commands/results, ошибки и ограничения в
   «Результат выполнения». Обновить checkbox таблицы и current-state.
6. Завершить выбранный шаг. Следующий выполняется по запросу пользователя;
   план сам не запускает кодирование, радиооперации или публикацию.

Для обычного запроса на шаг не требуются повторные согласования уже утверждённых
технических решений. Отдельное участие пользователя требуется в REGION26 (GUI)
и для конкретных аппаратных действий REGION32/33; использовать уже полученную
авторизацию, если она покрывает сценарий. При отсутствии условий выполнить
независимую подготовку и оставить соответствующую проверку pending.

## Общие инварианты

- Одна MeshCoreSharp.dll, логические transport frames, continuous RX,
  subscribe-before-send, single-flight immediate и отдельный remote gate.
- Scope канала хранится в приложении; default — на ноде; temporary override глобален.
  Входящим сообщениям не приписывать текущий region, channel identity сохраняется.
- Обычные ЛС используют default ноды; отдельный per-contact region GUI не требуется.
  Library explicit scope — техническая возможность для контролируемой отправки.
- ScopeOperationGate защищает управляемые клиентом команды. Автономный flood ACK
  прошивки может воспользоваться temporary override; этот предел явно сохраняется.
- Connect/reconnect не изменяет default и не повторяет отправки. Failed до SEND,
  Accepted и Unknown после возможного SEND имеют разные последствия.
- История/attempt snapshots immutable, SQLite один writer, full-key NodeId,
  session generation, post-commit UI и существующие shutdown barriers.
- Discovery возвращает потенциально неполный список. Изменения контактов/маршрутов
  имеют отдельный owner и outcome; автоматическое «восстановление» поверх нового
  route запрещено.

Подробности этих правил — в тематических разделах [архитектуры](region-design.md).

## Готовый запрос для следующей сессии

> Выполни REGION16 по docs/messenger/plan/region16-session-read.md.
> Сначала прочитай AGENTS.md и маршрут чтения в docs/README.md.
> Проверь код и результаты зависимостей, затем реализуй только выбранный подэтап,
> выполни его проверки и обнови документацию фактическими результатами.
> Следующие подэтапы автоматически не начинай.

Для продолжения заменить номер и имя файла на строку из таблицы.
