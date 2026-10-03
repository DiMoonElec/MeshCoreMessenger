# Главные риски и обязательные инварианты

[Оглавление](../../UI_COMPONENTS_AND_REDESIGN_ANALYSIS.md) · [Маршрутизация чтения](../../README.md)

## Главные риски и обязательные инварианты

1. **Ложное чтение скрытого чата.** До ухода в Settings/Devices сообщить history
   inactive/невидимый viewport. Повторно сообщить актуальную видимость после
   layout при возвращении. Прежний `_isWindowActive` не должен оставаться true;
   новые commits в скрытом чате не погашают unread.
2. **Потеря scroll/anchor.** Пересозданный visual list теряет offset; queued scroll
   request может попасть в уже скрытый view. Сохранять stable sequence и offset,
   ограничивать callbacks текущим контекстом, возвращаться к anchor после layout.
   Сохранять существующий предел 500 DTO, auto-scroll только у фактического конца.
3. **Разная семантика screen и conversation.** Settings не является четвёртым
   `MessengerNavigationTab`, принадлежащим ноде. Shell section и последняя
   node-scoped вкладка/запись — разные состояния. Возврат из Settings сохраняет
   диалог, search и draft. После уточнения UI6 Channels/Personal также сохраняют
   независимые presentation состояния: два фиксированных workspace, не кеш всех переписок.
4. **Черновик.** Уход с экрана не теряет dirty revision и не меняет его owner;
   смена NodeId/conversation по-прежнему выполняет безопасный flush. Connect из
   Settings может определить другую ноду: прежний draft остаётся в прежней истории.
5. **Подписки и shutdown.** Attach/detach не дублируют scroll/activation/commit
   handlers. Закрытие на любой странице сохраняет ingress/session/read/draft/
   preferences в прежнем порядке. Скрытые VM также quiesce при полном shutdown.
6. **Ownership данных.** ProfileId описывает транспорт, NodeId определяется по
   полному ключу каждой session. Все lists/details/search/drafts node-scoped;
   поздние результаты A не меняют B. UI продолжает обновляться после commit.
7. **Keyboard и доступность.** Cmd/Ctrl+F адресуется видимому workspace, не скрытому
   TextBox. Escape возвращает из Settings на прежний screen; Back в narrow сначала
   возвращает к списку. Draft/IME owns editing keys; Enter не отправляет до D.
8. **Errors и секреты.** Shell показывает persistent storage/shutdown failures
   независимо от screen; успешная загрузка истории не скрывает несохранённые данные.
   Settings пока не получает редактор channel secrets; устройство отображает
   public identity и существующие secret-free projections.
