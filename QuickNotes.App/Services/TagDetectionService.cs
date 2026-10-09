using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using QuickNotes.App.Models;

namespace QuickNotes.App.Services;

public class TagDetectionService
{
    private readonly TagRuleService? _tagRuleService;

    public TagDetectionService(TagRuleService? tagRuleService = null)
    {
        _tagRuleService = tagRuleService;
    }

    private IReadOnlyDictionary<int, TagRule>? GetEffectiveRules(IReadOnlyDictionary<int, TagRule>? rules)
    {
        return rules ?? _tagRuleService?.GetAllRules();
    }

    /// <summary>
    /// Builds a regular expression pattern for a tag name or synonym term
    /// that respects word boundaries for standard words as well as technical terms (C#, C++, .NET, etc.)
    /// </summary>
    public static string BuildRegexPatternForTerm(string term)
    {
        term = term.Trim();
        if (string.IsNullOrWhiteSpace(term))
            return string.Empty;

        // Split words by whitespace to allow matching across arbitrary whitespace (e.g. "Oracle   DB")
        var parts = term.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var escapedParts = parts.Select(Regex.Escape);
        string corePattern = string.Join(@"\s+", escapedParts);

        char firstChar = term[0];
        char lastChar = term[^1];

        // Left word boundary
        string leftBoundary = char.IsLetterOrDigit(firstChar) || firstChar == '_'
            ? @"(?<![\p{L}\p{N}_])"
            : @"(?<![\p{L}\p{N}_\.])";

        // Right word boundary
        string rightBoundary = char.IsLetterOrDigit(lastChar) || lastChar == '_'
            ? @"(?![\p{L}\p{N}_])"
            : @"(?![\p{L}\p{N}_#+])";

        return $"{leftBoundary}{corePattern}{rightBoundary}";
    }

