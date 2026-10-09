namespace QuickNotes.App.Helpers;

/// <summary>
/// Card open/expand copy for UX-C14. Double-click is not a command.
/// </summary>
public static class CardGestureCopy
{
    public const string Expand = "Развернуть";
    public const string Collapse = "Свернуть";
    public const string ExpandAutomationName = "Развернуть текст карточки";
    public const string CollapseAutomationName = "Свернуть текст карточки";
    public const string ExpandTooltip =
        "Показать полный текст карточки. Открытие заметки — Enter, отдельное окно — Ctrl+E.";
    public const string CollapseTooltip =
        "Свернуть текст карточки. Открытие заметки — Enter, отдельное окно — Ctrl+E.";
    public const string HeaderTooltip =
        "Один клик выбирает заметку; на узкой ширине сразу открывает её. Полный текст — кнопка «Развернуть».";
    public const string BodyTooltip =
        "Один клик выбирает заметку. Enter открывает её в режиме «Фокус», Ctrl+E — в отдельном окне.";
    public const string BoardBodyTooltip =
        "Один клик выбирает заметку. Enter открывает её в режиме «Фокус», Ctrl+E — в отдельном окне.";
    public const string ListHelp =
        "Один клик по карточке выбирает заметку. Enter открывает её в режиме «Фокус», Ctrl+E — в отдельном окне. Полный текст — кнопка «Развернуть», а не двойной клик.";

    public static string ButtonText(bool expanded) => expanded ? Collapse : Expand;

    public static string ButtonTooltip(bool expanded) => expanded ? CollapseTooltip : ExpandTooltip;

    public static string ButtonAutomationName(bool expanded) =>
        expanded ? CollapseAutomationName : ExpandAutomationName;
}
