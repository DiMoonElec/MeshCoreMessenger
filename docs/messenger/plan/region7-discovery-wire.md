# REGION7 — Форматы поиска соседей и регионов

[Stage REGION](stage-region.md) · [Общие контракты](region-design.md) · [Протокол](../../library/protocol/regions.md)

**Статус: выполнен 16.10.2026.** Задание подготовлено 16.10.2026.

**Цель:** Encoders/decoders discovery без ожидателей и Core orchestration.

**Зависимости:** REGION6. Проверить фактические результаты перед началом.
Рекомендуемый порядок последовательный: предыдущий шаг — [REGION6](region6-text-budget.md).
Выполнять только этот подэтап; завершение не запускает следующий автоматически.

**Файлы/область:** MeshCoreSharp/Protocol/{Commands,Packets,Parsing}, Models/; parser tests.
Новые типы/файлы здесь — предлагаемое место, не утверждение об их существовании.
Сначала сверить код, git status и версии схемы/API; готовые механизмы использовать повторно.

## Минимальное чтение

Общий порядок — [правила исполнения REGION](stage-region.md#порядок-работы-агента).
Из [архитектуры](region-design.md) и [протокола](../../library/protocol/regions.md)
прочитать только перечисленные ниже разделы и указанные соседние контракты:

protocol/regions.md: «Обнаружение: от репитера к списку имён».

## Шаги

1. Добавить команды55/57, typed control8E и binary8C с bounded parsing; protocol numbers держать в definitions.
2. Разобрать full32/prefix8, тип репитера, tag, SNR/RSSI, remote-clock и CSV; prefix не превращать в полноценную identity.
3. Добавить packed reply-path validation для hash sizes1–3 и отдельный helper reverse по hops; private имена сохранить без public derivation.

**За границами шага:** Активный radio discovery, транзакции ожидания и мутации контактов.

## Проверки и завершение

**Обязательные проверки:** Пустой CSV; optional NUL; invalid UTF-8; truncated headers; full/prefix key; signed signal values; path size/length overflow и reverse multi-byte hops.

**Готово:** Wire fixtures соответствуют закреплённому firmware, parsers сохраняют raw диагностику.

Если зависимость отсутствует или проверка не выполнена, записать точную причину и
оставить соответствующий пункт открытым. Для аппаратных/GUI проверок различать
автоматический результат, native просмотр и пользовательское подтверждение.
Не объявлять всю REGION завершённой по успеху одного слоя.

- [x] Изменения в границах подэтапа выполнены.
- [x] Обязательные проверки выполнены; команды и фактические результаты записаны.
- [x] Ограничения и оставшиеся проверки указаны.
- [x] Статус этого файла, checklist Stage REGION и current-state обновлены.

## Результат выполнения

Добавлены encoders CMD55 full-key repeater filter/tag/since и CMD57 full public key/anon subtype/reply path. RegionProtocol хранит control constants. ControlDataPacket/DiscoveredNeighbor различают full32 и prefix8, signed SNR/RSSI, node type/tag и path descriptor; неизвестный control subtype сохраняет payload. BinaryResponsePacket остаётся generic (reserved/tag/payload); RegionNamesResponse отдельно разбирает remote clock и strict UTF-8 CSV, optional terminal NUL, пустой список и private имена без derivation. ReplyPath копирует bytes, проверяет hash sizes1–3/64-byte bound и reverse по hops.

Fixtures сверены с локальным pinned firmware 81dbae592ffad40b4212bc0b37e82b38da08f4bc: companion onControlDataRecv/onContactResponse/CMD57, repeater discover/handleAnonRegionsReq и RegionMap::exportNamesTo. Базовый generic binary header6 принимается независимо от специфичного region header10; malformed известного parser типа диагностируется существующим RX, raw frame остаётся в typed packets.

Проверка: dotnet run --project tests/MeshCoreSharp.Tests -c Release --no-restore — 211/211. Три новых cases с перебором truncated lengths, full/prefix identity, signed signal, CSV/NUL/invalid UTF-8, path overflow/reverse и exact TX fields. Радиоопераций/мутаций не выполнялось; target hardware verification остаётся REGION32/33.

Следующий шаг — [REGION8](region8-neighbors.md).
