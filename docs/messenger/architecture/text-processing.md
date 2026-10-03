# Обработка исходящего текста

[Sending](sending.md) · [D2](../plan/d2-text-validation.md)

03.10.2026: по запросу пользователя добавлен промежуточный Core-модуль для
будущего уменьшения UTF-8 размера сообщения заменой одинаково выглядящих
кириллических букв латинскими. Сейчас реализован только passthrough.

Поток: исходный draft → `IOutgoingTextProcessor` → TransmissionText и Validation
→ composer/будущий prepare → библиотечный encoder. Protocol/library не выбирают
агрессивность и не изменяют текст по application settings. Они повторно проверяют
переданный body общим `TextMessageValidator`, который использует и модуль.

`Process(string text, OutgoingTextContext context, OutgoingTextOptions options)`
возвращает OriginalText, TransmissionText и typed Validation с UTF-8 размером,
максимальным бюджетом и ошибкой. Контекст различает private/channel; channel
получает фактический SelfInfo.Name текущей Online session. Неизвестное имя —
неизвестный бюджет, пустое имя — известный двухбайтовый префикс `": "`.

Reserved levels: Disabled, Conservative, Moderate, Aggressive. На всех уровнях
нынешний `PassthroughOutgoingTextProcessor` возвращает текст как есть: не trim,
не нормализует Unicode, не заменяет буквы/упоминания/пробелы, не режет строки.
Сам модуль не имеет client/store/settings зависимостей. Будущая настройка на
странице «Настройки» будет передавать options и инициировать пересчёт composer.
Реальная таблица замен/семантика уровней и persistence настройки не входят в D2.

Длина в UI всегда относится к TransmissionText и берётся из результата модуля;
самостоятельного подсчёта/дублирующей валидации в Desktop нет. Draft остаётся
исходным. Future MessageService должен захватить immutable result и передать
именно этот TransmissionText в durable prepare и API. Смена настройки после
prepare не должна изменять уже сохранённое сообщение/данные ACK. Отображение
original vs transmission при настоящих заменах требует отдельного решения.

Имя ноды, public keys, channel fingerprint, slot binding и прочая identity
не оптимизируются. Placeholder не требует БД, радио, нового library assembly
или сетевых команд. Preview может выбирать уровни, не притворяясь работающей
оптимизацией и не создавая session.

Будущая доработка по запросу пользователя 03.10.2026: composer должен обрезать
лишние символы при превышении лимита ввода. Размер определяется через этот
processor по TransmissionText; Unicode границы и неизвестный/меняющийся бюджет
нужно учесть в UI. Сейчас заглушка и validator текст не обрезают.
Подробности требования — в [D2](../plan/d2-text-validation.md#требование-для-будущей-доработки-composer).
