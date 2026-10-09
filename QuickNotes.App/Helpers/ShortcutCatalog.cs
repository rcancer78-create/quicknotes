using System;
using System.Collections.Generic;
using System.Windows.Input;
using QuickNotes.App.Models;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace QuickNotes.App.Helpers;

/// <summary>
/// Single source of truth for in-app editor shortcuts, help text, and OCR conflict checks.
/// Global capture hotkeys remain user-configurable in settings; their defaults live in <see cref="AppSettings"/>.
/// </summary>
public static class ShortcutCatalog
{
    public const string BoldGesture = "Ctrl+B";
    public const string ItalicGesture = "Ctrl+I";
    public const string HeadingGesture = "Ctrl+Shift+H";
    public const string HistoryGesture = "Ctrl+H";
    public const string CodeGesture = "Ctrl+Shift+C";
    public const string BulletListGesture = "Ctrl+Shift+U";
    public const string NumberedListGesture = "Ctrl+Shift+O";
    public const string CheckboxGesture = "Ctrl+Shift+X";
    public const string MarkdownLinkGesture = "Ctrl+K";
    public const string SaveGesture = "Ctrl+S";
    public const string PreviewGesture = "Ctrl+P";
    public const string FindGesture = "Ctrl+F";
    public const string FocusEnterGesture = "Enter";
    public const string FocusEscapeGesture = "Esc";
    public const string FullscreenGesture = "F11";
    public const string NavigationToggleGesture = "F4";

    public const string FullscreenToolbarTip = "Полноэкранный режим (F11)";
    public const string FullscreenExitToolbarTip = "Выйти из полноэкранного режима (F11, Esc)";
    public const string NavigationToggleToolbarTip = "Переключить панель навигации (F4)";

    public const string BoldToolbarTip = "Жирный текст (Ctrl+B)";
    public const string ItalicToolbarTip = "Курсив (Ctrl+I)";
    public const string HeadingToolbarTip = "Заголовок (Ctrl+Shift+H)";
    public const string CodeToolbarTip = "Код (Ctrl+Shift+C)";
    public const string BulletListToolbarTip = "Маркированный список (Ctrl+Shift+U)";
    public const string NumberedListToolbarTip = "Нумерованный список (Ctrl+Shift+O)";
    public const string CheckboxToolbarTip = "Список задач (Ctrl+Shift+X)";
    public const string MarkdownLinkToolbarTip = "Markdown-ссылка (Ctrl+K)";
    public const string HistoryToolbarTip = "Показать или скрыть историю версий (Ctrl+H)";

    public const string DefaultOcrKey = "R";
    public const string DefaultOcrGesture = "Ctrl+Shift+R";

    public static readonly ShortcutSpec DefaultOcr = new(true, true, false, false, "R", "Снимок экрана и распознавание текста");

    public static readonly ShortcutSpec[] EditorReserved =
    {
        new(true, false, false, false, "B", "жирный текст"),
        new(true, false, false, false, "I", "курсив"),
        new(true, false, false, false, "K", "ссылка"),
        new(true, false, false, false, "H", "история версий"),
        new(true, false, false, false, "S", "сохранение заметки"),
        new(true, false, false, false, "P", "предпросмотр Markdown"),
        new(true, false, false, false, "F", "поиск"),
        new(true, false, false, false, "E", "открытие заметки в окне"),
        new(true, false, false, false, "N", "новая заметка"),
        new(true, false, false, false, "Return", "сохранение заметки"),
        new(true, false, false, false, "Enter", "сохранение заметки"),
        new(true, true, false, false, "H", "заголовок Markdown"),
        new(true, true, false, false, "C", "код"),
        new(true, true, false, false, "U", "маркированный список"),
        new(true, true, false, false, "O", "нумерованный список"),
        new(true, true, false, false, "X", "список задач"),
        new(true, true, false, false, "E", "импорт и экспорт")
    };

