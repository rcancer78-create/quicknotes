using System;
using System.Collections.Generic;
using System.Linq;

namespace QuickNotes.App.Services;

public readonly record struct HighlightSpan(string Text, bool IsMatch);

public static class SearchPreview
{
    public const int ComfortMaxChars = 280;
    public const int CompactMaxChars = 140;

    public static string FirstTerm(string? query)
    {
        var terms = ExtractSearchTerms(query);
        foreach (var t in terms)
        {
            if (t.Length >= 2)
                return t;
        }
        return terms.FirstOrDefault() ?? string.Empty;
    }

    public static List<string> ExtractSearchTerms(string? query)
    {
        var terms = new List<string>();
        if (string.IsNullOrWhiteSpace(query))
            return terms;

        int i = 0;
        while (i < query.Length)
        {
            if (char.IsWhiteSpace(query[i]) || query[i] == '(' || query[i] == ')')
            {
                i++;
                continue;
            }

            // Quoted phrase: "..."
            if (query[i] == '"')
            {
                int start = i + 1;
                int end = query.IndexOf('"', start);
                if (end < 0)
                {
                    end = query.Length;
                }
                string phrase = query.Substring(start, end - start).Trim();
                if (!string.IsNullOrEmpty(phrase))
                {
                    terms.Add(phrase);
                }
                i = end < query.Length ? end + 1 : end;
                continue;
            }

            // Check if token starts a structured filter: tag:, created:, updated:, untagged:
            // or if it's an unquoted word
            int tokenStart = i;
            while (i < query.Length && !char.IsWhiteSpace(query[i]) && query[i] != '(' && query[i] != ')')
            {
                // If filter value has a quote, e.g. tag:"my tag"
                if (query[i] == '"')
                {
                    int quoteEnd = query.IndexOf('"', i + 1);
                    if (quoteEnd < 0) i = query.Length;
                    else i = quoteEnd + 1;
                    break;
                }
                i++;
            }

            string rawToken = query.Substring(tokenStart, i - tokenStart);

            if (IsStructuredFilter(rawToken))
            {
                continue;
            }

            if (IsOperator(rawToken))
            {
                continue;
            }

            string clean = rawToken.Trim('"').Trim();
            if (!string.IsNullOrEmpty(clean))
            {
                terms.Add(clean);
            }
        }

        return terms;
    }

    private static bool IsStructuredFilter(string token)
    {
        string[] prefixes = { "tag:", "created:", "updated:", "untagged:", "source:", "url:" };
        foreach (var p in prefixes)
        {
            if (token.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsOperator(string token)
    {
        return token.Equals("AND", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("OR", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("WITHOUT", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("NOT", StringComparison.OrdinalIgnoreCase) ||
               token == "&&" ||
               token == "||" ||
               token == "!";
    }

    public static string BuildSnippet(string text, string? query, int maxChars, bool expanded)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        if (expanded || text.Length <= maxChars)
            return text;

        var terms = ExtractSearchTerms(query);
        int matchIndex = -1;
        int matchedTermLen = 0;

        foreach (var term in terms)
        {
            if (string.IsNullOrEmpty(term)) continue;
            int idx = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && (matchIndex < 0 || idx < matchIndex))
            {
                matchIndex = idx;
                matchedTermLen = term.Length;
            }
        }

        if (matchIndex < 0)
            return text[..maxChars].TrimEnd() + "…";

        int window = Math.Max(matchedTermLen + 24, maxChars);
        int start = Math.Max(0, matchIndex - window / 4);
        if (start + window > text.Length)
            start = Math.Max(0, text.Length - window);

        string slice = text.Substring(start, Math.Min(window, text.Length - start));
        if (start > 0)
            slice = "…" + slice.TrimStart();
        if (start + Math.Min(window, text.Length - start) < text.Length)
            slice = slice.TrimEnd() + "…";
        return slice;
    }

    public static List<HighlightSpan> SplitHighlights(string text, string? query)
    {
        var result = new List<HighlightSpan>();
        if (string.IsNullOrEmpty(text))
            return result;

        var terms = ExtractSearchTerms(query);
        if (terms.Count == 0)
        {
            result.Add(new HighlightSpan(text, false));
            return result;
        }

        var intervals = new List<(int Start, int End)>();
        foreach (var term in terms.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(term)) continue;
            int index = 0;
            while (index < text.Length)
            {
                int match = text.IndexOf(term, index, StringComparison.OrdinalIgnoreCase);
                if (match < 0) break;
                intervals.Add((match, match + term.Length));
                index = match + Math.Max(1, term.Length);
            }
        }

        if (intervals.Count == 0)
        {
            result.Add(new HighlightSpan(text, false));
            return result;
        }

        intervals.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.End.CompareTo(a.End));
        var merged = new List<(int Start, int End)>();
        var cur = intervals[0];
        for (int k = 1; k < intervals.Count; k++)
        {
            var next = intervals[k];
            if (next.Start <= cur.End)
            {
                cur = (cur.Start, Math.Max(cur.End, next.End));
            }
            else
            {
                merged.Add(cur);
                cur = next;
            }
        }
        merged.Add(cur);

        int lastIdx = 0;
        foreach (var (start, end) in merged)
        {
            if (start > lastIdx)
            {
                result.Add(new HighlightSpan(text[lastIdx..start], false));
            }
            result.Add(new HighlightSpan(text[start..end], true));
            lastIdx = end;
        }

        if (lastIdx < text.Length)
        {
            result.Add(new HighlightSpan(text[lastIdx..], false));
        }

        return result;
    }
}
