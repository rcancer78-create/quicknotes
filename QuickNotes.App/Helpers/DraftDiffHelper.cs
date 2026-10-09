using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace QuickNotes.App.Helpers;

/// <summary>
/// Generates clear diagnostic comparisons and diff summaries between committed note content
/// and an uncommitted draft journal.
/// </summary>
public static class DraftDiffHelper
{
    public static string GenerateSummary(
        string? committedTitle,
        string? committedText,
        string? draftTitle,
        string? draftText)
    {
        committedTitle ??= string.Empty;
        committedText ??= string.Empty;
        draftTitle ??= string.Empty;
        draftText ??= string.Empty;

        var sb = new StringBuilder();

        // 1. Title difference
        if (!string.Equals(committedTitle, draftTitle, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(committedTitle) && !string.IsNullOrWhiteSpace(draftTitle))
            {
                sb.AppendLine($"• Заголовок в черновике: «{Truncate(draftTitle, 60)}» (в сохранённой версии отсутствует)");
            }
            else if (!string.IsNullOrWhiteSpace(committedTitle) && string.IsNullOrWhiteSpace(draftTitle))
            {
                sb.AppendLine($"• Заголовок в черновике очищен (был: «{Truncate(committedTitle, 60)}»)");
            }
            else
            {
                sb.AppendLine($"• Заголовок: «{Truncate(committedTitle, 40)}» → «{Truncate(draftTitle, 40)}»");
            }
        }
        else if (!string.IsNullOrWhiteSpace(committedTitle))
        {
            sb.AppendLine($"• Заголовок без изменений: «{Truncate(committedTitle, 60)}»");
        }

        // 2. Text difference
        int cLen = committedText.Length;
        int dLen = draftText.Length;
        int charDiff = dLen - cLen;

        string charDiffStr = charDiff > 0 ? $"+{charDiff}" : charDiff.ToString();
        var cLines = GetLines(committedText);
        var dLines = GetLines(draftText);
        int lineDiff = dLines.Count - cLines.Count;
        string lineDiffStr = lineDiff > 0 ? $"+{lineDiff}" : lineDiff.ToString();

        sb.AppendLine($"• Текст: длина {dLen} симв. ({charDiffStr}), строк {dLines.Count} ({lineDiffStr})");

        // 3. Find first line difference
        int minLines = Math.Min(cLines.Count, dLines.Count);
        int firstDiffLine = -1;
        for (int i = 0; i < minLines; i++)
        {
            if (!string.Equals(cLines[i], dLines[i], StringComparison.Ordinal))
            {
                firstDiffLine = i + 1;
                break;
            }
        }

        if (firstDiffLine == -1 && cLines.Count != dLines.Count)
        {
            firstDiffLine = minLines + 1;
        }

        if (firstDiffLine > 0)
        {
            sb.AppendLine($"• Первое отличие со строки {firstDiffLine}");
        }

        return sb.ToString().TrimEnd();
    }

    private static List<string> GetLines(string text)
    {
        var lines = new List<string>();
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            lines.Add(line);
        }
        return lines;
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= max ? value : value[..(max - 3)] + "...";
    }
}
