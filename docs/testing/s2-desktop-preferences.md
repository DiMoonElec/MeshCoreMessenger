# S2 — настройки Desktop и фоновое сохранение

[План S2](../messenger/plan/s2-desktop-preferences.md) · [Ветка S](../messenger/plan/stage-s.md)

07.10.2026: **S2 реализован; автоматическая и macOS native части проверены.
Пользователь подтвердил визуальную приёмку («Визуально все ОК»).**
Платформа пользовательской проверки не уточнена; отдельная Windows приёмка не заявляется.

## Поведение и границы

После темы в ApplicationSettingsView добавлены карточки «Поведение окна» и
«Уведомления». Выбор крестика: «Сворачивать в трей» (default) / «Закрывать приложение».
Два checkbox уведомлений для личных сообщений/каналов по умолчанию включены.
Они принимают и сохраняют выбор уже сейчас; подсказки обозначают недоступность
трея и системных уведомлений до следующих этапов. Крестик пока завершает приложение.

DesktopPreferences расширен отдельными revisions и тремя ключами Settings:
`desktop.close-behavior`, `desktop.notifications.private`, `desktop.notifications.channels`.
Нет миграции БД/новых зависимостей; прежние theme/window-placement сохранены.
Для старых/повреждённых значений используются независимые defaults.

Явные изменения темы и новых preferences сохраняются через tracked async FlushAsync
во время работы UI. Прежний flush gate и DatabaseWorker сериализуют записи.
Geometry остаётся dirty до ближайшего flush/штатного выхода, а не пишет на каждый resize.
StopAsync отменяет фоновые saves и ждёт принятых задач; финальный preferences barrier
сохраняет оставшиеся dirty values. История/unread/drafts и connection policy не изменены.

Settings failure сохраняет последний выбор в памяти, показывает отдельную ошибку
с кнопкой «Повторить сохранение». Ошибка не перезаписывает общий ErrorMessage.
Callback каждого save проверяет текущий IsPaused; перестановка старого/нового
completion не даёт stale failure/success. Retry — только local persistence.

## Автоматические проверки

- Debug/Release builds — **0 warnings/errors**; native audit build также чистый.
- Desktop regression suite — **354/354 в Release и Debug**, failures/skips=0.
- Девять новых cases: independent defaults/invalid input, изменение во время blocked
  flush, partial failure/latest retry, canceled save/final barrier, save до выхода,
  отдельная ошибка/retry, два порядка late UI completions, stop/cancellation.
- Существующие tests дополнены сохранением всех трёх параметров через reload и
  настоящий SQLite close/reopen; проходят старые theme/geometry/shutdown и S1 regressions.
- Root saves и retry не вызывают connect/disconnect; quiesced setters не меняют выбор.
- Core/library production code и schema не менялись; их suites повторно не запускались
  для S2. Полный Desktop suite использует реальные Core stores в интеграционных tests.

```sh
dotnet build MeshCoreSharp.sln -c Release --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false
dotnet build MeshCoreSharp.sln -c Debug --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false
dotnet tests/MeshCoreMessenger.Desktop.Tests/bin/Release/net10.0/MeshCoreMessenger.Desktop.Tests.dll -noColor
dotnet tests/MeshCoreMessenger.Desktop.Tests/bin/Debug/net10.0/MeshCoreMessenger.Desktop.Tests.dll -noColor
```

Полный Desktop suite включает S1 local IPC; в sandbox Unix socket bind запрещён,
поэтому успешные полные прогоны выполнены с доступом к локальным sockets.
Фокусные preferences/view-model/shutdown tests проходят также без этого доступа.
Все данные тестовые, аппаратные подключения не выполнялись.

## Native macOS

macOS arm64, Avalonia 12.1.3; CoreGraphics/CVDisplayLink preflight успешен.
Production MainWindow/SettingsView, временная SQLite и fake supervisor.

```sh
dotnet build tools/MeshCoreMessenger.ViewportAudit -c Release --no-restore
dotnet tools/MeshCoreMessenger.ViewportAudit/bin/Release/net10.0/MeshCoreMessenger.ViewportAudit.dll --settings-only
```

Light/Dark × **560/960 px**: реальные ComboBox/CheckBox меняют VM и сохраняются
через background workflow до закрытия. Tab переводит фокус от close behavior
к private checkbox; Space переключает его и сохраняет новое значение.
Controls не выходят за ширину страницы; error/retry доступен, recovery убирает
только settings error. ConnectCalls/DisconnectCalls=0.

Первоначальный запрос ширины 420 был ограничен production MinWidth=560; итоговый
audit использует фактические 560/960 и проверяет ClientSize. Ограничения окна не менялись.
Снимки сохраняются в `$TMPDIR/meshcore-s2-settings/`: Light/Dark-560/960.png и
Dark-960-save-error.png. Макеты осмотрены; runtime audit не заменяет пользовательскую
оценку подписей/удобства и отдельную Windows приёмку.

## Ручная проверка

- [ ] Открыть Settings: тема, затем поведение окна, затем два checkbox уведомлений.
- [ ] Новая/старая папка без ключей: default «Сворачивать в трей», оба checkbox включены.
- [ ] Изменить тему/крестик/оба checkbox, перейти на другой экран и вернуться:
  последний выбор сохраняется, история/черновик остаются прежними.
- [ ] Закрыть штатно и запустить снова: выбранные значения восстановлены.
- [ ] Light/Dark, минимальная/широкая ширина, вертикальная прокрутка;
  Tab/Shift+Tab и Space для checkbox, keyboard choice ComboBox.
- [ ] Отдельные папки имеют независимые настройки.
- [ ] Трей/уведомления действительно ещё не включены; соответствующие подсказки понятны.
- [ ] Windows: те же сценарии на обычной пользовательской сессии; указать ОС/пакет
  и результаты. Эту платформу агент в S2 не запускал.

Persistence error/retry проверены fault injection на временной БД; для ручной UI-приёмки
не нужно повреждать или ограничивать пользовательское хранилище.
