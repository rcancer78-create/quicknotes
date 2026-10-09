using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using QuickNotes.App.Models;

namespace QuickNotes.App.Services;

public sealed class SearchQueryResult
{
    public string RawQuery { get; }
    public string FreeText { get; }
    public SearchCondition? Condition { get; }
    public IReadOnlyList<SearchFilterChipModel> Chips { get; }

    public SearchQueryResult(
        string rawQuery,
        string freeText,
        SearchCondition? condition,
        IReadOnlyList<SearchFilterChipModel> chips)
    {
        RawQuery = rawQuery;
        FreeText = freeText;
        Condition = condition;
        Chips = chips;
    }
}

public static class SearchQueryParser
{
    private static readonly string[] ConditionPrefixes = { "tag:", "created:", "updated:", "untagged:", "source:", "url:" };

    private enum TokenType
    {
        Word,
        Condition,
        And,
        Or,
        Without,
        OpenParen,
        CloseParen
    }

    private sealed class QueryToken
    {
        public TokenType Type { get; set; }
        public string RawText { get; set; } = string.Empty;
        public int StartIndex { get; set; }
        public int Length { get; set; }
        public SearchCondition? ConditionPayload { get; set; }
        public SearchFilterChipModel? ChipModel { get; set; }
        public bool IsUsedInCondition { get; set; }
    }

    public static SearchQueryResult Parse(string? rawQuery, IEnumerable<Tag> allTags)
    {
        if (string.IsNullOrWhiteSpace(rawQuery))
        {
            return new SearchQueryResult(string.Empty, string.Empty, null, Array.Empty<SearchFilterChipModel>());
        }

        var tagsList = (allTags as IList<Tag>) ?? allTags.ToList();
        var tagsDict = new Dictionary<string, Tag>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tagsList)
        {
            if (!tagsDict.ContainsKey(t.Name))
            {
                tagsDict[t.Name] = t;
            }
            var fullPath = TagHierarchyService.GetFullPath(t.Id, tagsList);
            if (!tagsDict.ContainsKey(fullPath))
            {
                tagsDict[fullPath] = t;
            }
            var slashPath = TagHierarchyService.GetFullPath(t.Id, tagsList, "/");
            if (!tagsDict.ContainsKey(slashPath))
            {
                tagsDict[slashPath] = t;
            }
        }

        var tokens = Tokenize(rawQuery, tagsDict);
        var hasConditions = tokens.Any(t => t.Type == TokenType.Condition);

        if (!hasConditions)
        {
            return new SearchQueryResult(
                rawQuery,
                rawQuery.Trim(),
                null,
                Array.Empty<SearchFilterChipModel>());
        }

        // Parse condition expressions
        var parser = new TokenParser(tokens);
        var condition = parser.ParseAllConditions();

