# Ручной сброс маршрута приватного чата — 04.10.2026

По запросу пользователя реализован второй этап после создания разных меню.
Первый этап закоммичен `a4b38af`, пользователь подтвердил, что меню работает.
D7 и остальные незавершённые подэтапы этим изменением не начинаются.

## Реализация

Private menu содержит рабочий пункт «Сбросить маршрут»; Public этого пункта не имеет.
Состав меню/command wiring по-прежнему создаются при инициализации workspace.
Нажатие фиксирует NodeId/SessionId/Generation и полный ключ контакта. Offline,
unknown prefix-only, отсутствующий в справочнике или служебный контакт недопустимы.
CanExecute/tooltip проверяются при открытии меню, selection/session changes
инвалидируют подготовленную цель; поздний availability result игнорируется.

Library `MeshCoreClient.ResetPathAsync` кодирует `0D | publicKey[32]` и использует
существующий serialized dispatcher, subscribe-before-send и OK/ERROR matcher.
Аргумент копируется до первого ожидания, CancellationToken проходит до транспорта.
Core ICompanionClient/adapter/SessionCommandLease используют этот публичный API.
Новых библиотечных сборок нет.

ContactRouteService первоначально выполнял команду, полный GetContacts readback и
точечную запись маршрута в одном owned lease.RunAsync: API чтения одного контакта
ещё отсутствовал, а since-фильтр неприменим, поскольку reset не увеличивает lastmod.
04.10.2026 readback заменён на GetContactAsync; добавлена
[синхронизация по PATH_UPDATED](live-contact-routes.md).
Обновляется только целевой контакт, канальные bindings,
остальные контакты, история, попытки и черновики не меняются. Успешная запись
обновляет UI через существующие node-scoped projections; target не перенаправляется
при смене переписки. Все UI workflows отслеживаются MainWindow lifetime.

ConversationOperationGuard дополнен BeginExclusive; BeginClear использует его же.
Сброс блокируется на протяжении send/ACK workflow этому адресату, начиная до Prepare.
Во время reset/readback/commit новая отправка или очистка этому адресату не допускается.
Другие контакты не блокируются этим guard; immediate-команды имеют общий dispatcher.

При OK и ошибке readback/SQL сообщается, что маршрут сброшен, но локальные данные
не обновились. При ERROR — отказ ноды; при timeout/disconnect — подтверждения сброса
нет. Автоматических повторов, replay или повторной отправки сообщения нет.

## Маршрут в SQLite и UI

Миграция **v4** добавляет nullable Contacts.OutPathLength и RouteObservedUtc.
Существующий startup создаёт backup перед миграцией. Старые записи получают NULL:
это «данные не получены», а не direct или flood. Дескриптор проходит через
DirectoryContactSnapshot/ContactRecord/ContactDetailsProjection. Валидируются
reserved mode и размер используемой части пути. Поле OutPath сохраняется целиком.

FF — неизвестный маршрут/flood; 0 — direct без ретрансляторов. Остальные значения
декодируются как число хешей в младших шести битах и размер хеша в старших двух.
Подпись переписки показывает действующий префикс пути, а не все 64 байта.
DeviceDetails.RawRoute остаётся диагностическим полным raw field.

Время наблюдения фиксируется в UTC перед чтением контактов. Более старый snapshot
не перезаписывает свежий route readback; результат ApplySnapshot также возвращает
реально сохранённый маршрут. Новый snapshot может заменить его маршрутом,
выученным нодой. Точечный update проверяет node/session и незавершённую session.

## Семантика и ограничения

ResetPath не создаёт RF сообщение. Нода выбирает flood при неизвестном маршруте;
return-path (в том числе с вложенным ACK) может снова установить direct route.
Это не постоянный режим принудительного flood. Typed PATH_UPDATED/readback по push
в этом этапе не добавлены: локальная подпись отражает последний прочитанный снимок;
последующие изменения маршрута нодой будут видны после обновления справочника/reconnect.
Сверка wire handler выполнена с [текущим Companion firmware](https://github.com/meshcore-dev/MeshCore/blob/main/examples/companion_radio/MyMesh.cpp);
целевая аппаратная версия пока не проверена.

## Проверки

- Library Release **97/97**, включая полный ключ/copy, размер, interleaved advert,
  ERROR/NOT_FOUND, timeout/recovery, cancellation и отсутствие retries.
- Core Release **253/253**, добавлено 10 cases. Production library/client/session/
  transport через loopback TCP: direct descriptors 0/42/82 → reset → FF → обычная
  private send с flood descriptor и ACK; reset передаёт ноль сообщений.
  Проверены pending ACK, stale session/generation, offline, missing contact,
  отказ ноды, readback/SQL failure, copied target и exclusive guard до commit.
- Store: старый snapshot не восстанавливает путь после сброса, новый learned snapshot
  обновляет путь; проверены полный ключ, invalid descriptor и чужая session.
  Legacy migration сохраняет сообщения и NULL route metadata. Тестовые downgrade
  fixtures outgoing migration теперь удаляют также v4 columns/metadata.
- Desktop Release **305/305**, добавлено 11 cases: offline/ACK, late availability,
  target capture/selection switch, busy/double invoke, partial success/UI refresh failure,
  формат encoded descriptor и stale path bytes.
- Native `--route-reset-only`: **4/4** Light/Dark × 420/960. Production menu/commands/
  projections, имитация route service, временная SQLite: вызов ровно один раз,
  «Маршрут сброшен», flood metadata, история и draft сохранены.
  Screenshots: `$TMPDIR/meshcore-route-reset/`, Light 420 просмотрен.
- Native `--history-clear-only`: **8/8** Public/Private × Light/Dark × 420/960,
  cancel/confirm/draft, приватный reset disabled offline. Доступность проверяется
  через IsEffectivelyEnabled (учитывает CanExecute), не локальный IsEnabled.
- Debug/Release solution builds: 0 warnings, 0 errors; `git diff --check` пройден.
- Аппаратная нода и пользовательская БД не использовались.

В первом Core прогоне одновременно с библиотечным runner снова не прошёл существующий
HistoryPagingTests.CancellationInterruptsAnActiveSqliteSearch: ожидалась отмена,
запрос завершился. Последующие отдельные полные Core прогоны прошли 253/253.
Этот тест и DatabaseReader не изменялись; исходный сбой не скрывается успешным повтором.

## Ручная аппаратная приёмка

04.10.2026 пользователь подтвердил: «Функционал работает». Подробный аппаратный
лог не предоставлен; это подтверждение работоспособности, не заявка на полный
прогон всех перечисленных ниже сценариев.

Пользователь: личный Chat контакт с известным неработающим маршрутом →
«Сбросить маршрут» → дождаться сообщения об успехе → отправить новое ЛС → проверить
получение и ACK. Проверить недоступность reset при ожидании ACK и offline, другой
контакт, переподключение и обе темы. Сам reset не должен передавать текст или advert.
