using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using QuickNotes.App.Models;

namespace QuickNotes.App.Services;

public class TagSuggestionService
{
    private static readonly Regex UrlRegex = new(
        @"https?://\S+|ftp://\S+|www\.\S+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex BareDomainRegex = new(
        @"(?<![\p{L}\p{N}_@])(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,}(?:/[^\s]*)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex MarkdownLinkRegex = new(
        @"\[([^\]]+)\]\([^\)]+\)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TokenRegex = new(
        @"(?<![\p{L}\p{N}_])\.net(?![\p{L}\p{N}_])|" +
        @"(?<![\p{L}\p{N}_])[a-zA-Zа-яА-ЯёЁ][#+]{1,2}(?![\p{L}\p{N}_#+])|" +
        @"(?<![\p{L}\p{N}_])\d+[a-zA-Zа-яА-ЯёЁ]+(?![\p{L}\p{N}_])|" +
        @"\b[\p{L}][\p{L}\p{N}]*(?:-[\p{L}\p{N}]+)*\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> AllowedShortTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "c#", "c++", ".net", "1c", "1с", "f#", "ai", "ui", "ux", "db", "go", "qa", "ci", "cd", "os", "ip", "pr"
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // Russian prepositions, particles, conjunctions
        "в", "во", "на", "с", "со", "по", "к", "ко", "у", "о", "об", "обо", "за", "из", "изо", "от", "ото",
        "до", "для", "без", "безо", "под", "подо", "над", "надо", "перед", "передо", "при", "про", "через",
        "чрез", "после", "сквозь", "между", "около", "вокруг", "среди", "возле", "вместо", "кроме", "ради",
        "насчет", "навстречу", "и", "а", "но", "да", "или", "либо", "как", "что", "чтобы", "чтоб", "если",
        "то", "хотя", "будто", "словно", "когда", "пока", "так", "также", "тоже", "зато", "причем", "притом",
        "едва", "лишь", "не", "ни", "бы", "б", "ли", "ль", "же", "ж", "вот", "вон", "даже", "только", "ведь",
        "уже", "уж", "разве", "неужели", "прямо", "точно", "вроде", "просто", "авось", "вряд",

        // Russian pronouns
        "я", "ты", "он", "она", "оно", "они", "мы", "вы", "мой", "моя", "мое", "моё", "мои", "моего", "моему",
        "моем", "моём", "твой", "твоя", "твое", "твоё", "твои", "твоего", "твоему", "твоем", "твоём", "свой",
        "своя", "свое", "своё", "свои", "своего", "своему", "своем", "своём", "наш", "наша", "наше", "наши",
        "нашего", "нашему", "нашем", "ваш", "ваша", "ваше", "ваши", "вашего", "вашему", "вашем", "его", "ее",
        "её", "их", "этот", "эта", "это", "эти", "этого", "этому", "этим", "этом", "этой", "этих", "тот", "та",
        "то", "те", "того", "тому", "тем", "том", "той", "тех", "такой", "такая", "такое", "такие", "такого",
        "такому", "таким", "таких", "весь", "вся", "всё", "все", "всего", "всех", "всему", "всем", "всеми",
        "сам", "сама", "само", "сами", "самый", "самая", "самое", "самые", "кто", "кого", "кому", "кем", "ком",
        "чего", "чему", "чем", "какой", "какая", "какое", "какие", "чей", "чья", "чье", "чьё", "чьи",
        "который", "которая", "которое", "которые", "которого", "которой", "которых", "которым", "сколько",
        "никто", "ничто", "никакой", "никакая", "никакое", "никакие", "некто", "нечто", "некоторый",
        "некоторые", "некий", "некая", "некое", "каждый", "каждая", "каждое", "каждые", "любой", "любая",
        "любое", "любые", "иной", "иная", "иное", "иные", "другой", "другая", "другое", "другие", "мне",
        "меня", "мной", "мною", "тебе", "тебя", "тобой", "тобою", "ему", "него", "нему", "ним", "нем", "нём",
        "ей", "нее", "неё", "ней", "нею", "нам", "нас", "нами", "вам", "вас", "вами", "ими", "них",

        // Russian verbs & auxiliaries & common particles
        "быть", "был", "была", "было", "были", "будет", "будут", "буду", "будем", "будете", "будешь",
        "есть", "нет", "может", "можно", "нужно", "надо", "очень", "здесь", "там", "тут", "где", "куда",
        "откуда", "почему", "зачем", "поэтому", "потому", "тогда", "сейчас", "теперь", "еще", "ещё",
        "снова", "опять", "много", "мало", "более", "менее", "всегда", "никогда", "вдруг", "стал", "стала",
        "стало", "стали", "стать", "хочет", "хотят", "хотел", "хотела",

        // English prepositions, conjunctions, articles
        "in", "on", "at", "by", "for", "with", "about", "against", "between", "into", "through", "during",
        "before", "after", "above", "below", "to", "from", "up", "down", "of", "off", "over", "under", "via",
        "onto", "and", "but", "or", "nor", "so", "yet", "although", "because", "since", "unless", "while",
        "whereas", "a", "an", "the",

        // English pronouns
        "i", "you", "he", "she", "it", "we", "they", "me", "him", "her", "us", "them", "my", "your", "his",
        "its", "our", "their", "mine", "yours", "ours", "theirs", "myself", "yourself", "himself", "herself",
        "itself", "ourselves", "themselves", "this", "that", "these", "those", "who", "whom", "whose", "which",
        "what", "whatever", "whoever",

        // English auxiliaries & common verbs
        "is", "am", "are", "was", "were", "be", "been", "being", "have", "has", "had", "having", "do", "does",
        "did", "doing", "can", "could", "shall", "should", "will", "would", "may", "might", "must",

        // English adverbs & contractions
        "not", "no", "only", "same", "than", "too", "very", "just", "now", "then", "there", "here", "when",
        "where", "why", "how", "all", "any", "both", "each", "few", "more", "most", "other", "some", "such",
        "also", "well", "even", "don't", "doesn't", "didn't", "won't", "can't", "couldn't", "isn't", "aren't"
    };