    /// <summary>
    /// Determines whether the given text matches the term (tag name or synonym)
    /// </summary>
    public static bool MatchesTerm(string text, string term)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(term))
            return false;

        string pattern = BuildRegexPatternForTerm(term);
        if (string.IsNullOrEmpty(pattern))
            return false;

        return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// Normalizes term by trimming leading/trailing whitespaces and collapsing consecutive spaces.
    /// Preserves original casing.
    /// </summary>
    public static string NormalizeTerm(string term)
    {
        if (string.IsNullOrWhiteSpace(term))
            return string.Empty;

        var parts = term.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts);
    }

    private sealed class TagMatchCandidate
    {
        public bool IsTagName { get; init; }
        public TagSynonym? Synonym { get; init; }
        public string NormalizedTerm { get; init; } = string.Empty;
        public int OrderIndex { get; init; }
    }

    /// <summary>
    /// Evaluates all match candidates (tag name and all synonyms) against the given text.
    /// Returns the best match:
    /// - Longest match wins
    /// - If equal length, tag name has priority over synonym
    /// - If still equal, stable order (order index in synonyms, then ID, then ordinal)
    /// Returns null if no candidate matches.
    /// </summary>
    public TagDetectionMatch? MatchTag(string text, Tag tag)
    {
        return MatchTag(text, tag, (IReadOnlyDictionary<int, TagRule>?)null);
    }

    public TagDetectionMatch? MatchTag(string text, Tag tag, IReadOnlyDictionary<int, TagRule>? rules)
    {
        if (tag == null)
            return null;

        var effectiveRules = GetEffectiveRules(rules);
        TagRule? rule = null;
        effectiveRules?.TryGetValue(tag.Id, out rule);
        return MatchTag(text, tag, rule);
    }

    public TagDetectionMatch? MatchTag(string text, Tag tag, TagRule? rule)
    {
        if (string.IsNullOrWhiteSpace(text) || tag == null)
            return null;

        var candidates = new List<TagMatchCandidate>();

        if (!string.IsNullOrWhiteSpace(tag.Name))
        {
            var normName = NormalizeTerm(tag.Name);
            if (!string.IsNullOrEmpty(normName) && MatchesTerm(text, normName))
            {
                candidates.Add(new TagMatchCandidate
                {
                    IsTagName = true,
                    Synonym = null,
                    NormalizedTerm = normName,
                    OrderIndex = -1
                });
            }
        }

        if (tag.Synonyms != null)
        {
            int index = 0;
            foreach (var syn in tag.Synonyms)
            {
                if (!string.IsNullOrWhiteSpace(syn.Value))
                {
                    var normSyn = NormalizeTerm(syn.Value);
                    if (!string.IsNullOrEmpty(normSyn) && MatchesTerm(text, normSyn))
                    {
                        candidates.Add(new TagMatchCandidate
                        {
                            IsTagName = false,
                            Synonym = syn,
                            NormalizedTerm = normSyn,
                            OrderIndex = index
                        });
                    }
                }
                index++;
            }
        }

        if (candidates.Count == 0)
            return null;

        // Base match succeeded. If a rule is specified, final match is allowed only if rule conditions are met.
        if (rule != null && !rule.IsEmpty)
        {
            if (!rule.MatchesConditions(text))
            {
                return null;
            }
        }

        var best = candidates
            .OrderByDescending(c => c.NormalizedTerm.Length)
            .ThenByDescending(c => c.IsTagName)
            .ThenBy(c => c.OrderIndex)
            .ThenBy(c => c.Synonym?.Id ?? 0)
            .ThenBy(c => c.NormalizedTerm, StringComparer.Ordinal)
            .First();

        return new TagDetectionMatch
        {
            Tag = tag,
            Source = best.IsTagName ? TagMatchSource.TagName : TagMatchSource.Synonym,
            MatchedSynonym = best.Synonym,
            MatchedTerm = best.NormalizedTerm,
            AppliedRule = (rule != null && !rule.IsEmpty) ? rule : null,
            RuleDescription = (rule != null && !rule.IsEmpty) ? rule.GetSummary() : null
        };
    }

    /// <summary>
    /// Detects all matching tags with detailed match information for each tag
    /// (whether matched by tag name or synonym, matched term and explanation reason).
    /// </summary>
    public List<TagDetectionMatch> DetectDetailedTags(
        string text,
        IEnumerable<Tag> availableTags,
        IReadOnlyDictionary<int, TagRule>? rules = null)
    {
        var matched = new List<TagDetectionMatch>();
        if (string.IsNullOrWhiteSpace(text) || availableTags == null)
            return matched;

        var effectiveRules = GetEffectiveRules(rules);

        foreach (var tag in availableTags)
        {
            TagRule? rule = null;
            effectiveRules?.TryGetValue(tag.Id, out rule);

            var match = MatchTag(text, tag, rule);
            if (match != null)
            {
                matched.Add(match);
            }
        }

        return matched;
    }

    /// <summary>
    /// Detects all tags that match the given text (either via Tag.Name or any Tag.Synonyms)
    /// </summary>
    public List<Tag> DetectTags(
        string text,
        IEnumerable<Tag> availableTags,
        IReadOnlyDictionary<int, TagRule>? rules = null)
    {
        return DetectDetailedTags(text, availableTags, rules).Select(m => m.Tag).ToList();
    }

    /// <summary>
    /// Rescans a note against available tags:
    /// - Preserves Manual tags
    /// - Respects Suppressed tags (does not re-add them)
    /// - Recalculates Auto tags (adds new detected, removes old undetected auto tags)
    /// </summary>
    public void RescanNote(
        Note note,
        IEnumerable<Tag> availableTags,
        IReadOnlyDictionary<int, TagRule>? rules = null)
    {
        var detectedTags = DetectTags(note.Text, availableTags, rules);
        var detectedTagIds = detectedTags.Select(t => t.Id).ToHashSet();

        // 1. Remove undetected Auto tags (that are not suppressed)
        var toRemove = note.NoteTags
            .Where(nt => nt.Origin == TagOrigin.Auto && !nt.IsSuppressed && !detectedTagIds.Contains(nt.TagId))
            .ToList();

        foreach (var nt in toRemove)
        {
            note.NoteTags.Remove(nt);
        }

        // 2. For each detected tag, add as Auto if not already present and not suppressed
        foreach (var detectedTag in detectedTags)
        {
            var existing = note.NoteTags.FirstOrDefault(nt => nt.TagId == detectedTag.Id);
            if (existing == null)
            {
                note.NoteTags.Add(new NoteTag
                {
                    NoteId = note.Id,
                    Note = note,
                    TagId = detectedTag.Id,
                    Origin = TagOrigin.Auto,
                    IsSuppressed = false
                });
            }
            // If already exists: if it's suppressed or manual, leave it as is!
        }
    }
}
