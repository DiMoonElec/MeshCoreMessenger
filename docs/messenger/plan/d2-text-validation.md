# D2 — общий UTF-8 контракт

[Stage D](stage-d.md) · **Статус: выполнен. Автоматические проверки и ручная
проверка пользователем подтверждены 03.10.2026.**

**Цель:** UI и encoder используют одни правила.

**Scope:** маленький pure helper в существующей MeshCoreSharp.dll; подсчёт UTF-8,
лимит private/channel с фактическим SelfInfo.Name текущей session, валидация пустого
текста/NUL/некорректного UTF-16. Encoder переиспользует его без изменения wire bytes.
Реальный счётчик/полевые ошибки в Composer, availability по Online, node/адресат/slot
readiness; actual transmit недоступен до D5/D6. Отдельные причины offline,
неразрешённого адресата и превышения лимита.

**Не входит:** разбивка/обрезка, offline send queue, outgoing storage.
Draft редактируется offline и сохраняется без ограничения wire лимитом.
**Зависимости:** D1; sending architecture, CompanionCommands.EncodeText/ProtocolLimits.

**Тесты:** 159/160/161 UTF-8 bytes; Cyrillic/emoji/surrogates/NUL/empty; prefix budget
канала/слишком длинное имя; byte-identical encoder и прежние exceptions;
пересчёт при смене session/name/чата; offline draft сохраняется целиком.

**Готово:** нет двух валидаторов, UI сообщает точный бюджет и причину запрета.
**Ручная проверка: обязательна** — байтовые границы, emoji, Public/Private, offline,
темы. Preview/fake Online не открывает соединения.

## Дополнение пользователя: обработка исходящего текста

До общего валидатора добавлен application-модуль для будущей оптимизации размера
заменой похожих кириллических букв латинскими. `о`/`р` занимают по 2 UTF-8 байта,
`o`/`p` — по 1. Возможный выигрыш равен числу таких замен, но реальная таблица
замен и её пригодность для шрифта/упоминаний ещё не утверждены.
В D2 реализована только заглушка, одинаково сохраняющая текст на всех уровнях.

- Core `IOutgoingTextProcessor.Process(text, context, options)` возвращает
  `ProcessedOutgoingText`: OriginalText, TransmissionText, Validation.
- `OutgoingTextOptions` принимает Disabled/Conservative/Moderate/Aggressive;
  значения зарезервированы для будущей настройки приложения. Сейчас все no-op.
- Composer получает длину/лимит/ошибки исключительно из результата этого модуля.
  Сам модуль использует общий библиотечный валидатор. Editor не заменяет текст.
- Будущая отправка должна захватить TransmissionText вместе с результатом
  обработки до durable prepare; настройки не должны изменять уже сохранённый
  текст попытки. Нельзя повторно оптимизировать текст внутри encoder.
- Селектор уровня присутствует в изолированном preview для проверки API.
  Настройка настоящей страницы «Настройки»/её persistence и алгоритм замен остаются
  будущей отдельной задачей, как запрос пользователя о модуле-заглушке.

Контракт: [обработка исходящего текста](../architecture/text-processing.md).

## Реализовано

- Публичный pure `TextMessageValidator` в единственной MeshCoreSharp.dll и typed
  результат в Models. Строгий UTF-8, без trimming/normalization/разбиения; пустой
  текст, NUL, malformed UTF-16 и превышение отвергаются. Неизвестный бюджет
  возвращает длину текста, но не утверждает пригодность для отправки.
- Encoder использует тот же helper. Сохранены порядок/типы exceptions, wire bytes
  и прежний UTF-8 fallback при подсчёте именно sender name. Имя считается целиком,
  включая пробелы; message body по-прежнему кодируется строго.
- Composer считает байты на каждом вводе и изменении context/options. Пустое поле
  показывает 0 без красной ошибки, invalid UTF-16 — `— / 160 байт` с объяснением;
  NUL/превышение — отдельные пояснения. Offline текст любой длины сохраняется
  прежним draft owner целиком. Несколько причин разделены `•`.
- Для канала sender name берётся из Online session SelfInfo через read-only
  snapshot metadata, не LastName/display fallback из БД. Metadata не добавляет
  новых state publication points; вне Online очищается. Неизвестное имя даёт
  `42 / — байт`, исчерпанный именем бюджет — 0 с объяснением вместо отрицательного.
