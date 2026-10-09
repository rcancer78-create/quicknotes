using System;
using System.Collections.Generic;
using System.Linq;

namespace QuickNotes.App.Models;

public record CapturedNoteContext
{
    public string Text { get; init; } = string.Empty;
    public string? ProcessName { get; init; }
    public string? WindowTitle { get; init; }
    public string? Url { get; init; }
    public DateTime CapturedAt { get; init; } = DateTime.Now;
}

public static class NoteSourceContext
{
    public static string FormatCompactSource(string? processName, string? windowTitle, string? url)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(processName))
        {
            parts.Add(processName.Trim());
        }

        if (!string.IsNullOrWhiteSpace(windowTitle))
        {
            var trimmedTitle = windowTitle.Trim();
            if (parts.Count == 0 || !string.Equals(parts[0], trimmedTitle, StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(trimmedTitle);
            }
        }

        if (!string.IsNullOrWhiteSpace(url))
        {
            parts.Add(url.Trim());
        }

        return string.Join(" • ", parts);
    }

    public static string? SanitizeUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();
        if (raw.Contains(' ') || raw.Contains('\n') || raw.Contains('\r') || raw.Contains('\t')) return null;

        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return uri.AbsoluteUri;
        }

        if (raw.Contains('.') && !raw.StartsWith('.') && !raw.EndsWith('.') &&
            Uri.TryCreate("https://" + raw, UriKind.Absolute, out var testUri) &&
            testUri.Host.Contains('.') &&
            !testUri.Host.EndsWith('.'))
        {
            var hostParts = testUri.Host.Split('.');
            if (hostParts.Length >= 2 && hostParts[^1].Length >= 2 && hostParts[^1].All(char.IsLetter))
            {
                return testUri.AbsoluteUri;
            }
        }

        return null;
    }
}
