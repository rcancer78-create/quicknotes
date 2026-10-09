using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using QuickNotes.App.Services;

namespace QuickNotes.App.Models;

public class TagRule
{
    public int TagId { get; set; }

    public List<string> RequiredTerms { get; set; } = new();

    /// <summary>
    /// Alias for RequiredTerms (all must match).
    /// </summary>
    [JsonIgnore]
    public List<string> AllTerms
    {
        get => RequiredTerms;
        set => RequiredTerms = value;
    }

    public List<string> AnyTerms { get; set; } = new();

    public List<string> ExcludedTerms { get; set; } = new();

    [JsonIgnore]
    public bool IsEmpty =>
        (RequiredTerms == null || RequiredTerms.Count == 0) &&
        (AnyTerms == null || AnyTerms.Count == 0) &&
        (ExcludedTerms == null || ExcludedTerms.Count == 0);

    public TagRule Clone()
    {
        return new TagRule
        {
            TagId = TagId,
            RequiredTerms = RequiredTerms != null ? new List<string>(RequiredTerms) : new List<string>(),
            AnyTerms = AnyTerms != null ? new List<string>(AnyTerms) : new List<string>(),
            ExcludedTerms = ExcludedTerms != null ? new List<string>(ExcludedTerms) : new List<string>()
        };
    }

    public static List<string> NormalizeTerms(IEnumerable<string>? terms)
    {
        if (terms == null)
            return new List<string>();

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var term in terms)
        {
            var normalized = TagDetectionService.NormalizeTerm(term);
            if (!string.IsNullOrWhiteSpace(normalized) && seen.Add(normalized))
            {
                result.Add(normalized);
            }
        }

        return result;
    }

    public void Normalize()
    {
        RequiredTerms = NormalizeTerms(RequiredTerms);
        AnyTerms = NormalizeTerms(AnyTerms);
        ExcludedTerms = NormalizeTerms(ExcludedTerms);
    }

    public bool MatchesConditions(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        // 1. Required/All terms: every term must be present
        if (RequiredTerms != null && RequiredTerms.Count > 0)
        {
            foreach (var term in RequiredTerms)
            {
                if (!TagDetectionService.MatchesTerm(text, term))
                {
                    return false;
                }
            }
        }

        // 2. Any terms: if non-empty, at least one must be present
        if (AnyTerms != null && AnyTerms.Count > 0)
        {
            bool anyMatched = false;
            foreach (var term in AnyTerms)
            {
                if (TagDetectionService.MatchesTerm(text, term))
                {
                    anyMatched = true;
                    break;
                }
            }

            if (!anyMatched)
            {
                return false;
            }
        }

        // 3. Excluded terms: none may be present
        if (ExcludedTerms != null && ExcludedTerms.Count > 0)
        {
            foreach (var term in ExcludedTerms)
            {
                if (TagDetectionService.MatchesTerm(text, term))
                {
                    return false;
                }
            }
        }

        return true;
    }

    public string GetSummary()
    {
        if (IsEmpty)
            return string.Empty;

        var parts = new List<string>();

        if (RequiredTerms != null && RequiredTerms.Count > 0)
        {
            parts.Add($"все: {string.Join(", ", RequiredTerms)}");
        }

        if (AnyTerms != null && AnyTerms.Count > 0)
        {
            parts.Add($"любое: {string.Join(", ", AnyTerms)}");
        }

        if (ExcludedTerms != null && ExcludedTerms.Count > 0)
        {
            parts.Add($"кроме: {string.Join(", ", ExcludedTerms)}");
        }

        return string.Join("; ", parts);
    }
}