    private sealed class WordAccumulator
    {
        public string Word { get; }
        public List<int> NoteIds { get; } = new();
        public string SampleSnippet { get; set; } = string.Empty;

        public WordAccumulator(string word)
        {
            Word = word;
        }
    }

    /// <summary>
    /// Suggests new tags based on terms repeated across multiple distinct active notes.
    /// Does not mutate notes or database state.
    /// </summary>
    public List<TagSuggestionItem> SuggestTags(
        IEnumerable<Note> notes,
        IEnumerable<Tag> existingTags,
        TagSuggestionOptions? options = null)
    {
        options ??= new TagSuggestionOptions();
        return SuggestTags(notes, existingTags, options.MinNotesCount, options.MaxSuggestions);
    }

    /// <summary>
    /// Suggests new tags based on terms repeated across multiple distinct active notes.
    /// Does not mutate notes or database state.
    /// </summary>
    public List<TagSuggestionItem> SuggestTags(
        IEnumerable<Note> notes,
        IEnumerable<Tag> existingTags,
        int minNotesCount,
        int maxSuggestions = 50)
    {
        if (notes == null)
            return new List<TagSuggestionItem>();

        minNotesCount = Math.Max(1, minNotesCount);
        maxSuggestions = Math.Max(0, maxSuggestions);

        var activeNotes = notes
            .Where(n => n != null && n.DeletedAt == null && !string.IsNullOrWhiteSpace(n.Text))
            .ToList();

        // A database query normally returns each note once, but callers may pass a
        // concatenated collection. Count each persisted note only once while
        // keeping separate unsaved Note instances distinct (their ID is still 0).
        var seenIds = new HashSet<int>();
        var seenUnsaved = new HashSet<Note>();
        activeNotes = activeNotes
            .Where(n => n.Id > 0 ? seenIds.Add(n.Id) : seenUnsaved.Add(n))
            .ToList();

        if (activeNotes.Count == 0)
            return new List<TagSuggestionItem>();

        var existingTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (existingTags != null)
        {
            foreach (var tag in existingTags)
            {
                if (tag == null) continue;
                if (!string.IsNullOrWhiteSpace(tag.Name))
                {
                    existingTerms.Add(TagDetectionService.NormalizeTerm(tag.Name));
                }
                if (tag.Synonyms != null)
                {
                    foreach (var syn in tag.Synonyms)
                    {
                        if (!string.IsNullOrWhiteSpace(syn.Value))
                        {
                            existingTerms.Add(TagDetectionService.NormalizeTerm(syn.Value));
                        }
                    }
                }
            }
        }

        var wordMap = new Dictionary<string, WordAccumulator>(StringComparer.OrdinalIgnoreCase);

        foreach (var note in activeNotes)
        {
            var tokensInNote = ExtractTokensFromNote(note.Text);
            var distinctTokensInNote = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var rawToken in tokensInNote)
            {
                if (TryNormalizeToken(rawToken, existingTerms, out var normalizedWord))
                {
                    distinctTokensInNote.Add(normalizedWord);
                }
            }

            foreach (var word in distinctTokensInNote)
            {
                if (!wordMap.TryGetValue(word, out var acc))
                {
                    acc = new WordAccumulator(word);
                    wordMap[word] = acc;
                }

                acc.NoteIds.Add(note.Id);
                if (string.IsNullOrEmpty(acc.SampleSnippet))
                {
                    acc.SampleSnippet = ExtractPreviewSnippet(note.Text, word);
                }
            }
        }