        // Collect chips that were actually used
        var chips = new List<SearchFilterChipModel>();
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Type == TokenType.Condition && t.IsUsedInCondition && t.ChipModel != null)
            {
                // Check if this condition was negated by a preceding WITHOUT
                var chip = t.ChipModel;
                if (i > 0 && tokens[i - 1].Type == TokenType.Without && tokens[i - 1].IsUsedInCondition)
                {
                    var withoutToken = tokens[i - 1];
                    chip.IsNegated = true;
                    chip.DisplayText = $"WITHOUT {chip.DisplayText}";
                    int start = withoutToken.StartIndex;
                    int len = (t.StartIndex + t.Length) - start;
                    chip.StartIndex = start;
                    chip.Length = len;
                    chip.RawTokenText = rawQuery.Substring(start, len);
                }
                chips.Add(chip);
            }
        }

        // Extract free text from unused tokens
        var freeTextTokens = new List<string>();
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (!t.IsUsedInCondition)
            {
                // If it's an operator not used in conditions, only keep it if surrounded by words
                if (t.Type is TokenType.And or TokenType.Or or TokenType.Without)
                {
                    bool hasWordBefore = i > 0 && tokens[i - 1].Type == TokenType.Word && !tokens[i - 1].IsUsedInCondition;
                    bool hasWordAfter = i < tokens.Count - 1 && tokens[i + 1].Type == TokenType.Word && !tokens[i + 1].IsUsedInCondition;
                    if (hasWordBefore && hasWordAfter)
                    {
                        freeTextTokens.Add(t.RawText);
                    }
                }
                else if (t.Type == TokenType.Word)
                {
                    freeTextTokens.Add(t.RawText);
                }
            }
        }

        var freeText = string.Join(" ", freeTextTokens).Trim();

        return new SearchQueryResult(rawQuery, freeText, condition, chips);
    }

    public static string RemoveChip(string? query, SearchFilterChipModel chip)
    {
        if (string.IsNullOrWhiteSpace(query) || chip == null)
            return string.Empty;

        int start = chip.StartIndex;
        int len = chip.Length;

        // Verify bounds
        if (start < 0 || start + len > query.Length ||
            !string.Equals(query.Substring(start, len), chip.RawTokenText, StringComparison.OrdinalIgnoreCase))
        {
            // Fallback find
            start = query.IndexOf(chip.RawTokenText, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                start = query.IndexOf(chip.DisplayText, StringComparison.OrdinalIgnoreCase);
                if (start < 0) return query;
                len = chip.DisplayText.Length;
            }
            else
            {
                len = chip.RawTokenText.Length;
            }
        }

        int end = start + len;

        // Check if there is an operator immediately following the chip
        var after = query.Substring(end);
        var followingOpMatch = Regex.Match(after, @"^\s+(AND|OR|&&|\|\|)(?=\s|$)", RegexOptions.IgnoreCase);
        if (followingOpMatch.Success)
        {
            end += followingOpMatch.Length;
        }
        else
        {
            // Check if there is an operator immediately preceding the chip
            var before = query.Substring(0, start);
            var precedingOpMatch = Regex.Match(before, @"(?<=^|\s)(AND|OR|WITHOUT|NOT|&&|\|\||!)\s+$", RegexOptions.IgnoreCase);
            if (precedingOpMatch.Success)
            {
                start -= precedingOpMatch.Length;
            }
        }

        var result = query.Substring(0, start) + " " + query.Substring(end);

        // Remove empty parentheses if any were left behind
        result = Regex.Replace(result, @"\(\s*\)", " ");

        // Clean up leading/trailing operators
        result = Regex.Replace(result, @"^\s*(AND|OR|&&|\|\|)\s+", " ", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\s+(AND|OR|WITHOUT|NOT|&&|\|\||!)\s*$", " ", RegexOptions.IgnoreCase);

        // Normalize spaces
        result = Regex.Replace(result, @"\s{2,}", " ").Trim();

        return result;
    }

    private static List<QueryToken> Tokenize(string query, Dictionary<string, Tag> tagsDict)
    {
        var tokens = new List<QueryToken>();
        int i = 0;

        while (i < query.Length)
        {
            if (char.IsWhiteSpace(query[i]))
            {
                i++;
                continue;
            }

            if (query[i] == '(')
            {
                tokens.Add(new QueryToken { Type = TokenType.OpenParen, RawText = "(", StartIndex = i, Length = 1 });
                i++;
                continue;
            }

            if (query[i] == ')')
            {
                tokens.Add(new QueryToken { Type = TokenType.CloseParen, RawText = ")", StartIndex = i, Length = 1 });
                i++;
                continue;
            }

            // Quoted word: "some phrase"
            if (query[i] == '"')
            {
                int start = i;
                i++;
                while (i < query.Length && query[i] != '"') i++;
                if (i < query.Length && query[i] == '"') i++;
                int len = i - start;
                tokens.Add(new QueryToken { Type = TokenType.Word, RawText = query.Substring(start, len), StartIndex = start, Length = len });
                continue;
            }

            // CLI-style negation: -tag:Name is equivalent to WITHOUT tag:Name.
            if (query[i] == '-' && i + 1 < query.Length && MatchesConditionPrefix(query, i + 1))
            {
                tokens.Add(new QueryToken { Type = TokenType.Without, RawText = "-", StartIndex = i, Length = 1 });
                i++;
                continue;
            }

            // Check condition prefixes: tag:, created:, updated:, untagged:
            if (TryConsumeCondition(query, i, tagsDict, out var condToken, out int consumedLen))
            {
                tokens.Add(condToken);
                i += consumedLen;
                continue;
            }

            // Otherwise, read word until whitespace or parenthesis
            int wordStart = i;
            while (i < query.Length && !char.IsWhiteSpace(query[i]) && query[i] != '(' && query[i] != ')')
            {
                i++;
            }
            int wordLen = i - wordStart;
            string word = query.Substring(wordStart, wordLen);

            if (string.Equals(word, "AND", StringComparison.OrdinalIgnoreCase) || word == "&&")
            {
                tokens.Add(new QueryToken { Type = TokenType.And, RawText = word, StartIndex = wordStart, Length = wordLen });
            }
            else if (string.Equals(word, "OR", StringComparison.OrdinalIgnoreCase) || word == "||")
            {
                tokens.Add(new QueryToken { Type = TokenType.Or, RawText = word, StartIndex = wordStart, Length = wordLen });
            }
            else if (string.Equals(word, "WITHOUT", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(word, "NOT", StringComparison.OrdinalIgnoreCase) ||
                     word == "!")
            {
                tokens.Add(new QueryToken { Type = TokenType.Without, RawText = word, StartIndex = wordStart, Length = wordLen });
            }
            else
            {
                tokens.Add(new QueryToken { Type = TokenType.Word, RawText = word, StartIndex = wordStart, Length = wordLen });
            }
        }

        return tokens;
    }

    private static bool MatchesConditionPrefix(string query, int startIndex)
    {
        foreach (var p in ConditionPrefixes)
        {
            if (startIndex + p.Length <= query.Length &&
                string.Equals(query.Substring(startIndex, p.Length), p, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryConsumeCondition(
        string query,
        int startIndex,
        Dictionary<string, Tag> tagsDict,
        out QueryToken token,
        out int consumedLength)
    {
        token = null!;
        consumedLength = 0;

        string? matchedPrefix = null;

        foreach (var p in ConditionPrefixes)
        {
            if (startIndex + p.Length <= query.Length &&
                string.Equals(query.Substring(startIndex, p.Length), p, StringComparison.OrdinalIgnoreCase))
            {
                matchedPrefix = p;
                break;
            }
        }

        if (matchedPrefix == null)
            return false;

        int valStart = startIndex + matchedPrefix.Length;
        int valEnd = valStart;
        string value;

        if (valStart < query.Length && query[valStart] == '"')
        {
            // Quoted value
            int quoteStart = valStart + 1;
            int quoteEnd = quoteStart;
            while (quoteEnd < query.Length && query[quoteEnd] != '"') quoteEnd++;
            value = query.Substring(quoteStart, quoteEnd - quoteStart);
            valEnd = quoteEnd < query.Length ? quoteEnd + 1 : quoteEnd;
        }
        else
        {
            // Unquoted value
            while (valEnd < query.Length && !char.IsWhiteSpace(query[valEnd]) && query[valEnd] != '(' && query[valEnd] != ')')
            {
                valEnd++;
            }
            value = query.Substring(valStart, valEnd - valStart);
        }

        int totalLen = valEnd - startIndex;
        string rawText = query.Substring(startIndex, totalLen);

        if (matchedPrefix.Equals("tag:", StringComparison.OrdinalIgnoreCase))
        {
            if (tagsDict.TryGetValue(value, out var tag))
            {
                var cond = new TagCondition(tag.Name, tag.Id);
                var chip = new SearchFilterChipModel
                {
                    Type = ChipType.Tag,
                    DisplayText = $"tag:{tag.Name}",
                    RawTokenText = rawText,
                    StartIndex = startIndex,
                    Length = totalLen,
                    Condition = cond
                };
                token = new QueryToken
                {
                    Type = TokenType.Condition,
                    RawText = rawText,
                    StartIndex = startIndex,
                    Length = totalLen,
                    ConditionPayload = cond,
                    ChipModel = chip
                };
                consumedLength = totalLen;
                return true;
            }
            // Unknown tag: treated as Word
            token = new QueryToken
            {
                Type = TokenType.Word,
                RawText = rawText,
                StartIndex = startIndex,
                Length = totalLen
            };
            consumedLength = totalLen;
            return true;
        }

        if (matchedPrefix.Equals("created:", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(value, "today", StringComparison.OrdinalIgnoreCase))
            {
                var cond = new CreatedCondition(DateFilterType.Today);
                var chip = new SearchFilterChipModel
                {
                    Type = ChipType.Created,
                    DisplayText = "created:today",
                    RawTokenText = rawText,
                    StartIndex = startIndex,
                    Length = totalLen,
                    Condition = cond
                };
                token = new QueryToken
                {
                    Type = TokenType.Condition,
                    RawText = rawText,
                    StartIndex = startIndex,
                    Length = totalLen,
                    ConditionPayload = cond,
                    ChipModel = chip
                };
                consumedLength = totalLen;
                return true;
            }
            if (string.Equals(value, "week", StringComparison.OrdinalIgnoreCase))
            {
                var cond = new CreatedCondition(DateFilterType.Week);
                var chip = new SearchFilterChipModel
                {
                    Type = ChipType.Created,
                    DisplayText = "created:week",
                    RawTokenText = rawText,
                    StartIndex = startIndex,
                    Length = totalLen,
                    Condition = cond
                };
                token = new QueryToken
                {
                    Type = TokenType.Condition,
                    RawText = rawText,
                    StartIndex = startIndex,
                    Length = totalLen,
                    ConditionPayload = cond,
                    ChipModel = chip
                };
                consumedLength = totalLen;
                return true;
            }

            token = new QueryToken { Type = TokenType.Word, RawText = rawText, StartIndex = startIndex, Length = totalLen };
            consumedLength = totalLen;
            return true;
        }

        if (matchedPrefix.Equals("updated:", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(value, "week", StringComparison.OrdinalIgnoreCase))
            {
                var cond = new UpdatedCondition(DateFilterType.Week);
                var chip = new SearchFilterChipModel
                {
                    Type = ChipType.Updated,
                    DisplayText = "updated:week",
                    RawTokenText = rawText,
                    StartIndex = startIndex,
                    Length = totalLen,
                    Condition = cond
                };
                token = new QueryToken
                {
                    Type = TokenType.Condition,
                    RawText = rawText,
                    StartIndex = startIndex,
                    Length = totalLen,
                    ConditionPayload = cond,
                    ChipModel = chip
                };
                consumedLength = totalLen;
                return true;
            }
            if (string.Equals(value, "today", StringComparison.OrdinalIgnoreCase))
            {
                var cond = new UpdatedCondition(DateFilterType.Today);
                var chip = new SearchFilterChipModel
                {
                    Type = ChipType.Updated,
                    DisplayText = "updated:today",
                    RawTokenText = rawText,
                    StartIndex = startIndex,
                    Length = totalLen,
                    Condition = cond
                };
                token = new QueryToken
                {
                    Type = TokenType.Condition,
                    RawText = rawText,
                    StartIndex = startIndex,
                    Length = totalLen,
                    ConditionPayload = cond,
                    ChipModel = chip
                };
                consumedLength = totalLen;
                return true;
            }

            token = new QueryToken { Type = TokenType.Word, RawText = rawText, StartIndex = startIndex, Length = totalLen };
            consumedLength = totalLen;
            return true;
        }

        if (matchedPrefix.Equals("untagged:", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1" || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase))
            {
                var cond = new UntaggedCondition(true);
                var chip = new SearchFilterChipModel
                {
                    Type = ChipType.Untagged,
                    DisplayText = "untagged:true",
                    RawTokenText = rawText,
                    StartIndex = startIndex,
                    Length = totalLen,
                    Condition = cond
                };
                token = new QueryToken
                {
                    Type = TokenType.Condition,
                    RawText = rawText,
                    StartIndex = startIndex,
                    Length = totalLen,
                    ConditionPayload = cond,
                    ChipModel = chip
                };
                consumedLength = totalLen;
                return true;
            }
            if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) || value == "0" || string.Equals(value, "no", StringComparison.OrdinalIgnoreCase))
            {
                var cond = new UntaggedCondition(false);
                var chip = new SearchFilterChipModel
                {
                    Type = ChipType.Untagged,
                    DisplayText = "untagged:false",
                    RawTokenText = rawText,
                    StartIndex = startIndex,
                    Length = totalLen,
                    Condition = cond
                };
                token = new QueryToken
                {
                    Type = TokenType.Condition,
                    RawText = rawText,
                    StartIndex = startIndex,
                    Length = totalLen,
                    ConditionPayload = cond,
                    ChipModel = chip
                };
                consumedLength = totalLen;
                return true;
            }

            token = new QueryToken { Type = TokenType.Word, RawText = rawText, StartIndex = startIndex, Length = totalLen };
            consumedLength = totalLen;
            return true;
        }

        if (matchedPrefix.Equals("source:", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
        {
            var cond = new SourceCondition(value);
            var chip = new SearchFilterChipModel
            {
                Type = ChipType.Source,
                DisplayText = $"source:{value}",
                RawTokenText = rawText,
                StartIndex = startIndex,
                Length = totalLen,
                Condition = cond
            };
            token = new QueryToken
            {
                Type = TokenType.Condition,
                RawText = rawText,
                StartIndex = startIndex,
                Length = totalLen,
                ConditionPayload = cond,
                ChipModel = chip
            };
            consumedLength = totalLen;
            return true;
        }

        if (matchedPrefix.Equals("url:", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
        {
            var cond = new UrlCondition(value);
            var chip = new SearchFilterChipModel
            {
                Type = ChipType.Url,
                DisplayText = $"url:{value}",
                RawTokenText = rawText,
                StartIndex = startIndex,
                Length = totalLen,
                Condition = cond
            };
            token = new QueryToken
            {
                Type = TokenType.Condition,
                RawText = rawText,
                StartIndex = startIndex,
                Length = totalLen,
                ConditionPayload = cond,
                ChipModel = chip
            };
            consumedLength = totalLen;
            return true;
        }

        return false;
    }

    private sealed class TokenParser
    {
        private readonly List<QueryToken> _tokens;
        private int _pos;

        public TokenParser(List<QueryToken> tokens)
        {
            _tokens = tokens;
            _pos = 0;
        }

        public SearchCondition? ParseAllConditions()
        {
            SearchCondition? root = null;

            while (_pos < _tokens.Count)
            {
                if (CanStartCondition(_tokens[_pos]))
                {
                    var cond = ParseOr();
                    if (cond != null)
                    {
                        root = root == null ? cond : new AndCondition(root, cond);
                    }
                    else
                    {
                        _pos++;
                    }
                }
                else
                {
                    _pos++;
                }
            }

            return root;
        }

        private QueryToken? Peek(int offset = 0) =>
            _pos + offset < _tokens.Count ? _tokens[_pos + offset] : null;

        private QueryToken Consume() => _tokens[_pos++];

        private SearchCondition? ParseOr()
        {
            var left = ParseAnd();
            if (left == null) return null;

            while (Peek()?.Type == TokenType.Or)
            {
                var nextAfterOr = Peek(1);
                if (nextAfterOr != null && CanStartCondition(nextAfterOr))
                {
                    var orToken = Consume();
                    var right = ParseAnd();
                    if (right != null)
                    {
                        orToken.IsUsedInCondition = true;
                        left = new OrCondition(left, right);
                    }
                    else
                    {
                        break;
                    }
                }
                else
                {
                    break;
                }
            }

            return left;
        }

        private SearchCondition? ParseAnd()
        {
            var left = ParseWithout();
            if (left == null) return null;

            while (true)
            {
                var next = Peek();
                if (next == null) break;

                if (next.Type == TokenType.And)
                {
                    var nextAfterAnd = Peek(1);
                    if (nextAfterAnd != null && CanStartCondition(nextAfterAnd))
                    {
                        var andToken = Consume();
                        var right = ParseWithout();
                        if (right != null)
                        {
                            andToken.IsUsedInCondition = true;
                            left = new AndCondition(left, right);
                        }
                        else
                        {
                            break;
                        }
                    }
                    else
                    {
                        break;
                    }
                }
                else if (CanStartCondition(next))
                {
                    // Implicit AND between conditions
                    var right = ParseWithout();
                    if (right != null)
                    {
                        left = new AndCondition(left, right);
                    }
                    else
                    {
                        break;
                    }
                }
                else
                {
                    break;
                }
            }

            return left;
        }

        private SearchCondition? ParseWithout()
        {
            var next = Peek();
            if (next?.Type == TokenType.Without)
            {
                var nextAfterWithout = Peek(1);
                if (nextAfterWithout != null && CanStartCondition(nextAfterWithout))
                {
                    var withoutToken = Consume();
                    var right = ParsePrimary();
                    if (right != null)
                    {
                        withoutToken.IsUsedInCondition = true;
                        return new NotCondition(right);
                    }
                }
                return null;
            }

            var left = ParsePrimary();
            if (left == null) return null;

            while (Peek()?.Type == TokenType.Without)
            {
                var nextAfterWithout = Peek(1);
                if (nextAfterWithout != null && CanStartCondition(nextAfterWithout))
                {
                    var withoutToken = Consume();
                    var right = ParsePrimary();
                    if (right != null)
                    {
                        withoutToken.IsUsedInCondition = true;
                        left = new AndCondition(left, new NotCondition(right));
                    }
                    else
                    {
                        break;
                    }
                }
                else
                {
                    break;
                }
            }

            return left;
        }

        private SearchCondition? ParsePrimary()
        {
            var next = Peek();
            if (next == null) return null;

            if (next.Type == TokenType.OpenParen)
            {
                var openToken = Consume();
                var expr = ParseOr();
                if (expr != null)
                {
                    openToken.IsUsedInCondition = true;
                    if (Peek()?.Type == TokenType.CloseParen)
                    {
                        var closeToken = Consume();
                        closeToken.IsUsedInCondition = true;
                    }
                    return expr;
                }
                return null;
            }

            if (next.Type == TokenType.Condition && next.ConditionPayload != null)
            {
                var condToken = Consume();
                condToken.IsUsedInCondition = true;
                return condToken.ConditionPayload;
            }

            return null;
        }

        private static bool CanStartCondition(QueryToken token)
        {
            return token.Type is TokenType.Condition or TokenType.Without or TokenType.OpenParen;
        }
    }
}
