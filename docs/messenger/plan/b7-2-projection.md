# B7.2 — состояние подключения и commit-driven UI (выполнено 28.09.2026)

[Оглавление](../../MESSENGER_PLAN.md) · [Маршрутизация чтения](../../README.md)

##### B7.2 — состояние подключения и commit-driven UI (выполнено 28.09.2026)

- Проецировать immutable supervisor snapshot, выбранный профиль, причину и время
  retry в MainWindow; добавить минимальные Connect/Disconnect действия и применение
  `SwitchProfile` после явной смены профиля.
- `MessageCommitted` обрабатывать только через UI dispatcher и перечитывать SQLite
  после commit; объединять частые обновления и отписываться при остановке ViewModel.

Тесты B7.2: отображение состояний; UI-thread dispatch; profile switch; отсутствие
обновления до commit; обновление истории после commit; поздние callbacks после stop.

Реализовано: `MainWindowViewModel` проецирует immutable snapshot supervisor во все
восемь пользовательских состояний, показывает причину и время следующей попытки,
а также предоставляет минимальные команды Connect/Disconnect. После явного
`SaveAndSelect` профиль передаётся supervisor через `SwitchProfileAsync`; выбор в
редакторе сам по себе подключения не меняет.

Desktop подписывается только на post-commit событие `MessageIngestor` через узкую
notification-границу. Фоновый coalescing worker после сигнала перечитывает историю
через `ILocalHistoryReader` и применяет коллекции через UI dispatcher. При остановке
обе подписки снимаются до отмены worker; уже поставленные и поздние callbacks не
могут изменить остановленную ViewModel. Прямого чтения `CompanionSession.Events` и
автоматических protocol mutations эта проекция не добавляет.

Проверено: полный Release build без предупреждений; Core tests — 94/94, Desktop —
40/40, MeshCoreSharp regression suite — 92/92. Fake-тесты покрывают точное
отображение всех состояний, обязательный UI dispatch, Connect/Disconnect, profile
switch, отсутствие reload для не вставленного дубликата, перечитывание истории после
commit, объединение частых commit-сигналов и игнорирование callbacks после stop.
Физическая нода не использовалась.
