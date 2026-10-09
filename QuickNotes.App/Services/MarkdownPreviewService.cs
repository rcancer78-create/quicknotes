using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using FontFamily = System.Windows.Media.FontFamily;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using CheckBox = System.Windows.Controls.CheckBox;
using Cursors = System.Windows.Input.Cursors;

namespace QuickNotes.App.Services;

#region Markdown AST Models

public abstract class MarkdownBlock
{
}

public class MarkdownParagraphBlock : MarkdownBlock
{
    public List<MarkdownInline> Inlines { get; set; } = new();
}

public class MarkdownHeadingBlock : MarkdownBlock
{
    public int Level { get; set; }
    public string AnchorId { get; set; } = string.Empty;
    public string PlainText { get; set; } = string.Empty;
    public int LineIndex { get; set; }
    public int CharacterIndex { get; set; }
    public List<MarkdownInline> Inlines { get; set; } = new();
}

public class MarkdownFencedCodeBlock : MarkdownBlock
{
    public string Language { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
}

public class MarkdownListItem
{
    public List<MarkdownInline> Inlines { get; set; } = new();
}

public class MarkdownUnorderedListBlock : MarkdownBlock
{
    public List<MarkdownListItem> Items { get; set; } = new();
}

public class MarkdownOrderedListBlock : MarkdownBlock
{
    public int StartNumber { get; set; } = 1;
    public List<MarkdownListItem> Items { get; set; } = new();
}

public class MarkdownTaskItem
{
    public bool IsChecked { get; set; }
    public List<MarkdownInline> Inlines { get; set; } = new();
}

public class MarkdownTaskListBlock : MarkdownBlock
{
    public List<MarkdownTaskItem> Items { get; set; } = new();
}

public enum MarkdownColumnAlignment
{
    Left,
    Center,
    Right
}

public class MarkdownTableBlock : MarkdownBlock
{
    public List<MarkdownColumnAlignment> Alignments { get; set; } = new();
    public List<List<MarkdownInline>> HeaderCells { get; set; } = new();
    public List<List<List<MarkdownInline>>> Rows { get; set; } = new();
}

public abstract class MarkdownInline
{
}

public class MarkdownTextInline : MarkdownInline
{
    public string Text { get; set; } = string.Empty;
}

public class MarkdownBoldInline : MarkdownInline
{
    public List<MarkdownInline> Children { get; set; } = new();
}

public class MarkdownItalicInline : MarkdownInline
{
    public List<MarkdownInline> Children { get; set; } = new();
}

public class MarkdownBoldItalicInline : MarkdownInline
{
    public List<MarkdownInline> Children { get; set; } = new();
}

public class MarkdownStrikethroughInline : MarkdownInline
{
    public List<MarkdownInline> Children { get; set; } = new();
}

public class MarkdownCodeInline : MarkdownInline
{
    public string Code { get; set; } = string.Empty;
}

public class MarkdownLinkInline : MarkdownInline
{
    public string Text { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public bool IsSafe { get; set; }
}

public class MarkdownImageInline : MarkdownInline
{
    public string AltText { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}

#endregion

public class MarkdownPreviewService
{
    private static readonly FontFamily CodeFont = new("Consolas, Cascadia Code, Courier New");
    private static readonly SolidColorBrush CodeBackground = new(Color.FromRgb(241, 245, 249));
    private static readonly SolidColorBrush CodeForeground = new(Color.FromRgb(15, 23, 42));
    private static readonly SolidColorBrush CodeBorder = new(Color.FromRgb(226, 232, 240));
    private static readonly SolidColorBrush LinkBrush = new(Color.FromRgb(37, 99, 235));
    private static readonly SolidColorBrush MainForeground = new(Color.FromRgb(30, 41, 59));
    private static readonly SolidColorBrush HighlightBrush = new(Color.FromRgb(254, 240, 138));
    private static readonly SolidColorBrush HighlightFgBrush = new(Color.FromRgb(23, 32, 51));

    static MarkdownPreviewService()
    {
        CodeBackground.Freeze();
        CodeForeground.Freeze();
        CodeBorder.Freeze();
        LinkBrush.Freeze();
        MainForeground.Freeze();
        HighlightBrush.Freeze();
        HighlightFgBrush.Freeze();
    }

    public FlowDocument BuildFlowDocument(string? markdownText, string? highlightQuery = null) =>
        BuildFlowDocument(markdownText, highlightQuery, null, null, null);

    public FlowDocument BuildFlowDocument(
        string? markdownText,
        string? highlightQuery,
        IReadOnlySet<string>? collapsedAnchors,
        Action<string>? onToggleCollapse,
        IAttachmentStorageService? attachmentStorage = null)
    {
        if (string.IsNullOrEmpty(markdownText))
        {
            return CreateEmptyDocument();
        }

        try
        {
            var blocks = ParseBlocks(markdownText);
            return RenderToFlowDocument(blocks, highlightQuery, collapsedAnchors, onToggleCollapse, attachmentStorage);
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("MarkdownPreviewService", ex);
            // Fallback for invalid Markdown: show raw plain text without throwing
            return CreatePlainTextDocument(markdownText, highlightQuery);
        }
    }

