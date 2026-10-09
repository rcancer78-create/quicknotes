using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace QuickNotes.App.Helpers;

public readonly record struct FormattingResult(string Text, int SelectionStart, int SelectionLength);

public enum ListContinuationAction
{
    None,
    Continue,
    Terminate
}

public readonly record struct ListContinuationResult(
    bool Handled,
    ListContinuationAction Action,
    string Text,
    int SelectionStart,
    int SelectionLength);

/// <summary>
/// Deterministic, pure markdown editing operations for selection wrapping,
/// line-based formatting (headings, lists, checkboxes), table generation,
/// and list continuation / termination on Enter.
/// </summary>
public static class MarkdownEditorOperations
{
    private static readonly Regex BulletRegex = new(@"^(\s*)([-*+])\s*(.*)$", RegexOptions.Compiled);
    private static readonly Regex NumberedRegex = new(@"^(\s*)(\d+)([\.\)])\s*(.*)$", RegexOptions.Compiled);
    private static readonly Regex CheckboxRegex = new(@"^(\s*)([-*+])\s+\[([ xX]?)\]\s*(.*)$", RegexOptions.Compiled);
    private static readonly Regex HeadingRegex = new(@"^(\s*)(#{1,6})\s*(.*)$", RegexOptions.Compiled);

    public static FormattingResult ToggleBold(string? text, int selStart, int selLen) =>
        ToggleInlineDelimiters(text, selStart, selLen, "**");

    public static FormattingResult ToggleItalic(string? text, int selStart, int selLen) =>
        ToggleInlineDelimiters(text, selStart, selLen, "*");

    public static FormattingResult ToggleCode(string? text, int selStart, int selLen)
    {
        text ??= string.Empty;
        selStart = Math.Clamp(selStart, 0, text.Length);
        selLen = Math.Clamp(selLen, 0, text.Length - selStart);

        if (selLen == 0)
        {
            // Empty selection: insert inline code placeholder
            string inserted = "``";
            string newText = text.Insert(selStart, inserted);
            return new FormattingResult(newText, selStart + 1, 0);
        }

        string selected = text.Substring(selStart, selLen);
        if (selected.StartsWith("```", StringComparison.Ordinal) &&
            selected.EndsWith("```", StringComparison.Ordinal) &&
            selected.Length >= 6 &&
            selected.Contains('\n'))
        {
            int firstNewline = selected.IndexOf('\n');
            int lastBackticks = selected.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline >= 0 && firstNewline < lastBackticks)
            {
                int contentStart = firstNewline + 1;
                int contentEnd = lastBackticks;
                if (contentEnd > 0 && selected[contentEnd - 1] == '\r') contentEnd--;
                if (contentEnd > 0 && selected[contentEnd - 1] == '\n') contentEnd--;
                if (contentEnd > 0 && selected[contentEnd - 1] == '\r') contentEnd--;
                string unwrapped = selected.Substring(contentStart, Math.Max(0, contentEnd - contentStart));
                string newText = text.Remove(selStart, selLen).Insert(selStart, unwrapped);
                return new FormattingResult(newText, selStart, unwrapped.Length);
            }
        }

        if (selected.Contains('\n'))
        {
            // Multiline selection: wrap as fenced code block
            string prefix = "```\n";
            string suffix = "\n```";
            string wrapped = prefix + selected + suffix;
            string newText = text.Remove(selStart, selLen).Insert(selStart, wrapped);
            return new FormattingResult(newText, selStart + prefix.Length, selected.Length);
        }

