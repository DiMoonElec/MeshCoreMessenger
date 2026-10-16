# REGION10 — Запрос регионов одного репитера

[Stage REGION](stage-region.md) · [Общие контракты](region-design.md) · [Протокол](../../library/protocol/regions.md)

**Статус: выполнен 16.10.2026.** Задание подготовлено 16.10.2026.

**Цель:** Public RequestRegionsAsync поверх remote tracker.

**Зависимости:** REGION9. Проверить фактические результаты перед началом.
Рекомендуемый порядок последовательный: предыдущий шаг — [REGION9](region9-remote-runtime.md).
Выполнять только этот подэтап; завершение не запускает следующий автоматически.

**Файлы/область:** MeshCoreSharp/Client/, Models/ и typed fixtures REGION7.
Новые типы/файлы здесь — предлагаемое место, не утверждение об их существовании.
Сначала сверить код, git status и версии схемы/API; готовые механизмы использовать повторно.

## Минимальное чтение

Общий порядок — [правила исполнения REGION](stage-region.md#порядок-работы-агента).
Из [архитектуры](region-design.md) и [протокола](../../library/protocol/regions.md)
прочитать только перечисленные ниже разделы и указанные соседние контракты:

protocol/regions.md: «2. Запросить регионы конкретного репитера»; region-design.md: «Discovery в приложении».

## Шаги

1. Соединить CMD57/REGIONS, reply path и matcher, вернуть имена/remote clock с явной возможной неполнотой.
2. Принимать полный ключ и проверенный маршрут; не менять контакт/путь автоматически. Документировать implicit firmware addContact для неизвестного ключа.
3. Отличать transport/protocol failure, local ERROR и remote timeout; отсутствие ответа не превращать в Unsupported или пустой список.

**За границами шага:** Перебор репитеров, временные route mutations и SQLite.

## Проверки и завершение

**Обязательные проверки:** Известный zero-hop/direct, packed route, неизвестный ключ как явно переданный target, empty/partial CSV, unsupported/local table-full, final timeout и cancel.

**Готово:** Один адресный запрос работает без скрытой application-политики и без потери раннего ответа.

Если зависимость отсутствует или проверка не выполнена, записать точную причину и
оставить соответствующий пункт открытым. Для аппаратных/GUI проверок различать
автоматический результат, native просмотр и пользовательское подтверждение.
Не объявлять всю REGION завершённой по успеху одного слоя.

- [x] Изменения в границах подэтапа выполнены.
- [x] Обязательные проверки выполнены; команды и фактические результаты записаны.
- [x] Ограничения и оставшиеся проверки указаны.
- [x] Статус этого файла, checklist Stage REGION и current-state обновлены.

## Результат выполнения

Добавлены RequestRegionsAsync(fullKey,replyPath,ct) и overload с explicit responseTimeout1–60000ms. Encoder копирует target/path до ожиданий, CMD57 проходит общий MeshRequestGate → ScopeGate → CommandGate. Binary final связан в RX hook MSG_SENT; возвращаются names/tag/remote clock и IsPotentiallyIncomplete=true. Нет скрытых contact/route API, перебора репитеров или SQLite.

XML public API явно предупреждает: вызывающий обеспечивает direct outgoing route; replyPath задаёт обратный путь и не заменяет contact out_path; protocol13+ firmware может сама добавить явно переданный неизвестный полный ключ как zero-hop contact. Library не обещает read-only semantics для такого target. Полный preflight и route ownership остаются REGION22/23.

Проверка: dotnet run --project tests/MeshCoreSharp.Tests -c Release --no-restore — 222/222. Три новых cases: zero-hop/packed path, full target и отсутствие сопутствующих commands; empty/possibly-partial/private CSV; final push до transport completion; UnsupportedCommand/TableFull, final timeout, malformed CSV, cancel во время remote wait. Отсутствие final никогда не выдаётся за Unsupported или пустой список. Hardware не выполнялся.

Следующий шаг — [REGION11](region11-library-acceptance.md).
