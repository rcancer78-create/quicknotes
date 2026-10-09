using System;
using System.Collections.Generic;
using System.Text;

namespace QuickNotes.App.Helpers;

/// <summary>
/// Generates deterministic, stable anchors (slugs) from heading text,
/// supporting Cyrillic characters, ASCII characters, and collision-free duplicate resolution.
/// </summary>
public static class HeadingAnchorHelper
{
    public static string CreateSlug(string headingText)
    {
        if (string.IsNullOrWhiteSpace(headingText))
        {
            return "heading";
        }

        var sb = new StringBuilder();
        bool lastWasHyphen = false;

        foreach (char c in headingText.Trim())
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
                lastWasHyphen = false;
            }
            else if (c == ' ' || c == '-' || c == '_' || c == '.' || c == ',' || c == ':' || c == ';' || c == '/' || c == '\\')
            {
                if (!lastWasHyphen && sb.Length > 0)
                {
                    sb.Append('-');
                    lastWasHyphen = true;
                }
            }
        }

        string result = sb.ToString().Trim('-');
        return string.IsNullOrEmpty(result) ? "heading" : result;
    }

    public static string GetUniqueAnchor(string headingText, ISet<string> existingAnchors)
    {
        ArgumentNullException.ThrowIfNull(existingAnchors);

        string baseSlug = CreateSlug(headingText);
        if (existingAnchors.Add(baseSlug))
        {
            return baseSlug;
        }

        int suffix = 1;
        while (true)
        {
            string candidate = $"{baseSlug}-{suffix}";
            if (existingAnchors.Add(candidate))
            {
                return candidate;
            }
            suffix++;
        }
    }
}
