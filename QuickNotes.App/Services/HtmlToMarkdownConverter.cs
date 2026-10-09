using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace QuickNotes.App.Services;

public sealed class HtmlConversionResult
{
    public string Markdown { get; set; } = string.Empty;
    public List<string> LostElements { get; } = new();
    public List<string> BlockingReasons { get; } = new();
    public List<HtmlLocalAttachmentRef> LocalAttachments { get; } = new();
    public bool HasBlocking => BlockingReasons.Count > 0;
}

public sealed class HtmlLocalAttachmentRef
{
    public string RawSrc { get; init; } = string.Empty;
    public string Alt { get; init; } = string.Empty;
}

public static class HtmlToMarkdownConverter
{
    private static readonly HashSet<string> ForbiddenSkipInner = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "iframe", "object", "embed", "applet", "link", "meta", "base", "form"
    };

    public static HtmlConversionResult Convert(string html)
    {
        var result = new HtmlConversionResult();
        if (string.IsNullOrEmpty(html))
        {
            return result;
        }

        if (html.Length > ImportLimits.MaxDecodedTextChars)
        {
            result.BlockingReasons.Add("HTML превышает лимит размера декодированного текста.");
            return result;
        }

        var tokens = Tokenize(html, result);
        if (result.HasBlocking)
        {
            return result;
        }

        var ctx = new RenderContext(result);
        Walk(tokens, 0, tokens.Count, ctx);
        string markdown = ctx.Builder.ToString().Trim();
        if (markdown.Length > ImportLimits.MaxDecodedTextChars)
        {
            result.BlockingReasons.Add("Преобразованный Markdown превышает лимит размера.");
            result.Markdown = string.Empty;
            return result;
        }

        result.Markdown = markdown;
        return result;
    }

    private static void Walk(List<HtmlToken> tokens, int start, int end, RenderContext ctx)
    {
        int i = start;
        while (i < end)
        {
            var token = tokens[i];
            if (token.Kind == HtmlTokenKind.Text)
            {
                ctx.AppendText(token.Text);
                i++;
                continue;
            }

            if (token.Kind == HtmlTokenKind.Close)
            {
                i++;
                continue;
            }

            int close = token.Kind == HtmlTokenKind.Empty ? i : FindMatchingClose(tokens, i, end);
            int innerStart = i + 1;
            int innerEnd = token.Kind == HtmlTokenKind.Empty ? i : close;
            string name = token.Name;

            switch (name)
            {
                case "h1":
                case "h2":
                case "h3":
                case "h4":
                case "h5":
                case "h6":
                    ctx.EnsureBlankLine();
                    ctx.Builder.Append(new string('#', name[1] - '0')).Append(' ');
                    Walk(tokens, innerStart, innerEnd, ctx);
                    ctx.EnsureBlankLine();
                    break;
                case "p":
                case "div":
                case "section":
                case "article":
                    ctx.EnsureBlankLine();
                    Walk(tokens, innerStart, innerEnd, ctx);
                    ctx.EnsureBlankLine();
                    break;
                case "br":
                    ctx.Builder.AppendLine();
                    break;
                case "strong":
                case "b":
                    ctx.Builder.Append("**");
                    Walk(tokens, innerStart, innerEnd, ctx);
                    ctx.Builder.Append("**");
                    break;
                case "em":
                case "i":
                    ctx.Builder.Append('*');
                    Walk(tokens, innerStart, innerEnd, ctx);
                    ctx.Builder.Append('*');
                    break;
                case "code":
                    if (ctx.InPre)
                    {
                        Walk(tokens, innerStart, innerEnd, ctx);
                    }
                    else
                    {
                        ctx.Builder.Append('`');
                        Walk(tokens, innerStart, innerEnd, ctx);
                        ctx.Builder.Append('`');
                    }
                    break;
                case "pre":
                    ctx.EnsureBlankLine();
                    ctx.Builder.AppendLine("```");
                    ctx.InPre = true;
                    Walk(tokens, innerStart, innerEnd, ctx);
                    ctx.InPre = false;
                    ctx.EnsureNewline();
                    ctx.Builder.AppendLine("```");
                    ctx.EnsureBlankLine();
                    break;
                case "ul":
                case "ol":
                    ctx.Lists.Push(new ListState { Ordered = name == "ol", Index = 1 });
                    ctx.EnsureBlankLine();
                    Walk(tokens, innerStart, innerEnd, ctx);
                    ctx.Lists.Pop();
                    ctx.EnsureBlankLine();
                    break;
                case "li":
                    RenderListItem(tokens, innerStart, innerEnd, ctx);
                    break;
                case "blockquote":
                    ctx.EnsureBlankLine();
                    int quoteAt = ctx.Builder.Length;
                    Walk(tokens, innerStart, innerEnd, ctx);
                    PrefixBlock(ctx.Builder, quoteAt, "> ");
                    ctx.EnsureBlankLine();
                    break;
                case "a":
                    RenderAnchor(token, tokens, innerStart, innerEnd, ctx);
                    break;
                case "img":
                    RenderImage(token, ctx);
                    break;
                case "table":
                    RenderTable(tokens, innerStart, innerEnd, ctx);
                    break;
                case "hr":
                    ctx.EnsureBlankLine();
                    ctx.Builder.AppendLine("---");
                    ctx.EnsureBlankLine();
                    break;
                case "input":
                    break;
                case "html":
                case "body":
                case "head":
                case "span":
                case "font":
                case "center":
                case "tbody":
                case "thead":
                    Walk(tokens, innerStart, innerEnd, ctx);
                    break;
                default:
                    if (name.Contains(':', StringComparison.Ordinal))
                    {
                        Walk(tokens, innerStart, innerEnd, ctx);
                    }
                    else
                    {
                        ctx.Result.LostElements.Add($"Элемент <{name}> преобразован как простой текст.");
                        Walk(tokens, innerStart, innerEnd, ctx);
                    }
                    break;
            }

            i = token.Kind == HtmlTokenKind.Empty ? i + 1 : close + 1;
        }
    }

    private static void RenderListItem(List<HtmlToken> tokens, int innerStart, int innerEnd, RenderContext ctx)
    {
        var state = ctx.Lists.Count > 0 ? ctx.Lists.Peek() : new ListState { Ordered = false, Index = 1 };
        string indent = new string(' ', Math.Max(0, ctx.Lists.Count - 1) * 2);
        ctx.EnsureNewline();

        bool checkbox = false;
        bool isChecked = false;
        int walkFrom = innerStart;
        if (innerStart < innerEnd && string.Equals(tokens[innerStart].Name, "input", StringComparison.OrdinalIgnoreCase))
        {
            var attrs = tokens[innerStart].Attributes;
            if (attrs.TryGetValue("type", out var type) && type.Equals("checkbox", StringComparison.OrdinalIgnoreCase))
            {
                checkbox = true;
                isChecked = attrs.ContainsKey("checked");
                walkFrom = tokens[innerStart].Kind == HtmlTokenKind.Empty
                    ? innerStart + 1
                    : FindMatchingClose(tokens, innerStart, innerEnd) + 1;
            }
        }

        if (checkbox)
        {
            ctx.Builder.Append(indent).Append("- [").Append(isChecked ? "x" : " ").Append("] ");
        }
        else if (state.Ordered)
        {
            ctx.Builder.Append(indent).Append(state.Index).Append(". ");
            state.Index++;
        }
        else
        {
            ctx.Builder.Append(indent).Append("- ");
        }

        Walk(tokens, walkFrom, innerEnd, ctx);
    }

    private static void RenderAnchor(HtmlToken token, List<HtmlToken> tokens, int innerStart, int innerEnd, RenderContext ctx)
    {
        token.Attributes.TryGetValue("href", out var href);
        int mark = ctx.Builder.Length;
        Walk(tokens, innerStart, innerEnd, ctx);
        string inner = ctx.Builder.ToString(mark, ctx.Builder.Length - mark);
        ctx.Builder.Length = mark;

        if (string.IsNullOrWhiteSpace(href))
        {
            ctx.Builder.Append(inner);
            return;
        }

        href = href.Trim();
        bool mailto = href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);
        bool relative = !href.Contains("://", StringComparison.Ordinal)
                        && !ImportPathSafety.IsRejectedExternalOrUnsafeUri(href);
        if (mailto || relative)
        {
            ctx.Builder.Append('[').Append(inner).Append("](").Append(href).Append(')');
            return;
        }

        ctx.Result.LostElements.Add("Внешняя или небезопасная ссылка отброшена.");
        ctx.Builder.Append(inner);
    }

    private static void RenderImage(HtmlToken token, RenderContext ctx)
    {
        token.Attributes.TryGetValue("src", out var src);
        token.Attributes.TryGetValue("alt", out var alt);
        alt ??= string.Empty;
        if (string.IsNullOrWhiteSpace(src)
            || src.Contains("://", StringComparison.Ordinal)
            || src.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || ImportPathSafety.IsRejectedExternalOrUnsafeUri(src))
        {
            ctx.Result.LostElements.Add("Внешнее, data: или небезопасное изображение отброшено.");
            return;
        }

        src = src.Trim();
        ctx.Result.LocalAttachments.Add(new HtmlLocalAttachmentRef { RawSrc = src, Alt = alt });
        ctx.Builder.Append("![").Append(alt).Append("](").Append(src).Append(')');
    }

    private static void RenderTable(List<HtmlToken> tokens, int innerStart, int innerEnd, RenderContext ctx)
    {
        var rows = new List<List<string>>();
        int i = innerStart;
        while (i < innerEnd)
        {
            var t = tokens[i];
            if (t.Kind == HtmlTokenKind.Open && t.Name is "thead" or "tbody" or "tfoot")
            {
                i++;
                continue;
            }

            if ((t.Kind == HtmlTokenKind.Open || t.Kind == HtmlTokenKind.Empty) && t.Name == "tr")
            {
                int trClose = t.Kind == HtmlTokenKind.Empty ? i : FindMatchingClose(tokens, i, innerEnd);
                var cells = new List<string>();
                int c = i + 1;
                while (c < trClose)
                {
                    var cell = tokens[c];
                    if ((cell.Kind == HtmlTokenKind.Open || cell.Kind == HtmlTokenKind.Empty) && cell.Name is "td" or "th")
                    {
                        int cellClose = cell.Kind == HtmlTokenKind.Empty ? c : FindMatchingClose(tokens, c, trClose);
                        int mark = ctx.Builder.Length;
                        Walk(tokens, c + 1, cellClose, ctx);
                        string text = ctx.Builder.ToString(mark, ctx.Builder.Length - mark)
                            .Replace("|", "\\|")
                            .Replace("\r", " ")
                            .Replace('\n', ' ')
                            .Trim();
                        ctx.Builder.Length = mark;
                        cells.Add(text);
                        c = cell.Kind == HtmlTokenKind.Empty ? c + 1 : cellClose + 1;
                        continue;
                    }

                    c++;
                }

                if (cells.Count > 0)
                {
                    rows.Add(cells);
                }

                i = t.Kind == HtmlTokenKind.Empty ? i + 1 : trClose + 1;
                continue;
            }

            i++;
        }

        if (rows.Count == 0)
        {
            ctx.Result.LostElements.Add("Пустая таблица отброшена.");
            return;
        }

        int cols = 0;
        foreach (var row in rows)
        {
            cols = Math.Max(cols, row.Count);
        }

        foreach (var row in rows)
        {
            while (row.Count < cols)
            {
                row.Add(string.Empty);
            }
        }

        ctx.EnsureBlankLine();
        ctx.Builder.Append("| ").Append(string.Join(" | ", rows[0])).AppendLine(" |");
        ctx.Builder.Append("| ");
        for (int c = 0; c < cols; c++)
        {
            if (c > 0)
            {
                ctx.Builder.Append(" | ");
            }

            ctx.Builder.Append("---");
        }

        ctx.Builder.AppendLine(" |");
        for (int r = 1; r < rows.Count; r++)
        {
            ctx.Builder.Append("| ").Append(string.Join(" | ", rows[r])).AppendLine(" |");
        }

        ctx.EnsureBlankLine();
    }

    private static void PrefixBlock(StringBuilder sb, int start, string prefix)
    {
        string block = sb.ToString(start, sb.Length - start).Trim('\r', '\n');
        sb.Length = start;
        if (block.Length == 0)
        {
            sb.Append(prefix).AppendLine();
            return;
        }

        foreach (var line in block.Replace("\r\n", "\n").Split('\n'))
        {
            sb.Append(prefix).AppendLine(line);
        }
    }

    private static int FindMatchingClose(List<HtmlToken> tokens, int openIndex, int end)
    {
        string name = tokens[openIndex].Name;
        if (tokens[openIndex].Kind == HtmlTokenKind.Empty)
        {
            return openIndex;
        }

        int depth = 0;
        for (int i = openIndex; i < end; i++)
        {
            var t = tokens[i];
            if (t.Kind == HtmlTokenKind.Open && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                depth++;
            }
            else if (t.Kind == HtmlTokenKind.Close && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return end - 1;
    }

    private static List<HtmlToken> Tokenize(string html, HtmlConversionResult result)
    {
        var tokens = new List<HtmlToken>();
        int i = 0;
        int depth = 0;
        while (i < html.Length)
        {
            if (html[i] == '<')
            {
                if (StartsWith(html, i, "<!--"))
                {
                    int cend = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                    if (cend < 0)
                    {
                        result.LostElements.Add("Незакрытый HTML-комментарий отброшен.");
                        break;
                    }

                    i = cend + 3;
                    continue;
                }

                if (StartsWithIgnoreCase(html, i, "<!doctype") || StartsWithIgnoreCase(html, i, "<![cdata["))
                {
                    int gt = html.IndexOf('>', i + 2);
                    i = gt < 0 ? html.Length : gt + 1;
                    continue;
                }

                int close = FindTagEnd(html, i);
                if (close < 0)
                {
                    result.LostElements.Add("Повреждённый HTML-тег отброшен.");
                    break;
                }

                string raw = html.Substring(i + 1, close - i - 1).Trim();
                bool isClose = raw.StartsWith('/');
                bool selfClose = raw.EndsWith('/');
                string inner = raw.Trim().Trim('/');
                string name = ReadName(inner);
                var attrs = ParseAttributes(inner.Length > name.Length ? inner[name.Length..] : string.Empty);

                if (ForbiddenSkipInner.Contains(name))
                {
                    result.LostElements.Add($"Небезопасный или неподдерживаемый элемент <{name}> отброшен.");
                    if (!isClose && !selfClose)
                    {
                        i = SkipUntilClose(html, close + 1, name);
                        continue;
                    }

                    i = close + 1;
                    continue;
                }

                if (!isClose && !selfClose && !IsVoid(name))
                {
                    depth++;
                    if (depth > ImportLimits.MaxHtmlNestingDepth)
                    {
                        result.BlockingReasons.Add("Превышена допустимая вложенность HTML.");
                        return tokens;
                    }
                }
                else if (isClose)
                {
                    depth = Math.Max(0, depth - 1);
                }

                tokens.Add(new HtmlToken
                {
                    Kind = isClose ? HtmlTokenKind.Close : (selfClose || IsVoid(name) ? HtmlTokenKind.Empty : HtmlTokenKind.Open),
                    Name = name,
                    Attributes = attrs
                });
                i = close + 1;
            }
            else
            {
                int next = html.IndexOf('<', i);
                if (next < 0)
                {
                    next = html.Length;
                }

                string text = DecodeEntities(html.Substring(i, next - i), result);
                if (result.HasBlocking)
                {
                    return tokens;
                }

                if (text.Length > 0)
                {
                    tokens.Add(new HtmlToken { Kind = HtmlTokenKind.Text, Text = text });
                }

                i = next;
            }
        }

        return tokens;
    }

    private static bool IsVoid(string name)
        => name is "br" or "img" or "hr" or "input" or "meta" or "link" or "col" or "area" or "source" or "wbr";

    private static bool StartsWith(string html, int i, string value)
        => string.CompareOrdinal(html, i, value, 0, value.Length) == 0;

    private static bool StartsWithIgnoreCase(string html, int i, string value)
        => string.Compare(html, i, value, 0, value.Length, StringComparison.OrdinalIgnoreCase) == 0;

    private static int FindTagEnd(string html, int start)
    {
        bool quote = false;
        char q = '\0';
        for (int i = start + 1; i < html.Length; i++)
        {
            char c = html[i];
            if (quote)
            {
                if (c == q)
                {
                    quote = false;
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = true;
                q = c;
                continue;
            }

            if (c == '>')
            {
                return i;
            }
        }

        return -1;
    }

    private static int SkipUntilClose(string html, int start, string name)
    {
        string close = "</" + name;
        int found = html.IndexOf(close, start, StringComparison.OrdinalIgnoreCase);
        if (found < 0)
        {
            return html.Length;
        }

        int gt = html.IndexOf('>', found);
        return gt < 0 ? html.Length : gt + 1;
    }

    private static string ReadName(string inner)
    {
        int i = 0;
        while (i < inner.Length && !char.IsWhiteSpace(inner[i]) && inner[i] != '/' && inner[i] != '>')
        {
            i++;
        }

        return inner[..i].Trim().ToLowerInvariant();
    }

    private static Dictionary<string, string> ParseAttributes(string rest)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        while (i < rest.Length)
        {
            while (i < rest.Length && char.IsWhiteSpace(rest[i]))
            {
                i++;
            }

            if (i >= rest.Length)
            {
                break;
            }

            int nameStart = i;
            while (i < rest.Length && rest[i] != '=' && !char.IsWhiteSpace(rest[i]) && rest[i] != '/')
            {
                i++;
            }

            string name = rest[nameStart..i].Trim();
            if (name.Length == 0)
            {
                i++;
                continue;
            }

            while (i < rest.Length && char.IsWhiteSpace(rest[i]))
            {
                i++;
            }

            string value = "true";
            if (i < rest.Length && rest[i] == '=')
            {
                i++;
                while (i < rest.Length && char.IsWhiteSpace(rest[i]))
                {
                    i++;
                }

                if (i < rest.Length && rest[i] is '"' or '\'')
                {
                    char q = rest[i++];
                    int vs = i;
                    while (i < rest.Length && rest[i] != q)
                    {
                        i++;
                    }

                    value = rest[vs..Math.Min(i, rest.Length)];
                    if (i < rest.Length)
                    {
                        i++;
                    }
                }
                else
                {
                    int vs = i;
                    while (i < rest.Length && !char.IsWhiteSpace(rest[i]) && rest[i] != '/')
                    {
                        i++;
                    }

                    value = rest[vs..i];
                }
            }

            map.TryAdd(name, WebUtility.HtmlDecode(value));
        }

        return map;
    }

    private static string DecodeEntities(string text, HtmlConversionResult result)
    {
        if (text.IndexOf('&') < 0)
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        int i = 0;
        int expansions = 0;
        while (i < text.Length)
        {
            int amp = text.IndexOf('&', i);
            if (amp < 0)
            {
                sb.Append(text, i, text.Length - i);
                break;
            }

            sb.Append(text, i, amp - i);
            int semi = text.IndexOf(';', amp + 1);
            if (semi < 0 || semi - amp > 16)
            {
                sb.Append('&');
                i = amp + 1;
                continue;
            }

            string entity = text.Substring(amp, semi - amp + 1);
            string decoded = WebUtility.HtmlDecode(entity);
            if (!string.Equals(decoded, entity, StringComparison.Ordinal)
                && decoded.IndexOf('&') >= 0
                && decoded.Contains(';'))
            {
                result.BlockingReasons.Add("Отклонена потенциальная атака расширения сущностей.");
                return string.Empty;
            }

            expansions += Math.Max(1, decoded.Length);
            if (expansions > ImportLimits.MaxEntityExpansionChars)
            {
                result.BlockingReasons.Add("Превышен лимит расширения HTML-сущностей.");
                return string.Empty;
            }

            sb.Append(decoded);
            i = semi + 1;
        }

        return sb.ToString();
    }

    private enum HtmlTokenKind
    {
        Text,
        Open,
        Close,
        Empty
    }

    private sealed class HtmlToken
    {
        public HtmlTokenKind Kind { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Text { get; init; } = string.Empty;
        public Dictionary<string, string> Attributes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ListState
    {
        public bool Ordered { get; init; }
        public int Index { get; set; }
    }

    private sealed class RenderContext
    {
        public RenderContext(HtmlConversionResult result) => Result = result;
        public HtmlConversionResult Result { get; }
        public StringBuilder Builder { get; } = new();
        public Stack<ListState> Lists { get; } = new();
        public bool InPre { get; set; }

        public void AppendText(string text)
        {
            Builder.Append(InPre ? text : CollapseInline(text));
        }

        public void EnsureNewline()
        {
            if (Builder.Length == 0 || Builder[^1] == '\n')
            {
                return;
            }

            Builder.AppendLine();
        }

        public void EnsureBlankLine()
        {
            EnsureNewline();
            if (Builder.Length == 0)
            {
                return;
            }

            if (Builder.Length == 1 || Builder[^2] != '\n')
            {
                Builder.AppendLine();
            }
        }

        private static string CollapseInline(string text)
        {
            var sb = new StringBuilder(text.Length);
            bool space = false;
            foreach (var ch in text)
            {
                if (ch == '\r')
                {
                    continue;
                }

                if (char.IsWhiteSpace(ch) && ch != '\n')
                {
                    if (!space)
                    {
                        sb.Append(' ');
                        space = true;
                    }
                }
                else if (ch == '\n')
                {
                    if (!space)
                    {
                        sb.Append(' ');
                        space = true;
                    }
                }
                else
                {
                    space = false;
                    sb.Append(ch);
                }
            }

            return sb.ToString();
        }
    }
}