    public static string EditorShortcutsSummary
    {
        get
        {
            return "В редакторе: " +
                   $"{BoldGesture} жирный, {ItalicGesture} курсив, {HeadingGesture} заголовок, " +
                   $"{HistoryGesture} история, {CodeGesture} код, {BulletListGesture} маркированный список, " +
                   $"{NumberedListGesture} нумерованный список, {CheckboxGesture} задачи, {MarkdownLinkGesture} ссылка.";
        }
    }

    public static string BuildHelpHotkeysParagraph(string ocrGesture)
    {
        return "Ctrl+N — новая заметка. Ctrl+F — поиск. " +
               $"{ocrGesture} — снимок экрана и распознавание текста (настраивается в разделе «Основные»). " +
               $"{NumberedListGesture} — нумерованный список в редакторе. " +
               $"{HeadingGesture} — заголовок Markdown. {HistoryGesture} — история версий. " +
               "Ctrl+Enter во «Входящих» — обработано и следующая. Enter в списке открывает выбранную заметку, Ctrl+E — в отдельном окне. " +
               $"{FocusEnterGesture} на доске открывает заметку в режиме «Фокус», {FocusEscapeGesture} возвращает назад. " +
               $"{FullscreenGesture} — полноэкранный режим. {NavigationToggleGesture} — переключение панели навигации. " +
               "F5 — синхронизация. F1 — эта справка. Alt+M — меню «Ещё». " +
               "Глобальные сочетания для захвата текста настраиваются в разделе «Основные».";
    }

    public static bool ConflictsWithEditorReserved(bool ctrl, bool shift, bool alt, bool win, string? key, out string? reservedName)
    {
        reservedName = null;
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        foreach (var spec in EditorReserved)
        {
            if (spec.Matches(ctrl, shift, alt, win, key))
            {
                reservedName = spec.DisplayName;
                return true;
            }
        }

        return false;
    }

    public static bool ConflictsWithEditorReserved(AppSettings settings, out string? reservedName)
    {
        return ConflictsWithEditorReserved(
            settings.OcrHotkeyCtrl,
            settings.OcrHotkeyShift,
            settings.OcrHotkeyAlt,
            settings.OcrHotkeyWin,
            settings.OcrHotkeyKey,
            out reservedName);
    }

    public static string FormatGesture(bool ctrl, bool shift, bool alt, bool win, string key)
    {
        var parts = new List<string>();
        if (ctrl) parts.Add("Ctrl");
        if (shift) parts.Add("Shift");
        if (alt) parts.Add("Alt");
        if (win) parts.Add("Win");
        parts.Add(string.IsNullOrWhiteSpace(key) ? DefaultOcrKey : key);
        return string.Join("+", parts);
    }

    public static bool IsModifierChord(KeyEventArgs e, bool ctrl, bool shift, bool alt, Key key)
    {
        bool isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        bool isShift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        bool isAlt = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;
        var pressed = e.Key == Key.System ? e.SystemKey : e.Key;
        return isCtrl == ctrl && isShift == shift && isAlt == alt && pressed == key;
    }

    public static bool IsNumberedList(KeyEventArgs e) => IsModifierChord(e, ctrl: true, shift: true, alt: false, Key.O);

    public static bool IsHeading(KeyEventArgs e) => IsModifierChord(e, ctrl: true, shift: true, alt: false, Key.H);

    public static bool IsHistory(KeyEventArgs e)
        => IsModifierChord(e, ctrl: true, shift: false, alt: false, Key.H);
}

public readonly record struct ShortcutSpec(bool Ctrl, bool Shift, bool Alt, bool Win, string Key, string DisplayName)
{
    public bool Matches(bool ctrl, bool shift, bool alt, bool win, string key)
        => Ctrl == ctrl
           && Shift == shift
           && Alt == alt
           && Win == win
           && string.Equals(Key, key, StringComparison.OrdinalIgnoreCase);
}
