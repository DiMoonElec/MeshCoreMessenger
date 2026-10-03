# D16 — UI ручного advert

[Stage D](stage-d.md) · **Статус: не начат.**

**Цель:** явное radio-действие без auto announcement.

**Scope:** небольшой блок Подключение (не глобальная панель), ZeroHop/Flood,
пояснение радиуса, Send advert; busy/accepted/error/unknown/offline preview.
Успех — принятие нодой, не доказанная видимость участникам. Выбор режима и открытие
вкладки не вызывают command.

**Не входит:** real advert, self identity/name/radio settings,
auto advert по connect/wake. Runtime action disabled.
**Зависимости:** D1 preview, D15; ConnectionSettingsView/AdvertisementMode.

**Тесты:** load/select/mode change → zero command; один simulated click;
availability/stale node/long labels, resources/bindings обеих тем.

**Готово:** manual-only блок готов к D17, root VM не растёт логикой.
**Ручная проверка: обязательна** — темы/narrow/wide/keyboard,
Flood warning/disabled offline.