    public List<MarkdownOutlineItem> ExtractOutline(string? markdownText)
    {
        var outline = new List<MarkdownOutlineItem>();
        if (string.IsNullOrEmpty(markdownText))
        {
            return outline;
        }

        string normalized = markdownText.Replace("\r\n", "\n").Replace('\r', '\n');
        string[] lines = normalized.Split('\n');
        var existingAnchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool inCodeBlock = false;
        int charIndex = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                inCodeBlock = !inCodeBlock;
                charIndex += line.Length + 1;
                continue;
            }

            if (!inCodeBlock && !string.IsNullOrWhiteSpace(line))
            {
                int hashCount = 0;
                while (hashCount < trimmed.Length && trimmed[hashCount] == '#' && hashCount < 6)
                {
                    hashCount++;
                }

                if (hashCount > 0 && hashCount < trimmed.Length && (trimmed[hashCount] == ' ' || trimmed[hashCount] == '\t'))
                {
                    string title = trimmed.Substring(hashCount).Trim();
                    string anchor = HeadingAnchorHelper.GetUniqueAnchor(title, existingAnchors);

                    outline.Add(new MarkdownOutlineItem
                    {
                        Title = title,
                        Level = hashCount,
                        AnchorId = anchor,
                        LineIndex = i,
                        CharacterIndex = charIndex + (line.Length - trimmed.Length)
                    });
                }
            }

            charIndex += line.Length + 1;
        }

        return outline;
    }

