using System;
using System.Linq;

namespace QuickNotes.App.Helpers;

/// <summary>
/// Helper for deriving, sanitizing, and formatting note titles.
/// Enforces business rules:
/// - Empty Title in data is valid and stored as empty string.
/// - Fallback display title is UI-only ("Без заголовка") and NEVER saved to data.
/// - Unprotected notes derive title from the first non-empty line without altering note text.
/// </summary>
public static class NoteTitleHelper
{
    public const string DefaultUntitledPlaceholder = "Без заголовка";
    public const int DefaultMaxDisplayLength = 72;
    public const int DefaultMaxDerivedLength = 120;

    /// <summary>
    /// Derives a clean title from note text by taking the first non-empty line and stripping markdown heading symbols.
    /// Does not alter the original text.
    /// </summary>
    public static string DeriveTitleFromText(string? text, int maxLength = DefaultMaxDerivedLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var cleaned = StripMarkdownHeading(line).Trim();
            if (!string.IsNullOrWhiteSpace(cleaned))
            {
                cleaned = SanitizeSingleLine(cleaned);
                if (maxLength > 0 && cleaned.Length > maxLength)
                {
                    return cleaned.Substring(0, maxLength).TrimEnd();
                }
                return cleaned;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Computes user-facing display title for UI representation only.
    /// 1. Uses explicit title if non-empty.
    /// 2. Falls back to first non-empty line of text.
    /// 3. Falls back to "Без заголовка".
    /// This value is purely for display and is never saved to the database.
    /// </summary>
    public static string GetDisplayTitle(string? title, string? text, int maxLength = DefaultMaxDisplayLength)
    {
        if (!string.IsNullOrWhiteSpace(title))
        {
            var sanitized = SanitizeSingleLine(title.Trim());
            return TruncateWithEllipsis(sanitized, maxLength);
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            var derived = DeriveTitleFromText(text, maxLength);
            if (!string.IsNullOrWhiteSpace(derived))
            {
                return TruncateWithEllipsis(derived, maxLength);
            }
        }

        return DefaultUntitledPlaceholder;
    }

    /// <summary>
    /// Sanitizes a string by replacing newline characters with spaces and collapsing redundant whitespace.
    /// </summary>
    public static string SanitizeSingleLine(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var replaced = input.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
        while (replaced.Contains("  "))
        {
            replaced = replaced.Replace("  ", " ");
        }

        return replaced.Trim();
    }

    /// <summary>
    /// Strips leading markdown header tokens (#) and trims.
    /// </summary>
    public static string StripMarkdownHeading(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return string.Empty;
        }

        var trimmed = line.TrimStart();
        if (trimmed.StartsWith('#'))
        {
            trimmed = trimmed.TrimStart('#').TrimStart();
        }

        return trimmed;
    }

    /// <summary>
    /// Truncates string to maxLength with an ellipsis ("…") if it exceeds the limit.
    /// </summary>
    public static string TruncateWithEllipsis(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || maxLength <= 0 || value.Length <= maxLength)
        {
            return value ?? string.Empty;
        }

        return value.Substring(0, maxLength).TrimEnd() + "…";
    }
}
