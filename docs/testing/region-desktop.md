# REGION — Settings, выбор региона канала и composer

Карточка поиска/каталога переработана после этого checkpoint. Актуальный сценарий
и результаты: [простой поиск регионов](region-simple-discovery.md).
Остальной текст сохраняет историческое описание первоначального UI.

17.10.2026. Компоновка согласована пользователем в REGION26; реализация REGION27–30.

## Поведение

- Settings: отдельная карточка default ноды (SET/CLEAR только по кнопке с readback)
  и карточка локального каталога с временным UI discovery. Добавление публичного,
  импорт приватного ключа, label rename, archive/restore и delete через RegionService.
- Под названием канала — действующий регион; наследование default помечено
  «как у ноды», offline cache явно отмечен. Unknown не превращается в unscoped.
- ⋯ → «Регион отправки» → inherit, unscoped или запись каталога. Назначение сохраняется
  по NodeId/ChannelId с CAS, не по mutable slot/name; история, draft и channel identity
  сохраняются. MessageService читает эту политику при отправке; UI не кодирует wire.
- Discovery explicit start/cancel, progress, source keys и per-target outcomes,
  разрешения на contact creation/temporary zero-hop сбрасываются после запуска.
  Найденные имена не сохраняются и не назначаются автоматически. Для `$…` требуется ключ.
  Automatic RF retries в GUI не добавлены: новый проход только по кнопке.
- Composer использует target-aware Core ReadBudgetAsync и перечитывает бюджет после
  post-commit изменения policy/default. Over-limit блокирует отправку без обрезки draft.
  Нового UI истории scope, per-contact region и «delivery to region» статуса нет.

## Проверки

Изолированная сборка (пользовательское Release-приложение держало DLL, не закрывалось):

```powershell
dotnet build tests/MeshCoreMessenger.Desktop.Tests -c Release --no-restore -p:UsedAvaloniaProducts= -o artifacts/regionuitests -v quiet
dotnet artifacts/regionuitests/MeshCoreMessenger.Desktop.Tests.dll -noLogo -parallelMode none
dotnet build tools/MeshCoreMessenger.ViewportAudit -c Release --no-restore -p:UsedAvaloniaProducts= -o artifacts/regionnative -v quiet
dotnet artifacts/regionnative/MeshCoreMessenger.ViewportAudit.dll --region-only
```

Release build0 warnings/errors. Targeted catalogue/default/discovery/bootstrap11/11;
channel menu/budget/stale readiness4/4; MainWindow+region/bootstrap60/60 после fix.
Первый полный Desktop425-case прогон дал424/425: новый region listener добавлял
лишнюю dispatcher работу до LoadAsync. Listener теперь пропускает pre-initialization
и отсутствующий region service; исходный тест supervisor publication снова проходит.
Повторный полный Desktop прогон: **425/425**, включая оба100000-message сценария. Финальный native audit4/4 также повторён после startup fix.

Новые интеграционные проверки: offline CRUD, invalid private key/form retention,
node isolation/key clearing, unknown/unsupported default без wire, consent и explicit
discovery save, late completion после смены node, header/local policy без потери
history/draft, бюджет100→50→100 при смене scope без trim/SEND.

Native fixture **4/4**: Light/Dark ×560/1040. Проверены Settings bounds, keyboard focus,
channel header, настоящий MenuFlyout и вложенные Header/Command bindings; выбор региона
из реального submenu сохраняется через Core. Данные временные, supervisor fake, ни
одного подключения к аппаратуре. Скриншоты: `artifacts/region-ui-native/`.

Действующие Core/library регрессии на предыдущем backend checkpoint: Core558/558,
library228/228. GUI использует эти публичные контракты без изменений протокола/схемы.

## Оставшаяся приёмка

Пользовательский просмотр нового UI пока не подтверждён. Эти native/fake проверки не
подменяют end-to-end GUI→CatCore radio test. Предыдущий отдельный физический sample
SEND `#test`/`ru-ros` и приём на другой ноде подтверждены пользователем; UI сам не
запускал аппаратные проверки, сообщения или discovery при открытии экрана.
REGION32/33 в целом остаются незавершёнными (reboot persistence, дополнительные
scoped/unscoped/private ACK и firmware autonomous replies).