- Core `SendReadinessReader` читает committed directory: actual node/Online/session,
  текущий Chat contact, коллизии 6-byte prefix среди всех типов, active slot binding.
  Несколько slots одного fingerprint требуют явного выбора; первый не выбирается.
  Это UI-readiness, окончательный admission перед wire остаётся D4–D6.
- Небольшой Desktop `ComposerContextCoordinator` на workspace отслеживает
  selection/node и существующую root publication supervisor, защищает late reads
  revision и отменяет/ждёт собственные чтения при shutdown. Offline bootstrap
  не требует запущенного UI dispatcher. MainWindowVM вырос только на композицию
  двух owners и lifecycle hooks; алгоритмы валидации/readiness туда не перенесены.
- CanSend остаётся false: при готовом тексте строка может содержать только счётчик,
  tooltip кнопки объясняет подключение отправки в D5/D6. Core/schema outgoing,
  реальные радио-команды и D3–D18 не реализовывались.

## Проверки

- Solution/preview Debug и Release: 0 warnings/errors.
- MeshCoreSharp: 96/96, Core: 139/139, Desktop: 256/256 в Debug/Release.
- Библиотека: 159/160/161 ASCII/Cyrillic/emoji, пустой/NUL/invalid UTF-16,
  пробелы/новые строки, неизвестный/исчерпанный бюджет; сравнение payload и точных
  exception types/parameter names со старым encoder.
- Core: no-op на всех уровнях, неизменность original/transmission, unknown vs empty
  sender name; временная настоящая SQLite — removed contact, Chat/non-Chat collision,
  same fingerprint в нескольких slots, stale/removed binding, offline/wrong node;
  session name очищается при disconnect и обновляется при следующей session.
- Desktop: processor output является источником счётчика, draft остаётся оригинальным,
  полные offline черновики, public/private independence, live name/session changes,
  late readiness после offline не применяется, shutdown отменяет owned reads;
  существующие bootstrap/shutdown/history/copy/mentions regression suites.
- Native preview: Light/Dark × 420/960 DIP × обе вкладки; реальный TextBox обновляет
  счётчик, name/level changes, offline неизвестный канальный бюджет, bindings/resources,
  disabled Send, menu cancel/confirm и тот же пузырёк. Нет Binding warnings/errors.
- Native viewport audit сохранён; `git diff --check` и относительные ссылки проверены.

## Ручная приёмка

03.10.2026 пользователь проверил результат и подтвердил: «пока что все норм».
Сценарий ниже сохранён для повторных проверок; подтверждение не означает
аппаратных испытаний или отдельного отчёта по каждому пункту.

```sh
dotnet run --project tools/MeshCoreMessenger.SendUiPreview -c Debug
```

1. В личном чате проверить счётчик на кириллице/emoji и 159/160/161 байтах. Длинный
   текст не обрезается; после переключения вкладки черновик остаётся.
2. Demo Online задаёт только presentation, без session/transport. В канале изменить
   имя ноды, включая пробелы/emoji: бюджет изменяется; offline даёт неизвестный лимит.
3. Empty input не показывает ошибку. Превышение/несколько причин/readiness и ошибка
   draft видны в прежней строке, обе темы/narrow/wide. Кнопка Send выключена.
4. Переключить уровни заглушки: текст и byte count не меняются. Это ещё не
   работающая оптимизация и не настройка настоящего приложения.
5. В обычном запуске проверить полный offline draft, при разрешённом обычном
   подключении — бюджет фактической ноды. Тестовые передачи не требуются.

## Требование для будущей доработки composer

03.10.2026 пользователь запросил: **при превышении лимита в текстовом поле
обрезать лишние символы**. Это отложенное изменение UI, в текущем D2 обрезка
не реализована. Оно уточняет будущий контракт ввода, сохраняя результаты D2.

При реализации ограничение должно опираться на UTF-8 размер TransmissionText
из обработчика, включая актуальный канальный бюджет, а не на число C# char.
Обрезка должна корректно работать с вводом/вставкой и Unicode: не разрывать
surrogate pairs и составные emoji/графемы. Поведение при неизвестном offline
канальном бюджете, уменьшении лимита после смены ноды/настройки и загрузке уже
сохранённого длинного черновика нужно определить отдельно. Сам библиотечный
валидатор/encoder по-прежнему не должны молча обрезать переданный текст.