        return wordMap.Values
            .Where(acc => acc.NoteIds.Count >= minNotesCount)
            .OrderByDescending(acc => acc.NoteIds.Count)
            .ThenBy(acc => acc.Word, StringComparer.OrdinalIgnoreCase)
            .Take(maxSuggestions)
            .Select(acc => new TagSuggestionItem
            {
                Word = acc.Word,
                NotesCount = acc.NoteIds.Count,
                Preview = acc.SampleSnippet,
                NoteIds = acc.NoteIds
            })
            .ToList();
    }

    public static List<string> ExtractTokensFromNote(string text)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return list;

        // 1. Strip URLs to prevent URL domain/path tokens from leaking
        string cleaned = UrlRegex.Replace(text, " ");

        // Also strip bare domains such as github.com when a note omits the URL
        // scheme. This prevents domain fragments from becoming suggestions.
        cleaned = BareDomainRegex.Replace(cleaned, " ");

        // 2. Strip Markdown link targets: [Text](URL) -> Text
        cleaned = MarkdownLinkRegex.Replace(cleaned, "");

        // 3. Extract word and technical tokens
        var matches = TokenRegex.Matches(cleaned);
        foreach (Match m in matches)
        {
            if (m.Success && !string.IsNullOrWhiteSpace(m.Value))
            {
                list.Add(m.Value);
            }
        }

        return list;
    }

    public static bool TryNormalizeToken(string rawToken, HashSet<string> existingTerms, out string normalizedWord)
    {
        normalizedWord = string.Empty;
        if (string.IsNullOrWhiteSpace(rawToken))
            return false;

        string trimmed = rawToken.Trim();
        string lower = trimmed.ToLowerInvariant();

        // Canonical form for recognized technical tokens
        string word;
        if (lower == "c#")
            word = "C#";
        else if (lower == "c++")
            word = "C++";
        else if (lower == ".net")
            word = ".NET";
        else if (lower is "1c" or "1с") // Latin C or Cyrillic С
            word = "1C";
        else if (lower == "f#")
            word = "F#";
        else
            word = lower;

        // Filter out pure numbers
        if (Regex.IsMatch(word, @"^\d+$"))
            return false;

        // Filter out short noise (< 3 chars unless in allowed short technical tokens)
        if (word.Length < 3 && !AllowedShortTokens.Contains(word))
            return false;

        // Filter out stop words / service words
        if (StopWords.Contains(word))
            return false;

        // Filter out already existing tag names and synonyms
        if (existingTerms.Contains(word))
            return false;

        normalizedWord = word;
        return true;
    }

    public static string ExtractPreviewSnippet(string text, string word, int maxLength = 90)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        string singleLine = Regex.Replace(text, @"\s+", " ").Trim();
        if (singleLine.Length <= maxLength)
            return singleLine;

        int idx = singleLine.IndexOf(word, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return singleLine.Substring(0, Math.Min(singleLine.Length, maxLength)).Trim() + "...";
        }

        int half = (maxLength - word.Length) / 2;
        int start = Math.Max(0, idx - half);
        int length = Math.Min(singleLine.Length - start, maxLength);

        string snippet = singleLine.Substring(start, length).Trim();
        if (start > 0)
            snippet = "..." + snippet;
        if (start + length < singleLine.Length)
            snippet = snippet + "...";

        return snippet;
    }
}
