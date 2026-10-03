# D2 — общий UTF-8 контракт

[Stage D](stage-d.md) · **Статус: не начат.**

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
