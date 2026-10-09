using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using QuickNotes.App.Models;

namespace QuickNotes.App.Services;

/// <summary>
/// Line-oriented open-checkbox parser. Source of truth is <see cref="Note.Text"/> only.
/// Contract (ADR-011): optional indent + <c>- [ ]</c> or <c>* [ ]</c> + required whitespace and non-empty text.
/// Closed <c>[x]</c>/<c>[X]</c>, fenced code, escaped and inline-like lines are not indexed.
/// Due date: documented token <c>@YYYY-MM-DD</c>, strict Gregorian invariant-culture; the first valid token wins.
/// </summary>
public static class MarkdownTaskParser
{
    public const string DueDateTokenPattern = "@YYYY-MM-DD";

    private static readonly Regex OpenCheckboxLine = new(
        @"^(?<indent>[ \t]*)(?<bullet>[-*])[ ]\[[ ]\][ \t]+(?<body>\S.*)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex DueDateToken = new(
        @"(?:^|[^A-Za-z0-9_])@(?<date>\d{4}-\d{2}-\d{2})(?!\d)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static IReadOnlyList<MarkdownTaskLocator> Parse(
        string? text,
        int noteId = 0,
        Guid noteSyncId = default,
        string? noteTitle = null,
        DateTime noteUpdatedAt = default)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<MarkdownTaskLocator>();
        }

        byte[] snapshotProof = ComputeNoteSnapshotProof(text);
        var locators = new List<MarkdownTaskLocator>();
        bool inFence = false;
        char fenceChar = '\0';
        int fenceLength = 0;
        int lineNumber = 1;
        int index = 0;
        int length = text.Length;
        string title = noteTitle ?? string.Empty;

        while (index <= length)
        {
            int lineStart = index;
            while (index < length && text[index] != '\n' && text[index] != '\r')
            {
                index++;
            }

            int lineEnd = index;
            string line = text.Substring(lineStart, lineEnd - lineStart);
            int consumedNewline = 0;
            if (index < length)
            {
                if (text[index] == '\r' && index + 1 < length && text[index + 1] == '\n')
                {
                    consumedNewline = 2;
                    index += 2;
                }
                else
                {
                    consumedNewline = 1;
                    index++;
                }
            }

            int lineLengthWithNewline = (lineEnd - lineStart) + consumedNewline;
            if (index >= length && consumedNewline == 0)
            {
                lineLengthWithNewline = lineEnd - lineStart;
            }

            if (TryParseFence(line, out char ch, out int run))
            {
                if (!inFence)
                {
                    inFence = true;
                    fenceChar = ch;
                    fenceLength = run;
                    if (index >= length)
                    {
                        break;
                    }

                    lineNumber++;
                    continue;
                }

                if (ch == fenceChar && run >= fenceLength && RemainderIsWhitespace(line, LeadingIndentWidth(line) + run))
                {
                    inFence = false;
                    fenceChar = '\0';
                    fenceLength = 0;
                    if (index >= length)
                    {
                        break;
                    }

                    lineNumber++;
                    continue;
                }
            }

            if (!inFence)
            {
                var match = OpenCheckboxLine.Match(line);
                if (match.Success && !LooksEscapedOrInline(line, match))
                {
                    string body = match.Groups["body"].Value.Trim();
                    if (body.Length > 0)
                    {
                        string bullet = match.Groups["bullet"].Value;
                        locators.Add(new MarkdownTaskLocator
                        {
                            NoteId = noteId,
                            NoteSyncId = noteSyncId,
                            LineNumber = lineNumber,
                            TaskText = body,
                            DueDate = ExtractFirstValidDueDate(body),
                            Fingerprint = bullet + "\u001f" + body,
                            NoteSnapshotProof = snapshotProof,
                            NoteTitle = title,
                            NoteUpdatedAt = noteUpdatedAt,
                            LineStartCharIndex = lineStart,
                            LineLength = lineEnd - lineStart,
                            Bullet = bullet
                        });
                    }
                }
            }

            if (index >= length)
            {
                break;
            }

            lineNumber++;
            _ = lineLengthWithNewline;
        }

        if (locators.Count == 0)
        {
            return locators;
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var locator in locators)
        {
            counts[locator.Fingerprint] = counts.TryGetValue(locator.Fingerprint, out int n) ? n + 1 : 1;
        }

        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var stamped = new List<MarkdownTaskLocator>(locators.Count);
        foreach (var locator in locators)
        {
            occurrences.TryGetValue(locator.Fingerprint, out int occ);
            occurrences[locator.Fingerprint] = occ + 1;
            stamped.Add(new MarkdownTaskLocator
            {
                NoteId = locator.NoteId,
                NoteSyncId = locator.NoteSyncId,
                LineNumber = locator.LineNumber,
                TaskText = locator.TaskText,
                DueDate = locator.DueDate,
                Fingerprint = locator.Fingerprint,
                OccurrenceIndex = occ,
                SameFingerprintCount = counts[locator.Fingerprint],
                NoteSnapshotProof = locator.NoteSnapshotProof,
                NoteTitle = locator.NoteTitle,
                NoteUpdatedAt = locator.NoteUpdatedAt,
                LineStartCharIndex = locator.LineStartCharIndex,
                LineLength = locator.LineLength,
                Bullet = locator.Bullet
            });
        }

        return stamped;
    }

    public static byte[] ComputeNoteSnapshotProof(string? text)
    {
        return SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty));
    }

    public static DateOnly? ExtractFirstValidDueDate(string? taskText)
    {
        if (string.IsNullOrEmpty(taskText))
        {
            return null;
        }

        foreach (Match match in DueDateToken.Matches(taskText))
        {
            string token = match.Groups["date"].Value;
            if (DateOnly.TryParseExact(
                    token,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateOnly date))
            {
                return date;
            }
        }

        return null;
    }

    private static bool LooksEscapedOrInline(string line, Match match)
    {
        int bulletAt = match.Groups["bullet"].Index;
        if (bulletAt > 0 && line[bulletAt - 1] == '\\')
        {
            return true;
        }

        string trimmed = line.TrimStart();
        if (trimmed.StartsWith("`", StringComparison.Ordinal) && trimmed.EndsWith("`", StringComparison.Ordinal) && trimmed.Length >= 2)
        {
            return true;
        }

        return false;
    }

    private static bool TryParseFence(string line, out char ch, out int run)
    {
        ch = '\0';
        run = 0;
        int i = 0;
        int indent = 0;
        while (i < line.Length && indent < 4 && (line[i] == ' ' || line[i] == '\t'))
        {
            indent += line[i] == '\t' ? 4 : 1;
            i++;
        }

        if (indent > 3 || i >= line.Length)
        {
            return false;
        }

        ch = line[i];
        if (ch != '`' && ch != '~')
        {
            return false;
        }

        int start = i;
        while (i < line.Length && line[i] == ch)
        {
            i++;
        }

        run = i - start;
        if (run < 3)
        {
            return false;
        }

        if (ch == '`')
        {
            for (int j = i; j < line.Length; j++)
            {
                if (line[j] == '`')
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static int LeadingIndentWidth(string line)
    {
        int i = 0;
        int indent = 0;
        while (i < line.Length && indent < 4 && (line[i] == ' ' || line[i] == '\t'))
        {
            indent += line[i] == '\t' ? 4 : 1;
            i++;
        }

        return i;
    }

    private static bool RemainderIsWhitespace(string line, int start)
    {
        for (int i = start; i < line.Length; i++)
        {
            if (!char.IsWhiteSpace(line[i]))
            {
                return false;
            }
        }

        return true;
    }
}