        return ToggleInlineDelimiters(text, selStart, selLen, "`");
    }

    public static FormattingResult ToggleInlineDelimiters(string? text, int selStart, int selLen, string delimiter)
    {
        text ??= string.Empty;
        selStart = Math.Clamp(selStart, 0, text.Length);
        selLen = Math.Clamp(selLen, 0, text.Length - selStart);
        int dLen = delimiter.Length;

        if (selLen == 0)
        {
            // Empty selection: insert pair of delimiters and place caret between them
            string pair = delimiter + delimiter;
            string newText = text.Insert(selStart, pair);
            return new FormattingResult(newText, selStart + dLen, 0);
        }

        string selected = text.Substring(selStart, selLen);

        // Case 1: Selection itself starts and ends with delimiter (e.g. **bold**)
        if (selected.Length >= dLen * 2 &&
            selected.StartsWith(delimiter, StringComparison.Ordinal) &&
            selected.EndsWith(delimiter, StringComparison.Ordinal))
        {
            string unwrapped = selected.Substring(dLen, selected.Length - dLen * 2);
            string newText = text.Remove(selStart, selLen).Insert(selStart, unwrapped);
            return new FormattingResult(newText, selStart, unwrapped.Length);
        }

        // Case 2: Delimiters are immediately outside the selection (e.g. **|bold|**)
        if (selStart >= dLen && selStart + selLen + dLen <= text.Length)
        {
            string before = text.Substring(selStart - dLen, dLen);
            string after = text.Substring(selStart + selLen, dLen);
            if (before == delimiter && after == delimiter)
            {
                // Remove outer delimiters
                string newText = text.Remove(selStart + selLen, dLen).Remove(selStart - dLen, dLen);
                return new FormattingResult(newText, selStart - dLen, selLen);
            }
        }

        // Case 3: Wrap selection
        string wrappedText = delimiter + selected + delimiter;
        string resultText = text.Remove(selStart, selLen).Insert(selStart, wrappedText);
        return new FormattingResult(resultText, selStart + dLen, selected.Length);
    }

    public static FormattingResult ToggleHeading(string? text, int selStart, int selLen, int level = 1)
    {
        text ??= string.Empty;
        selStart = Math.Clamp(selStart, 0, text.Length);
        selLen = Math.Clamp(selLen, 0, text.Length - selStart);
        level = Math.Clamp(level, 1, 6);

        string marker = new string('#', level) + " ";
        return MutateSelectedLines(text, selStart, selLen, line =>
        {
            var match = HeadingRegex.Match(line);
            if (match.Success)
            {
                int currentLevel = match.Groups[2].Value.Length;
                string remainder = match.Groups[3].Value;
                if (currentLevel == level)
                {
                    // Same level: remove heading
                    return remainder;
                }
                else
                {
                    // Different level: replace with requested level
                    return marker + remainder;
                }
            }
            return marker + line.TrimStart();
        });
    }

    public static FormattingResult CycleHeading(string? text, int selStart, int selLen)
    {
        text ??= string.Empty;
        selStart = Math.Clamp(selStart, 0, text.Length);
        selLen = Math.Clamp(selLen, 0, text.Length - selStart);

        return MutateSelectedLines(text, selStart, selLen, line =>
        {
            var match = HeadingRegex.Match(line);
            if (match.Success)
            {
                int currentLevel = match.Groups[2].Value.Length;
                string remainder = match.Groups[3].Value;
                if (currentLevel < 6)
                {
                    return new string('#', currentLevel + 1) + " " + remainder;
                }
                else
                {
                    // Level 6 -> remove heading
                    return remainder;
                }
            }
            return "# " + line.TrimStart();
        });
    }

    public static FormattingResult ToggleBulletList(string? text, int selStart, int selLen)
    {
        text ??= string.Empty;
        selStart = Math.Clamp(selStart, 0, text.Length);
        selLen = Math.Clamp(selLen, 0, text.Length - selStart);

        return MutateSelectedLines(text, selStart, selLen, line =>
        {
            // If already bullet: remove bullet
            var cbMatch = CheckboxRegex.Match(line);
            if (cbMatch.Success)
            {
                return cbMatch.Groups[1].Value + "- " + cbMatch.Groups[4].Value;
            }

            var numMatch = NumberedRegex.Match(line);
            if (numMatch.Success)
            {
                return numMatch.Groups[1].Value + "- " + numMatch.Groups[4].Value;
            }

            var bMatch = BulletRegex.Match(line);
            if (bMatch.Success)
            {
                return bMatch.Groups[1].Value + bMatch.Groups[3].Value;
            }

            int leadingSpaces = line.Length - line.TrimStart().Length;
            string indent = line.Substring(0, leadingSpaces);
            return indent + "- " + line.Substring(leadingSpaces);
        });
    }

    public static FormattingResult ToggleNumberedList(string? text, int selStart, int selLen)
    {
        text ??= string.Empty;
        selStart = Math.Clamp(selStart, 0, text.Length);
        selLen = Math.Clamp(selLen, 0, text.Length - selStart);

        int counter = 1;
        return MutateSelectedLines(text, selStart, selLen, line =>
        {
            var numMatch = NumberedRegex.Match(line);
            if (numMatch.Success)
            {
                return numMatch.Groups[1].Value + numMatch.Groups[4].Value;
            }

            var cbMatch = CheckboxRegex.Match(line);
            if (cbMatch.Success)
            {
                return cbMatch.Groups[1].Value + $"{counter++}. " + cbMatch.Groups[4].Value;
            }

            var bMatch = BulletRegex.Match(line);
            if (bMatch.Success)
            {
                return bMatch.Groups[1].Value + $"{counter++}. " + bMatch.Groups[3].Value;
            }

            int leadingSpaces = line.Length - line.TrimStart().Length;
            string indent = line.Substring(0, leadingSpaces);
            return indent + $"{counter++}. " + line.Substring(leadingSpaces);
        });
    }

    public static FormattingResult ToggleCheckbox(string? text, int selStart, int selLen)
    {
        text ??= string.Empty;
        selStart = Math.Clamp(selStart, 0, text.Length);
        selLen = Math.Clamp(selLen, 0, text.Length - selStart);

        return MutateSelectedLines(text, selStart, selLen, line =>
        {
            var cbMatch = CheckboxRegex.Match(line);
            if (cbMatch.Success)
            {
                string state = cbMatch.Groups[3].Value;
                if (string.IsNullOrWhiteSpace(state))
                {
                    // [ ] -> [x]
                    return cbMatch.Groups[1].Value + cbMatch.Groups[2].Value + " [x] " + cbMatch.Groups[4].Value;
                }
                else
                {
                    // [x] -> remove checkbox, restore bullet
                    return cbMatch.Groups[1].Value + cbMatch.Groups[2].Value + " " + cbMatch.Groups[4].Value;
                }
            }

            var bMatch = BulletRegex.Match(line);
            if (bMatch.Success)
            {
                return bMatch.Groups[1].Value + bMatch.Groups[2].Value + " [ ] " + bMatch.Groups[3].Value;
            }

            var numMatch = NumberedRegex.Match(line);
            if (numMatch.Success)
            {
                return numMatch.Groups[1].Value + "- [ ] " + numMatch.Groups[4].Value;
            }

            int leadingSpaces = line.Length - line.TrimStart().Length;
            string indent = line.Substring(0, leadingSpaces);
            return indent + "- [ ] " + line.Substring(leadingSpaces);
        });
    }

    public static FormattingResult InsertLink(string? text, int selStart, int selLen, string defaultUrl = "https://")
    {
        text ??= string.Empty;
        selStart = Math.Clamp(selStart, 0, text.Length);
        selLen = Math.Clamp(selLen, 0, text.Length - selStart);

        if (selLen == 0)
        {
            string snippet = "[текст](" + defaultUrl + ")";
            string newText = text.Insert(selStart, snippet);
            return new FormattingResult(newText, selStart + 1, "текст".Length);
        }

        string selected = text.Substring(selStart, selLen);
        if (selected.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            selected.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            string snippet = $"[ссылка]({selected})";
            string newText = text.Remove(selStart, selLen).Insert(selStart, snippet);
            return new FormattingResult(newText, selStart + 1, "ссылка".Length);
        }
        else
        {
            string snippet = $"[{selected}]({defaultUrl})";
            string newText = text.Remove(selStart, selLen).Insert(selStart, snippet);
            int urlStart = selStart + selected.Length + 3;
            return new FormattingResult(newText, urlStart, defaultUrl.Length);
        }
    }

    public static string GenerateTable(int rows, int cols)
    {
        rows = Math.Clamp(rows, 1, 50);
        cols = Math.Clamp(cols, 1, 20);

        var sb = new StringBuilder();

        // Header
        sb.Append('|');
        for (int c = 1; c <= cols; c++)
        {
            sb.Append($" Заголовок {c} |");
        }
        sb.AppendLine();

        // Separator
        sb.Append('|');
        for (int c = 1; c <= cols; c++)
        {
            sb.Append(" --- |");
        }
        sb.AppendLine();

        // Data rows
        for (int r = 1; r <= rows; r++)
        {
            sb.Append('|');
            for (int c = 1; c <= cols; c++)
            {
                sb.Append("  |");
            }
            if (r < rows)
            {
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    public static FormattingResult InsertTable(string? text, int selStart, int selLen, int rows, int cols)
    {
        text ??= string.Empty;
        selStart = Math.Clamp(selStart, 0, text.Length);
        selLen = Math.Clamp(selLen, 0, text.Length - selStart);

        string table = GenerateTable(rows, cols);

        // Ensure leading/trailing newlines if needed
        string prefix = (selStart > 0 && text[selStart - 1] != '\n') ? "\n\n" : "";
        string suffix = (selStart + selLen < text.Length && text[selStart + selLen] != '\n') ? "\n\n" : "\n";

        string snippet = prefix + table + suffix;
        string newText = text.Remove(selStart, selLen).Insert(selStart, snippet);
        return new FormattingResult(newText, selStart + prefix.Length, table.Length);
    }

    public static ListContinuationResult HandleEnter(string? text, int selStart, int selLen)
    {
        if (selLen > 0)
        {
            return new ListContinuationResult(false, ListContinuationAction.None, text ?? string.Empty, selStart, selLen);
        }

        text ??= string.Empty;
        selStart = Math.Clamp(selStart, 0, text.Length);

        // Find the start of the current line
        int lineStart = text.LastIndexOf('\n', Math.Max(0, selStart - 1)) + 1;
        string lineBeforeCaret = text.Substring(lineStart, selStart - lineStart);

        int nextNewline = text.IndexOf('\n', selStart);
        int lineEnd = nextNewline >= 0 ? nextNewline : text.Length;
        string lineAfterCaret = text.Substring(selStart, lineEnd - selStart);

        // 1. Checkbox list item: - [ ] or - [x]
        var cbMatch = CheckboxRegex.Match(lineBeforeCaret);
        if (cbMatch.Success)
        {
            string indent = cbMatch.Groups[1].Value;
            string bullet = cbMatch.Groups[2].Value;
            string content = cbMatch.Groups[4].Value;

            if (string.IsNullOrWhiteSpace(content) && string.IsNullOrWhiteSpace(lineAfterCaret))
            {
                // Terminate list item: remove the empty checkbox marker on current line
                string newText = text.Remove(lineStart, lineEnd - lineStart);
                return new ListContinuationResult(true, ListContinuationAction.Terminate, newText, lineStart, 0);
            }

            string continuation = "\n" + indent + bullet + " [ ] ";
            string newTextCont = text.Insert(selStart, continuation);
            return new ListContinuationResult(true, ListContinuationAction.Continue, newTextCont, selStart + continuation.Length, 0);
        }

        // 2. Numbered list item: 1. or 1)
        var numMatch = NumberedRegex.Match(lineBeforeCaret);
        if (numMatch.Success)
        {
            string indent = numMatch.Groups[1].Value;
            string numStr = numMatch.Groups[2].Value;
            string delim = numMatch.Groups[3].Value;
            string content = numMatch.Groups[4].Value;

            if (string.IsNullOrWhiteSpace(content) && string.IsNullOrWhiteSpace(lineAfterCaret))
            {
                // Terminate
                string newText = text.Remove(lineStart, lineEnd - lineStart);
                return new ListContinuationResult(true, ListContinuationAction.Terminate, newText, lineStart, 0);
            }

            int currentNum = int.TryParse(numStr, out int n) ? n : 1;
            string continuation = "\n" + indent + (currentNum + 1) + delim + " ";
            string newTextCont = text.Insert(selStart, continuation);
            return new ListContinuationResult(true, ListContinuationAction.Continue, newTextCont, selStart + continuation.Length, 0);
        }

        // 3. Bullet list item: - or * or +
        var bMatch = BulletRegex.Match(lineBeforeCaret);
        if (bMatch.Success)
        {
            string indent = bMatch.Groups[1].Value;
            string bullet = bMatch.Groups[2].Value;
            string content = bMatch.Groups[3].Value;

            if (string.IsNullOrWhiteSpace(content) && string.IsNullOrWhiteSpace(lineAfterCaret))
            {
                // Terminate
                string newText = text.Remove(lineStart, lineEnd - lineStart);
                return new ListContinuationResult(true, ListContinuationAction.Terminate, newText, lineStart, 0);
            }

            string continuation = "\n" + indent + bullet + " ";
            string newTextCont = text.Insert(selStart, continuation);
            return new ListContinuationResult(true, ListContinuationAction.Continue, newTextCont, selStart + continuation.Length, 0);
        }

        return new ListContinuationResult(false, ListContinuationAction.None, text, selStart, 0);
    }

    private static FormattingResult MutateSelectedLines(string text, int selStart, int selLen, Func<string, string> lineMutator)
    {
        // Find line start of selection start
        int rangeStart = text.LastIndexOf('\n', Math.Max(0, selStart - 1)) + 1;

        // Find line end of selection end
        int selEnd = selStart + selLen;
        int nextNewline = selEnd > 0 ? text.IndexOf('\n', selEnd - 1) : text.IndexOf('\n', 0);
        int rangeEnd = nextNewline >= 0 ? nextNewline : text.Length;

        string targetBlock = text.Substring(rangeStart, rangeEnd - rangeStart);
        string normalized = targetBlock.Replace("\r\n", "\n").Replace('\r', '\n');
        string[] lines = normalized.Split('\n');

        var mutatedLines = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            mutatedLines.Add(lineMutator(line));
        }

        string newBlock = string.Join("\n", mutatedLines);
        string newText = text.Remove(rangeStart, rangeEnd - rangeStart).Insert(rangeStart, newBlock);

        // Maintain selection coverage over the mutated lines
        return new FormattingResult(newText, rangeStart, newBlock.Length);
    }
}
