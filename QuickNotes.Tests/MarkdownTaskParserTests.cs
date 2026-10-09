using System;
using System.Linq;
using QuickNotes.App.Services;
using Xunit;

namespace QuickNotes.Tests;

public sealed class MarkdownTaskParserTests
{
    [Fact]
    public void Parse_IndentationAndBullets_IndexesOpenCheckboxes()
    {
        string text = "  - [ ] indented dash\n\t* [ ] tab star\n- [ ] root";
        var items = MarkdownTaskParser.Parse(text);
        Assert.Equal(3, items.Count);
        Assert.Equal(1, items[0].LineNumber);
        Assert.Equal("indented dash", items[0].TaskText);
        Assert.Equal("-", items[0].Bullet);
        Assert.Equal("*", items[1].Bullet);
        Assert.Equal(3, items[2].LineNumber);
    }

    [Fact]
    public void Parse_ClosedCheckboxes_AreIgnored()
    {
        string text = "- [x] done\n- [X] also done\n- [ ] open";
        var items = MarkdownTaskParser.Parse(text);
        Assert.Single(items);
        Assert.Equal("open", items[0].TaskText);
    }

    [Fact]
    public void Parse_FencedCodeBacktickAndTilde_IsNotIndexed()
    {
        string text = """
            - [ ] visible before
            ```
            - [ ] hidden tick
            ```
            ~~~info
            - [ ] hidden tilde
            ~~~
            - [ ] visible after
            """;
        var items = MarkdownTaskParser.Parse(text.Replace("\r\n", "\n"));
        Assert.Equal(2, items.Count);
        Assert.Equal("visible before", items[0].TaskText);
        Assert.Equal("visible after", items[1].TaskText);
    }

    [Fact]
    public void Parse_EscapingAndFalsePositives_AreIgnored()
    {
        string text = """
            \- [ ] escaped
            foo - [ ] inline
            -[ ] no space
            - [] empty bracket
            + [ ] plus not in contract
            `- [ ] inline code line`
            - [ ] real task
            """;
        var items = MarkdownTaskParser.Parse(text.Replace("\r\n", "\n"));
        Assert.Single(items);
        Assert.Equal("real task", items[0].TaskText);
    }

    [Fact]
    public void Parse_CrlfAndLf_UseStableLineNumbers()
    {
        string lf = "intro\n- [ ] lf task\n";
        string crlf = "intro\r\n- [ ] lf task\r\n";
        var a = MarkdownTaskParser.Parse(lf);
        var b = MarkdownTaskParser.Parse(crlf);
        Assert.Equal(a[0].LineNumber, b[0].LineNumber);
        Assert.Equal("lf task", a[0].TaskText);
        Assert.Equal("lf task", b[0].TaskText);
    }

    [Fact]
    public void Parse_UnicodeBodies_AreKept()
    {
        string text = "- [ ] Задача 日本語 ✅";
        var items = MarkdownTaskParser.Parse(text);
        Assert.Single(items);
        Assert.Equal("Задача 日本語 ✅", items[0].TaskText);
    }

    [Fact]
    public void Parse_EmptyAndMalformed_YieldNothing()
    {
        Assert.Empty(MarkdownTaskParser.Parse(null));
        Assert.Empty(MarkdownTaskParser.Parse(""));
        Assert.Empty(MarkdownTaskParser.Parse("- [ ]   \n* [ ]\n"));
    }

    [Fact]
    public void Dates_ValidInvalidLeapAndSimilarTokens()
    {
        Assert.Equal(new DateOnly(2026, 10, 1), MarkdownTaskParser.ExtractFirstValidDueDate("do @2026-10-01 it"));
        Assert.Null(MarkdownTaskParser.ExtractFirstValidDueDate("do 2026-10-01 it"));
        Assert.Null(MarkdownTaskParser.ExtractFirstValidDueDate("do @2026/10/01 it"));
        Assert.Null(MarkdownTaskParser.ExtractFirstValidDueDate("do @2026-10-1 it"));
        Assert.Null(MarkdownTaskParser.ExtractFirstValidDueDate("do @2026-02-30 it"));
        Assert.Null(MarkdownTaskParser.ExtractFirstValidDueDate("do @2026-13-01 it"));
        Assert.Null(MarkdownTaskParser.ExtractFirstValidDueDate("user@2026-10-01"));
        Assert.Equal(new DateOnly(2024, 2, 29), MarkdownTaskParser.ExtractFirstValidDueDate("@2024-02-29"));
        Assert.Null(MarkdownTaskParser.ExtractFirstValidDueDate("@2023-02-29"));
        Assert.Equal(new DateOnly(2000, 2, 29), MarkdownTaskParser.ExtractFirstValidDueDate("@2000-02-29"));
        Assert.Null(MarkdownTaskParser.ExtractFirstValidDueDate("@1900-02-29"));
    }

    [Fact]
    public void Dates_MultipleValid_FirstWins()
    {
        var items = MarkdownTaskParser.Parse("- [ ] first @2026-10-01 then @2026-11-02");
        Assert.Single(items);
        Assert.Equal(new DateOnly(2026, 10, 1), items[0].DueDate);
    }

    [Fact]
    public void Parse_StampsNonPlaintextSnapshotProofSharedByLocators()
    {
        string text = "- [ ] secret twin\n- [ ] secret twin\n";
        var items = MarkdownTaskParser.Parse(text);
        Assert.Equal(2, items.Count);
        Assert.Equal(32, items[0].NoteSnapshotProof.Length);
        Assert.Same(items[0].NoteSnapshotProof, items[1].NoteSnapshotProof);
        Assert.Equal(MarkdownTaskParser.ComputeNoteSnapshotProof(text), items[0].NoteSnapshotProof);
        string hex = Convert.ToHexString(items[0].NoteSnapshotProof);
        Assert.Equal(64, hex.Length);
        Assert.DoesNotContain("secret twin", hex, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("- [ ]", hex, StringComparison.Ordinal);
        Assert.NotEqual(MarkdownTaskParser.ComputeNoteSnapshotProof(text + "x"), items[0].NoteSnapshotProof);
    }
}