    public List<MarkdownBlock> ParseBlocks(string? markdownText)
    {
        var result = new List<MarkdownBlock>();
        if (string.IsNullOrEmpty(markdownText))
        {
            return result;
        }

        string normalized = markdownText.Replace("\r\n", "\n").Replace('\r', '\n');
        string[] lines = normalized.Split('\n');
        var existingAnchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool inCodeBlock = false;
        string codeLanguage = string.Empty;
        var codeLines = new List<string>();
        int charIndex = 0;

        MarkdownParagraphBlock? currentParagraph = null;
        MarkdownUnorderedListBlock? currentUnorderedList = null;
        MarkdownOrderedListBlock? currentOrderedList = null;
        MarkdownTaskListBlock? currentTaskList = null;

        void CloseCurrentBlock()
        {
            currentParagraph = null;
            currentUnorderedList = null;
            currentOrderedList = null;
            currentTaskList = null;
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            int currentLineChar = charIndex;
            charIndex += line.Length + 1;
            string trimmed = line.TrimStart();

            if (inCodeBlock)
            {
                if (trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    result.Add(new MarkdownFencedCodeBlock
                    {
                        Language = codeLanguage,
                        Code = string.Join("\n", codeLines)
                    });
                    codeLines.Clear();
                    codeLanguage = string.Empty;
                    inCodeBlock = false;
                }
                else
                {
                    codeLines.Add(line);
                }
                continue;
            }

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                CloseCurrentBlock();
                inCodeBlock = true;
                codeLanguage = trimmed.Length > 3 ? trimmed.Substring(3).Trim() : string.Empty;
                codeLines.Clear();
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                CloseCurrentBlock();
                continue;
            }

            // Heading: #..######
            int hashCount = 0;
            while (hashCount < trimmed.Length && trimmed[hashCount] == '#' && hashCount < 6)
            {
                hashCount++;
            }
            if (hashCount > 0 && hashCount < trimmed.Length && (trimmed[hashCount] == ' ' || trimmed[hashCount] == '\t'))
            {
                CloseCurrentBlock();
                string headingText = trimmed.Substring(hashCount).Trim();
                string anchor = HeadingAnchorHelper.GetUniqueAnchor(headingText, existingAnchors);
                result.Add(new MarkdownHeadingBlock
                {
                    Level = hashCount,
                    AnchorId = anchor,
                    PlainText = headingText,
                    LineIndex = i,
                    CharacterIndex = currentLineChar + (line.Length - trimmed.Length),
                    Inlines = ParseInlines(headingText)
                });
                continue;
            }

            // Pipe Table: line containing '|' and next line is table separator
            if (trimmed.Contains('|') && i + 1 < lines.Length && IsTableSeparatorRow(lines[i + 1], out var alignments))
            {
                CloseCurrentBlock();
                var headerCells = SplitTableRow(trimmed).Select(c => ParseInlines(c)).ToList();
                var tableBlock = new MarkdownTableBlock
                {
                    Alignments = alignments,
                    HeaderCells = headerCells
                };

                charIndex += lines[i + 1].Length + 1;
                i += 2;

                while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]) && lines[i].TrimStart().Contains('|'))
                {
                    var rowCells = SplitTableRow(lines[i].TrimStart()).Select(c => ParseInlines(c)).ToList();
                    tableBlock.Rows.Add(rowCells);
                    charIndex += lines[i].Length + 1;
                    i++;
                }
                i--;
                result.Add(tableBlock);
                continue;
            }

            // Task list checkbox: - [ ] or - [x] or * [ ] or + [ ]
            if (trimmed.Length >= 6 &&
                (trimmed[0] == '-' || trimmed[0] == '*' || trimmed[0] == '+') &&
                trimmed[1] == ' ' &&
                trimmed[2] == '[' &&
                (trimmed[3] == ' ' || trimmed[3] == 'x' || trimmed[3] == 'X') &&
                trimmed[4] == ']' &&
                trimmed[5] == ' ')
            {
                currentParagraph = null;
                currentUnorderedList = null;
                currentOrderedList = null;

                bool isChecked = trimmed[3] == 'x' || trimmed[3] == 'X';
                string itemText = trimmed.Substring(6).Trim();

                if (currentTaskList == null)
                {
                    currentTaskList = new MarkdownTaskListBlock();
                    result.Add(currentTaskList);
                }

                currentTaskList.Items.Add(new MarkdownTaskItem
                {
                    IsChecked = isChecked,
                    Inlines = ParseInlines(itemText)
                });
                continue;
            }

            // Unordered list: - or * or +
            if (trimmed.Length >= 2 &&
                (trimmed[0] == '-' || trimmed[0] == '*' || trimmed[0] == '+') &&
                (trimmed[1] == ' ' || trimmed[1] == '\t'))
            {
                currentParagraph = null;
                currentTaskList = null;
                currentOrderedList = null;

                string itemText = trimmed.Substring(2).Trim();
                if (currentUnorderedList == null)
                {
                    currentUnorderedList = new MarkdownUnorderedListBlock();
                    result.Add(currentUnorderedList);
                }

                currentUnorderedList.Items.Add(new MarkdownListItem
                {
                    Inlines = ParseInlines(itemText)
                });
                continue;
            }

            // Ordered list: 1. or 1)
            int digitEnd = 0;
            while (digitEnd < trimmed.Length && char.IsAsciiDigit(trimmed[digitEnd]))
            {
                digitEnd++;
            }
            if (digitEnd > 0 && digitEnd + 1 < trimmed.Length &&
                (trimmed[digitEnd] == '.' || trimmed[digitEnd] == ')') &&
                (trimmed[digitEnd + 1] == ' ' || trimmed[digitEnd + 1] == '\t'))
            {
                currentParagraph = null;
                currentTaskList = null;
                currentUnorderedList = null;

                int number = int.TryParse(trimmed.Substring(0, digitEnd), out int n) ? n : 1;
                string itemText = trimmed.Substring(digitEnd + 2).Trim();

                if (currentOrderedList == null)
                {
                    currentOrderedList = new MarkdownOrderedListBlock { StartNumber = number };
                    result.Add(currentOrderedList);
                }

                currentOrderedList.Items.Add(new MarkdownListItem
                {
                    Inlines = ParseInlines(itemText)
                });
                continue;
            }

            // Regular paragraph line
            currentUnorderedList = null;
            currentOrderedList = null;
            currentTaskList = null;

            if (currentParagraph == null)
            {
                currentParagraph = new MarkdownParagraphBlock();
                currentParagraph.Inlines.AddRange(ParseInlines(trimmed));
                result.Add(currentParagraph);
            }
            else
            {
                // Multi-line paragraph continuation
                currentParagraph.Inlines.Add(new MarkdownTextInline { Text = "\n" });
                currentParagraph.Inlines.AddRange(ParseInlines(trimmed));
            }
        }

        if (inCodeBlock)
        {
            result.Add(new MarkdownFencedCodeBlock
            {
                Language = codeLanguage,
                Code = string.Join("\n", codeLines)
            });
        }

        return result;
    }

    private static List<string> SplitTableRow(string line)
    {
        string trimmed = line.Trim();
        if (trimmed.StartsWith('|'))
        {
            trimmed = trimmed.Substring(1);
        }
        if (trimmed.EndsWith('|'))
        {
            trimmed = trimmed.Substring(0, trimmed.Length - 1);
        }
        var parts = trimmed.Split('|');
        var cells = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            cells.Add(part.Trim());
        }
        return cells;
    }

    private static bool IsTableSeparatorRow(string line, out List<MarkdownColumnAlignment> alignments)
    {
        alignments = new List<MarkdownColumnAlignment>();
        string trimmed = line.Trim();
        if (!trimmed.Contains('-'))
        {
            return false;
        }
        var cells = SplitTableRow(trimmed);
        if (cells.Count == 0)
        {
            return false;
        }
        foreach (var cell in cells)
        {
            string c = cell.Trim();
            if (c.Length < 3)
            {
                return false;
            }
            bool leftColon = c.StartsWith(':');
            bool rightColon = c.EndsWith(':');
            string dashes = c.Trim(':');
            if (dashes.Length < 1 || dashes.Any(ch => ch != '-'))
            {
                return false;
            }
            if (leftColon && rightColon)
            {
                alignments.Add(MarkdownColumnAlignment.Center);
            }
            else if (rightColon)
            {
                alignments.Add(MarkdownColumnAlignment.Right);
            }
            else
            {
                alignments.Add(MarkdownColumnAlignment.Left);
            }
        }
        return true;
    }

    public List<MarkdownInline> ParseInlines(string text)
    {
        var inlines = new List<MarkdownInline>();
        if (string.IsNullOrEmpty(text))
        {
            return inlines;
        }

        var buffer = new StringBuilder();

        void FlushBuffer()
        {
            if (buffer.Length > 0)
            {
                inlines.Add(new MarkdownTextInline { Text = buffer.ToString() });
                buffer.Clear();
            }
        }

        int i = 0;
        while (i < text.Length)
        {
            // Inline code: `code`
            if (text[i] == '`')
            {
                int close = text.IndexOf('`', i + 1);
                if (close > i)
                {
                    FlushBuffer();
                    string code = text.Substring(i + 1, close - i - 1);
                    inlines.Add(new MarkdownCodeInline { Code = code });
                    i = close + 1;
                    continue;
                }
            }

            // Image: ![alt](url)
            if (text[i] == '!' && i + 1 < text.Length && text[i + 1] == '[')
            {
                int mid = text.IndexOf("](", i + 2, StringComparison.Ordinal);
                if (mid > i + 1)
                {
                    int openCount = 1;
                    int closeParen = -1;
                    for (int p = mid + 2; p < text.Length; p++)
                    {
                        if (text[p] == '(')
                        {
                            openCount++;
                        }
                        else if (text[p] == ')')
                        {
                            openCount--;
                            if (openCount == 0)
                            {
                                closeParen = p;
                                break;
                            }
                        }
                    }

                    if (closeParen > mid + 2)
                    {
                        FlushBuffer();
                        string altText = text.Substring(i + 2, mid - i - 2);
                        string imageUrl = text.Substring(mid + 2, closeParen - mid - 2).Trim();

                        inlines.Add(new MarkdownImageInline
                        {
                            AltText = altText,
                            Url = imageUrl
                        });
                        i = closeParen + 1;
                        continue;
                    }
                }
            }

            // Link: [text](url)
            if (text[i] == '[')
            {
                int mid = text.IndexOf("](", i + 1, StringComparison.Ordinal);
                if (mid > i)
                {
                    int openCount = 1;
                    int closeParen = -1;
                    for (int p = mid + 2; p < text.Length; p++)
                    {
                        if (text[p] == '(')
                        {
                            openCount++;
                        }
                        else if (text[p] == ')')
                        {
                            openCount--;
                            if (openCount == 0)
                            {
                                closeParen = p;
                                break;
                            }
                        }
                    }

                    if (closeParen > mid + 2)
                    {
                        FlushBuffer();
                        string linkText = text.Substring(i + 1, mid - i - 1);
                        string linkUrl = text.Substring(mid + 2, closeParen - mid - 2).Trim();

                        bool isSafe = Uri.TryCreate(linkUrl, UriKind.Absolute, out var uri) &&
                                      (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                                       uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));

                        inlines.Add(new MarkdownLinkInline
                        {
                            Text = linkText,
                            Url = linkUrl,
                            IsSafe = isSafe
                        });
                        i = closeParen + 1;
                        continue;
                    }
                }
            }

            // Bold-Italic: ***text***
            if (i + 2 < text.Length && text[i] == '*' && text[i + 1] == '*' && text[i + 2] == '*')
            {
                int close = text.IndexOf("***", i + 3, StringComparison.Ordinal);
                if (close > i + 2)
                {
                    FlushBuffer();
                    string inner = text.Substring(i + 3, close - i - 3);
                    inlines.Add(new MarkdownBoldItalicInline { Children = ParseInlines(inner) });
                    i = close + 3;
                    continue;
                }
            }

            // Bold: **text**
            if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
            {
                int close = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (close > i + 1)
                {
                    FlushBuffer();
                    string inner = text.Substring(i + 2, close - i - 2);
                    inlines.Add(new MarkdownBoldInline { Children = ParseInlines(inner) });
                    i = close + 2;
                    continue;
                }
            }

            // Bold: __text__
            if (i + 1 < text.Length && text[i] == '_' && text[i + 1] == '_')
            {
                int close = text.IndexOf("__", i + 2, StringComparison.Ordinal);
                if (close > i + 1)
                {
                    FlushBuffer();
                    string inner = text.Substring(i + 2, close - i - 2);
                    inlines.Add(new MarkdownBoldInline { Children = ParseInlines(inner) });
                    i = close + 2;
                    continue;
                }
            }

            // Strikethrough: ~~text~~
            if (i + 1 < text.Length && text[i] == '~' && text[i + 1] == '~')
            {
                int close = text.IndexOf("~~", i + 2, StringComparison.Ordinal);
                if (close > i + 1)
                {
                    FlushBuffer();
                    string inner = text.Substring(i + 2, close - i - 2);
                    inlines.Add(new MarkdownStrikethroughInline { Children = ParseInlines(inner) });
                    i = close + 2;
                    continue;
                }
            }

            // Italic: *text*
            if (text[i] == '*')
            {
                int close = -1;
                for (int j = i + 1; j < text.Length; j++)
                {
                    if (text[j] == '*')
                    {
                        if (j + 1 < text.Length && text[j + 1] == '*')
                        {
                            j++;
                            continue;
                        }
                        close = j;
                        break;
                    }
                }

                if (close > i)
                {
                    FlushBuffer();
                    string inner = text.Substring(i + 1, close - i - 1);
                    inlines.Add(new MarkdownItalicInline { Children = ParseInlines(inner) });
                    i = close + 1;
                    continue;
                }
            }

            // Italic: _text_
            if (text[i] == '_')
            {
                int close = -1;
                for (int j = i + 1; j < text.Length; j++)
                {
                    if (text[j] == '_')
                    {
                        if (j + 1 < text.Length && text[j + 1] == '_')
                        {
                            j++;
                            continue;
                        }
                        close = j;
                        break;
                    }
                }

                if (close > i)
                {
                    FlushBuffer();
                    string inner = text.Substring(i + 1, close - i - 1);
                    inlines.Add(new MarkdownItalicInline { Children = ParseInlines(inner) });
                    i = close + 1;
                    continue;
                }
            }

            // Normal text character (including HTML chars <, >, etc.)
            buffer.Append(text[i]);
            i++;
        }

        FlushBuffer();
        return inlines;
    }

    public FlowDocument RenderToFlowDocument(List<MarkdownBlock> blocks, string? highlightQuery = null) =>
        RenderToFlowDocument(blocks, highlightQuery, null, null, null);

    public FlowDocument RenderToFlowDocument(
        List<MarkdownBlock> blocks,
        string? highlightQuery,
        IReadOnlySet<string>? collapsedAnchors,
        Action<string>? onToggleCollapse,
        IAttachmentStorageService? attachmentStorage)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(14, 10, 14, 10),
            FontFamily = new FontFamily("Segoe UI, Segoe UI Variable, Arial"),
            FontSize = 14.5,
            Foreground = GetThemeBrush("InkBrush", MainForeground),
            LineHeight = 22,
            TextAlignment = TextAlignment.Left,
            IsOptimalParagraphEnabled = false
        };

        if (blocks.Count == 0)
        {
            doc.Blocks.Add(new Paragraph());
            return doc;
        }

        int? collapsedLevel = null;

        foreach (var block in blocks)
        {
            if (block is MarkdownHeadingBlock heading)
            {
                if (collapsedLevel != null && heading.Level <= collapsedLevel.Value)
                {
                    collapsedLevel = null;
                }

                if (collapsedLevel != null)
                {
                    continue;
                }

                bool isCollapsed = collapsedAnchors != null && !string.IsNullOrEmpty(heading.AnchorId) && collapsedAnchors.Contains(heading.AnchorId);
                doc.Blocks.Add(RenderHeading(heading, highlightQuery, isCollapsed, onToggleCollapse, attachmentStorage));

                if (isCollapsed)
                {
                    collapsedLevel = heading.Level;
                }
                continue;
            }

            if (collapsedLevel != null)
            {
                continue;
            }

            switch (block)
            {
                case MarkdownTableBlock table:
                    doc.Blocks.Add(RenderTable(table, highlightQuery, attachmentStorage));
                    break;
                case MarkdownFencedCodeBlock codeBlock:
                    doc.Blocks.Add(RenderFencedCode(codeBlock));
                    break;
                case MarkdownTaskListBlock taskList:
                    foreach (var item in taskList.Items)
                    {
                        doc.Blocks.Add(RenderTaskItem(item, highlightQuery, attachmentStorage));
                    }
                    break;
                case MarkdownUnorderedListBlock unList:
                    doc.Blocks.Add(RenderUnorderedList(unList, highlightQuery, attachmentStorage));
                    break;
                case MarkdownOrderedListBlock ordList:
                    doc.Blocks.Add(RenderOrderedList(ordList, highlightQuery, attachmentStorage));
                    break;
                case MarkdownParagraphBlock paragraph:
                    doc.Blocks.Add(RenderParagraph(paragraph, highlightQuery, attachmentStorage));
                    break;
            }
        }

        return doc;
    }

    private Paragraph RenderHeading(
        MarkdownHeadingBlock heading,
        string? highlightQuery = null,
        bool isCollapsed = false,
        Action<string>? onToggleCollapse = null,
        IAttachmentStorageService? attachmentStorage = null)
    {
        var p = new Paragraph
        {
            Tag = heading.AnchorId
        };

        switch (heading.Level)
        {
            case 1:
                p.FontSize = 22;
                p.FontWeight = FontWeights.Bold;
                p.Margin = new Thickness(0, 14, 0, 6);
                break;
            case 2:
                p.FontSize = 18;
                p.FontWeight = FontWeights.Bold;
                p.Margin = new Thickness(0, 12, 0, 5);
                break;
            case 3:
                p.FontSize = 16;
                p.FontWeight = FontWeights.SemiBold;
                p.Margin = new Thickness(0, 10, 0, 4);
                break;
            case 4:
                p.FontSize = 15;
                p.FontWeight = FontWeights.SemiBold;
                p.Margin = new Thickness(0, 8, 0, 3);
                break;
            case 5:
                p.FontSize = 14;
                p.FontWeight = FontWeights.SemiBold;
                p.Margin = new Thickness(0, 6, 0, 2);
                break;
            default:
                p.FontSize = 13;
                p.FontWeight = FontWeights.SemiBold;
                p.Margin = new Thickness(0, 4, 0, 2);
                break;
        }

        if (onToggleCollapse != null && !string.IsNullOrEmpty(heading.AnchorId))
        {
            var glyphRun = new Run(isCollapsed ? "▶ " : "▼ ")
            {
                FontFamily = new FontFamily("Segoe UI Symbol, Segoe UI"),
                FontSize = Math.Max(10, p.FontSize - 4),
                Foreground = GetThemeBrush("SubtleInkBrush", Brushes.Gray)
            };
            var glyphLink = new Hyperlink(glyphRun)
            {
                TextDecorations = null,
                Cursor = Cursors.Hand,
                ToolTip = isCollapsed ? "Развернуть раздел" : "Свернуть раздел"
            };
            string anchor = heading.AnchorId;
            glyphLink.Click += (_, _) => onToggleCollapse(anchor);
            p.Inlines.Add(glyphLink);
        }

        foreach (var inline in heading.Inlines)
        {
            p.Inlines.Add(RenderInline(inline, highlightQuery, attachmentStorage));
        }

        return p;
    }

    private Table RenderTable(MarkdownTableBlock tableBlock, string? highlightQuery = null, IAttachmentStorageService? attachmentStorage = null)
    {
        var table = new Table
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 8, 0, 12),
            BorderBrush = GetThemeBrush("LineBrush", CodeBorder),
            BorderThickness = new Thickness(1)
        };

        int colCount = Math.Max(tableBlock.HeaderCells.Count, tableBlock.Rows.Count > 0 ? tableBlock.Rows.Max(r => r.Count) : 0);
        colCount = Math.Max(1, colCount);

        for (int c = 0; c < colCount; c++)
        {
            table.Columns.Add(new TableColumn());
        }

        var rowGroup = new TableRowGroup();

        if (tableBlock.HeaderCells.Count > 0)
        {
            var headerRow = new TableRow
            {
                Background = GetThemeBrush("SurfaceAltBrush", CodeBackground)
            };

            for (int c = 0; c < colCount; c++)
            {
                var cellParagraph = new Paragraph { Margin = new Thickness(0) };
                var alignment = c < tableBlock.Alignments.Count ? tableBlock.Alignments[c] : MarkdownColumnAlignment.Left;
                cellParagraph.TextAlignment = alignment switch
                {
                    MarkdownColumnAlignment.Center => TextAlignment.Center,
                    MarkdownColumnAlignment.Right => TextAlignment.Right,
                    _ => TextAlignment.Left
                };

                if (c < tableBlock.HeaderCells.Count)
                {
                    foreach (var inline in tableBlock.HeaderCells[c])
                    {
                        var rendered = RenderInline(inline, highlightQuery, attachmentStorage);
                        rendered.FontWeight = FontWeights.SemiBold;
                        cellParagraph.Inlines.Add(rendered);
                    }
                }

                var cell = new TableCell(cellParagraph)
                {
                    Padding = new Thickness(8, 6, 8, 6),
                    BorderBrush = GetThemeBrush("LineBrush", CodeBorder),
                    BorderThickness = new Thickness(0, 0, c < colCount - 1 ? 1 : 0, 1)
                };
                headerRow.Cells.Add(cell);
            }
            rowGroup.Rows.Add(headerRow);
        }

        for (int r = 0; r < tableBlock.Rows.Count; r++)
        {
            var row = tableBlock.Rows[r];
            var tableRow = new TableRow();

            for (int c = 0; c < colCount; c++)
            {
                var cellParagraph = new Paragraph { Margin = new Thickness(0) };
                var alignment = c < tableBlock.Alignments.Count ? tableBlock.Alignments[c] : MarkdownColumnAlignment.Left;
                cellParagraph.TextAlignment = alignment switch
                {
                    MarkdownColumnAlignment.Center => TextAlignment.Center,
                    MarkdownColumnAlignment.Right => TextAlignment.Right,
                    _ => TextAlignment.Left
                };

                if (c < row.Count)
                {
                    foreach (var inline in row[c])
                    {
                        cellParagraph.Inlines.Add(RenderInline(inline, highlightQuery, attachmentStorage));
                    }
                }

                var cell = new TableCell(cellParagraph)
                {
                    Padding = new Thickness(8, 6, 8, 6),
                    BorderBrush = GetThemeBrush("LineBrush", CodeBorder),
                    BorderThickness = new Thickness(0, 0, c < colCount - 1 ? 1 : 0, r < tableBlock.Rows.Count - 1 ? 1 : 0)
                };
                tableRow.Cells.Add(cell);
            }
            rowGroup.Rows.Add(tableRow);
        }

        table.RowGroups.Add(rowGroup);
        return table;
    }

    private Paragraph RenderFencedCode(MarkdownFencedCodeBlock codeBlock)
    {
        var p = new Paragraph
        {
            FontFamily = CodeFont,
            FontSize = 13,
            LineHeight = 18,
            Background = GetThemeBrush("SurfaceAltBrush", CodeBackground),
            Foreground = GetThemeBrush("InkBrush", CodeForeground),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 6, 0, 10),
            BorderBrush = GetThemeBrush("LineBrush", CodeBorder),
            BorderThickness = new Thickness(1)
        };

        p.Inlines.Add(new Run(codeBlock.Code));
        return p;
    }

    private Paragraph RenderTaskItem(MarkdownTaskItem item, string? highlightQuery = null, IAttachmentStorageService? attachmentStorage = null)
    {
        var p = new Paragraph
        {
            Margin = new Thickness(6, 2, 0, 4)
        };

        var checkBox = new CheckBox
        {
            IsChecked = item.IsChecked,
            IsHitTestVisible = false,
            Focusable = false,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 1, 8, 0)
        };

        p.Inlines.Add(new InlineUIContainer(checkBox));
        foreach (var inline in item.Inlines)
        {
            p.Inlines.Add(RenderInline(inline, highlightQuery, attachmentStorage));
        }

        return p;
    }

    private System.Windows.Documents.List RenderUnorderedList(MarkdownUnorderedListBlock listBlock, string? highlightQuery = null, IAttachmentStorageService? attachmentStorage = null)
    {
        var list = new System.Windows.Documents.List
        {
            MarkerStyle = TextMarkerStyle.Disc,
            Margin = new Thickness(10, 2, 0, 6),
            Padding = new Thickness(10, 0, 0, 0)
        };

        foreach (var item in listBlock.Items)
        {
            var li = new ListItem();
            var p = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
            foreach (var inline in item.Inlines)
            {
                p.Inlines.Add(RenderInline(inline, highlightQuery, attachmentStorage));
            }
            li.Blocks.Add(p);
            list.ListItems.Add(li);
        }

        return list;
    }

    private System.Windows.Documents.List RenderOrderedList(MarkdownOrderedListBlock listBlock, string? highlightQuery = null, IAttachmentStorageService? attachmentStorage = null)
    {
        var list = new System.Windows.Documents.List
        {
            MarkerStyle = TextMarkerStyle.Decimal,
            StartIndex = listBlock.StartNumber,
            Margin = new Thickness(10, 2, 0, 6),
            Padding = new Thickness(10, 0, 0, 0)
        };

        foreach (var item in listBlock.Items)
        {
            var li = new ListItem();
            var p = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
            foreach (var inline in item.Inlines)
            {
                p.Inlines.Add(RenderInline(inline, highlightQuery, attachmentStorage));
            }
            li.Blocks.Add(p);
            list.ListItems.Add(li);
        }

        return list;
    }

    private Paragraph RenderParagraph(MarkdownParagraphBlock paragraph, string? highlightQuery = null, IAttachmentStorageService? attachmentStorage = null)
    {
        var p = new Paragraph
        {
            Margin = new Thickness(0, 0, 0, 8)
        };

        foreach (var inline in paragraph.Inlines)
        {
            p.Inlines.Add(RenderInline(inline, highlightQuery, attachmentStorage));
        }

        return p;
    }

    private Inline RenderInline(MarkdownInline inline, string? highlightQuery = null, IAttachmentStorageService? attachmentStorage = null)
    {
        switch (inline)
        {
            case MarkdownTextInline text:
                return CreateTextInline(text.Text, highlightQuery);

            case MarkdownBoldInline bold:
                var boldSpan = new Bold();
                foreach (var child in bold.Children)
                {
                    boldSpan.Inlines.Add(RenderInline(child, highlightQuery, attachmentStorage));
                }
                return boldSpan;

            case MarkdownItalicInline italic:
                var italicSpan = new Italic();
                foreach (var child in italic.Children)
                {
                    italicSpan.Inlines.Add(RenderInline(child, highlightQuery, attachmentStorage));
                }
                return italicSpan;

            case MarkdownBoldItalicInline boldItalic:
                var biBold = new Bold();
                var biItalic = new Italic();
                foreach (var child in boldItalic.Children)
                {
                    biItalic.Inlines.Add(RenderInline(child, highlightQuery, attachmentStorage));
                }
                biBold.Inlines.Add(biItalic);
                return biBold;

            case MarkdownStrikethroughInline strike:
                var strikeSpan = new Span { TextDecorations = TextDecorations.Strikethrough };
                foreach (var child in strike.Children)
                {
                    strikeSpan.Inlines.Add(RenderInline(child, highlightQuery, attachmentStorage));
                }
                return strikeSpan;

            case MarkdownCodeInline code:
                return new Run(code.Code)
                {
                    FontFamily = CodeFont,
                    FontSize = 13,
                    Background = GetThemeBrush("SurfaceAltBrush", CodeBackground),
                    Foreground = GetThemeBrush("InkBrush", CodeForeground)
                };

            case MarkdownImageInline image:
                return RenderImageInline(image, attachmentStorage);

            case MarkdownLinkInline link:
                if (link.IsSafe && Uri.TryCreate(link.Url, UriKind.Absolute, out var uri))
                {
                    var hyperlink = new Hyperlink(CreateTextInline(link.Text, highlightQuery))
                    {
                        NavigateUri = uri,
                        ToolTip = uri.AbsoluteUri,
                        Foreground = GetThemeBrush("AccentBrush", LinkBrush),
                        TextDecorations = TextDecorations.Underline
                    };
                    return hyperlink;
                }
                else
                {
                    // Unsafe links or invalid URLs rendered strictly as plain text
                    string linkRaw = !string.IsNullOrEmpty(link.Text) ? $"[{link.Text}]({link.Url})" : link.Url;
                    return CreateTextInline(linkRaw, highlightQuery);
                }

            default:
                return new Run();
        }
    }

    private Inline RenderImageInline(MarkdownImageInline image, IAttachmentStorageService? attachmentStorage)
    {
        string? resolvedPath = null;

        if (!string.IsNullOrWhiteSpace(image.Url))
        {
            if (File.Exists(image.Url))
            {
                resolvedPath = image.Url;
            }
            else if (attachmentStorage != null)
            {
                if (attachmentStorage.FileExists(image.Url))
                {
                    resolvedPath = attachmentStorage.GetFullPath(image.Url);
                }
                else
                {
                    string combined = Path.Combine("Attachments", image.Url);
                    if (attachmentStorage.FileExists(combined))
                    {
                        resolvedPath = attachmentStorage.GetFullPath(combined);
                    }
                }
            }
        }

        if (!string.IsNullOrEmpty(resolvedPath) && File.Exists(resolvedPath))
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(resolvedPath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();

                var imgControl = new System.Windows.Controls.Image
                {
                    Source = bitmap,
                    MaxWidth = 600,
                    MaxHeight = 450,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(0, 4, 0, 4),
                    ToolTip = !string.IsNullOrWhiteSpace(image.AltText) ? image.AltText : resolvedPath
                };

                return new InlineUIContainer(imgControl);
            }
            catch (Exception ex)
            {
                ErrorLogService.Write("MarkdownPreviewService.RenderImage", ex);
            }
        }

        string label = !string.IsNullOrWhiteSpace(image.AltText) ? image.AltText : image.Url;
        return new Run($"🖼 [{label}]")
        {
            Foreground = GetThemeBrush("SubtleInkBrush", Brushes.Gray),
            FontStyle = FontStyles.Italic
        };
    }

    private static Inline CreateTextInline(string text, string? highlightQuery)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new Run(string.Empty);
        }

        if (text.Contains('\n'))
        {
            var span = new Span();
            string[] parts = text.Split('\n');
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                {
                    span.Inlines.Add(new LineBreak());
                }
                if (!string.IsNullOrEmpty(parts[i]))
                {
                    span.Inlines.Add(CreateTextInline(parts[i], highlightQuery));
                }
            }
            return span;
        }

        if (string.IsNullOrWhiteSpace(highlightQuery))
        {
            return new Run(text);
        }

        var highlights = SearchPreview.SplitHighlights(text, highlightQuery);
        if (highlights.Count == 0 || (highlights.Count == 1 && !highlights[0].IsMatch))
        {
            return new Run(text);
        }

        var resultSpan = new Span();
        foreach (var h in highlights)
        {
            var run = new Run(h.Text);
            if (h.IsMatch)
            {
                run.Background = HighlightBrush;
                run.Foreground = HighlightFgBrush;
                run.FontWeight = FontWeights.SemiBold;
            }
            resultSpan.Inlines.Add(run);
        }
        return resultSpan;
    }

    private FlowDocument CreateEmptyDocument()
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(14, 10, 14, 10),
            FontFamily = new FontFamily("Segoe UI, Segoe UI Variable, Arial"),
            FontSize = 14.5,
            Foreground = GetThemeBrush("InkBrush", MainForeground)
        };
        doc.Blocks.Add(new Paragraph());
        return doc;
    }

    private FlowDocument CreatePlainTextDocument(string text, string? highlightQuery = null)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(14, 10, 14, 10),
            FontFamily = new FontFamily("Segoe UI, Segoe UI Variable, Arial"),
            FontSize = 14.5,
            Foreground = GetThemeBrush("InkBrush", MainForeground)
        };
        var p = new Paragraph();
        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        string[] parts = normalized.Split('\n');
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) p.Inlines.Add(new LineBreak());
            if (!string.IsNullOrEmpty(parts[i])) p.Inlines.Add(CreateTextInline(parts[i], highlightQuery));
        }
        doc.Blocks.Add(p);
        return doc;
    }

    private static Brush GetThemeBrush(string key, Brush fallback)
    {
        try
        {
            if (System.Windows.Application.Current?.Resources[key] is Brush b)
            {
                return b;
            }
        }
        catch
        {
            // Unit tests running without Application.Current
        }
        return fallback;
    }
}
