using QuickNotes.App.Models;

namespace QuickNotes.App.Helpers;

/// <summary>
/// First-level origin copy for note tags (UX-C12). Technical details stay in tooltips.
/// </summary>
public static class TagOriginCopy
{
    public const string ManualMarker = "вручную";
    public const string AutoMarker = "авто";
    public const string SuppressedMarker = "скрыт";
    public const string RestoreAction = "Вернуть";
    public const string RestoreAutomationName = "Вернуть скрытый автотег";
    public const string SuppressedSectionTitle = "Скрытые автотеги";
    public const string ManualReason = "добавлен вручную";
    public const string RestoredAutoReason = "возвращённый автотег";

    public const string RequiredAllLabel = "Обязательные (все)";
    public const string AnyOfLabel = "Любые (хотя бы одно)";
    public const string ExcludedNoneLabel = "Исключения (ни одного)";

    public static string Marker(TagOrigin origin, bool isSuppressed = false)
    {
        if (isSuppressed)
        {
            return SuppressedMarker;
        }

        return origin == TagOrigin.Manual ? ManualMarker : AutoMarker;
    }

    public static string Tooltip(TagOrigin origin, bool isSuppressed = false)
    {
        if (isSuppressed)
        {
            return "Автотег скрыт и не будет назначаться при пересканировании. Можно вернуть.";
        }

        return origin == TagOrigin.Manual
            ? "Тег добавлен вручную и не снимается пересканированием."
            : "Тег назначен автоматически по тексту заметки.";
    }
}
