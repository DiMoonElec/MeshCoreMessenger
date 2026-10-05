# P5 — known-route retries и fallback flood

Дата: 05.10.2026. Проверки выполнялись через TCP Companion emulator;
аппаратную приёмку выполняет пользователь. [План](../messenger/plan/d7-private-auto-retry.md).

Production coordinator выбирает бюджет в момент начала job по актуальному маршруту:
3 TX для flood, 5 для known. После трёх known timeout выполняется ровно один owned
ResetPath, typed GetContact readback и сохранение локального маршрута. Перед первой
fallback-передачей readback должен подтвердить flood. Ручной reset использует тот же
внутренний readback helper и сохраняет своё поведение частичного успеха.

Default wire schedule: T1/attempt 0,1,2 → reset → T2/attempt 0,1, где T2 > T1.
NewTimestampResetAttempt: T1/0, T2/0, T3/0 → reset → T4/0, T5/0.
Один MessageId/пузырёк, пять отдельных durable attempts; progress 1/5…5/5.
Новый timestamp может создать дополнительное сообщение у получателя: exactly-once
для fallback не обещается.

Проверено:

- direct и многобайтовые маршруты: три known TX, затем два flood, правильные
  timestamps/attempt/ExpectedAck и captures в БД, прежний пузырёк;
- ACK первой known-попытки во время первой/третьей/четвёртой передачи останавливает
  job, успешный candidate остаётся known;
- ACK перед reset предотвращает mutation; ACK во время readback предотвращает
  первый flood TX, Delivered сохраняется, локальный маршрут обновляется;
- отказ reset, отказ readback, ошибка локального route commit: нет flood TX,
  job прекращается, SQL recovery не повторяет reset; DB ошибка включает pause;
- known readback после принятого reset не разрешает fallback;
- отмена во время reset не запускает flood, guard удерживается до завершения job;
- смена известного пути используется следующей known-попыткой, исходный путь
  не восстанавливается через изменение Contacts;
- learned known-путь во время flood без ACK сбрасывается перед следующим TX;
- смена режима между GetContact и TX сохраняет фактический MSG_SENT flag и
  останавливает job с Unknown, вместо продолжения неверно помеченной фазы.

Тайм-ауты production не уменьшались. Короткие интервалы настроены только в тестовом
TCP профиле. Схема v5 и библиотечный one-TX API не менялись. P6 (read API истории и
обогащение provenance), P7 (incoming dedup) и P8 (совместная приёмка) ещё впереди.

Release build: 0 warnings/errors. Library 105/105, Core 345/345, Desktop 318/318.

Аппаратная проверка пользователем: 05.10.2026 подтверждены автоматические попытки
и обмен с ближней нодой. Проверка дальней ноды пока неполная: не установлено,
вызвана ли неудача прохождением радио или ошибкой. Нужны подробные ручные тесты
с записью маршрутов, попыток, ACK и радиоактивности; успешную дальнюю доставку
на этом checkpoint не считать подтверждённой.
