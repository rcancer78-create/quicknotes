using System;
using QuickNotes.App.Helpers;
using Xunit;

namespace QuickNotes.Tests;

public class MarkdownEditorOperationsTests
{
    [Fact]
    public void ToggleBold_EmptySelection_InsertsMarkersAndPositionsCaret()
    {
        var (text, selStart, selLen) = MarkdownEditorOperations.ToggleBold("Hello world", 5, 0);
        Assert.Equal("Hello**** world", text);
        Assert.Equal(7, selStart); // Between the two pairs: Hello**|** world
        Assert.Equal(0, selLen);
    }

    [Fact]
    public void ToggleBold_SelectedWord_WrapsWithMarkersAndKeepsInnerSelected()
    {
        var (text, selStart, selLen) = MarkdownEditorOperations.ToggleBold("Hello world", 6, 5);
        Assert.Equal("Hello **world**", text);
        Assert.Equal(8, selStart); // covers "world" inside **
        Assert.Equal(5, selLen);

        // Toggle again unwraps
        var (unwrapped, uStart, uLen) = MarkdownEditorOperations.ToggleBold(text, selStart, selLen);
        Assert.Equal("Hello world", unwrapped);
        Assert.Equal(6, uStart);
        Assert.Equal(5, uLen);
    }

    [Fact]
    public void ToggleBold_OuterSelection_RemovesMarkers()
    {
        var (text, selStart, selLen) = MarkdownEditorOperations.ToggleBold("Hello **world**", 6, 9);
        Assert.Equal("Hello world", text);
        Assert.Equal(6, selStart);
        Assert.Equal(5, selLen);
    }

    [Fact]
    public void ToggleItalic_EmptySelection_InsertsMarkersAndPositionsCaret()
    {
        var (text, selStart, selLen) = MarkdownEditorOperations.ToggleItalic("Hello", 5, 0);
        Assert.Equal("Hello**", text);
        Assert.Equal(6, selStart); // between: Hello*|*
        Assert.Equal(0, selLen);
    }

    [Fact]
    public void ToggleItalic_SelectedWord_WrapsAndUnwraps()
    {
        var (wrapped, wStart, wLen) = MarkdownEditorOperations.ToggleItalic("test word", 0, 4);
        Assert.Equal("*test* word", wrapped);
        Assert.Equal(1, wStart);
        Assert.Equal(4, wLen);

        var (unwrapped, uStart, uLen) = MarkdownEditorOperations.ToggleItalic(wrapped, wStart, wLen);
        Assert.Equal("test word", unwrapped);
        Assert.Equal(0, uStart);
        Assert.Equal(4, uLen);
    }

    [Fact]
    public void ToggleHeading_TogglesLevel1OnAndOff()
    {
        string text = "Cyrillic Заголовок";
        var (h1, _, _) = MarkdownEditorOperations.ToggleHeading(text, 5, 0);
        Assert.Equal("# Cyrillic Заголовок", h1);

        var (plain, _, _) = MarkdownEditorOperations.ToggleHeading(h1, 5, 0);
        Assert.Equal("Cyrillic Заголовок", plain);
    }

    [Fact]
    public void CycleHeading_CyclesAllSixLevelsAndClears()
    {
        string text = "Cyrillic Заголовок";
        var (h1, _, _) = MarkdownEditorOperations.CycleHeading(text, 5, 0);
        Assert.Equal("# Cyrillic Заголовок", h1);

        var (h2, _, _) = MarkdownEditorOperations.CycleHeading(h1, 5, 0);
        Assert.Equal("## Cyrillic Заголовок", h2);

        var (h3, _, _) = MarkdownEditorOperations.CycleHeading(h2, 5, 0);
        Assert.Equal("### Cyrillic Заголовок", h3);

        var (h4, _, _) = MarkdownEditorOperations.CycleHeading(h3, 5, 0);
        Assert.Equal("#### Cyrillic Заголовок", h4);

        var (h5, _, _) = MarkdownEditorOperations.CycleHeading(h4, 5, 0);
        Assert.Equal("##### Cyrillic Заголовок", h5);

        var (h6, _, _) = MarkdownEditorOperations.CycleHeading(h5, 5, 0);
        Assert.Equal("###### Cyrillic Заголовок", h6);

        var (plain, _, _) = MarkdownEditorOperations.CycleHeading(h6, 5, 0);
        Assert.Equal("Cyrillic Заголовок", plain);
    }

    [Fact]
    public void ToggleCode_SingleLine_WrapsWithBackticksAndUnwraps()
    {
        var (wrapped, wStart, wLen) = MarkdownEditorOperations.ToggleCode("var x = 10;", 4, 1);
        Assert.Equal("var `x` = 10;", wrapped);
        Assert.Equal(5, wStart);
        Assert.Equal(1, wLen);

        var (unwrapped, uStart, uLen) = MarkdownEditorOperations.ToggleCode(wrapped, wStart, wLen);
        Assert.Equal("var x = 10;", unwrapped);
        Assert.Equal(4, uStart);
        Assert.Equal(1, uLen);
    }

    [Fact]
    public void ToggleCode_MultiLine_WrapsWithFencedBlockAndUnwraps()
    {
        string code = "int a = 1;\nint b = 2;";
        var (fenced, _, _) = MarkdownEditorOperations.ToggleCode(code, 0, code.Length);
        Assert.Equal("```\nint a = 1;\nint b = 2;\n```", fenced);

        var (unfenced, _, _) = MarkdownEditorOperations.ToggleCode(fenced, 0, fenced.Length);
        Assert.Equal(code, unfenced);
    }

    [Fact]
    public void ToggleBulletList_SingleAndMultiline()
    {
        string lines = "First line\nSecond line";
        var (bulleted, _, _) = MarkdownEditorOperations.ToggleBulletList(lines, 0, lines.Length);
        Assert.Equal("- First line\n- Second line", bulleted);

        var (unbulleted, _, _) = MarkdownEditorOperations.ToggleBulletList(bulleted, 0, bulleted.Length);
        Assert.Equal("First line\nSecond line", unbulleted);
    }

    [Fact]
    public void ToggleBulletList_PreservesIndent()
    {
        string text = "  Indented line";
        var (bulleted, _, _) = MarkdownEditorOperations.ToggleBulletList(text, 4, 0);
        Assert.Equal("  - Indented line", bulleted);

        var (unbulleted, _, _) = MarkdownEditorOperations.ToggleBulletList(bulleted, 4, 0);
        Assert.Equal("  Indented line", unbulleted);
    }

    [Fact]
    public void ToggleNumberedList_SequentiallyNumbersLines()
    {
        string lines = "Alpha\nBeta\nGamma";
        var (numbered, _, _) = MarkdownEditorOperations.ToggleNumberedList(lines, 0, lines.Length);
        Assert.Equal("1. Alpha\n2. Beta\n3. Gamma", numbered);

        var (unnum, _, _) = MarkdownEditorOperations.ToggleNumberedList(numbered, 0, numbered.Length);
        Assert.Equal("Alpha\nBeta\nGamma", unnum);
    }

    [Fact]
    public void ToggleCheckbox_TogglesStatesAndRemoves()
    {
        string text = "My task";
        // None -> unchecked
        var (chk1, _, _) = MarkdownEditorOperations.ToggleCheckbox(text, 0, 0);
        Assert.Equal("- [ ] My task", chk1);

        // unchecked -> checked
        var (chk2, _, _) = MarkdownEditorOperations.ToggleCheckbox(chk1, 0, 0);
        Assert.Equal("- [x] My task", chk2);

        // checked -> bullet
        var (chk3, _, _) = MarkdownEditorOperations.ToggleCheckbox(chk2, 0, 0);
        Assert.Equal("- My task", chk3);

        // bullet -> plain text
        var (plain, _, _) = MarkdownEditorOperations.ToggleBulletList(chk3, 0, 0);
        Assert.Equal("My task", plain);
    }

    [Fact]
    public void InsertLink_WithAndWithoutSelection()
    {
        // With selection: wraps word and selects the URL
        var (withSel, selStart, selLen) = MarkdownEditorOperations.InsertLink("Visit website for info", 6, 7, "https://example.com");
        Assert.Equal("Visit [website](https://example.com) for info", withSel);
        Assert.Equal(16, selStart); // selects "https://example.com"
        Assert.Equal("https://example.com".Length, selLen);

        // Without selection: inserts placeholder and selects text placeholder
        var (withoutSel, noStart, noLen) = MarkdownEditorOperations.InsertLink("Visit  for info", 6, 0, "https://example.com");
        Assert.Equal("Visit [текст](https://example.com) for info", withoutSel);
        Assert.Equal(7, noStart); // selects "текст"
        Assert.Equal("текст".Length, noLen);
    }

    [Fact]
    public void GenerateTable_ProducesStandardMarkdownPipeTable()
    {
        string table = MarkdownEditorOperations.GenerateTable(rows: 2, cols: 2).Replace("\r\n", "\n");
        string expected =
            "| Заголовок 1 | Заголовок 2 |\n" +
            "| --- | --- |\n" +
            "|  |  |\n" +
            "|  |  |";

        Assert.Equal(expected, table);
    }

    [Fact]
    public void GenerateTable_ClampsDimensions()
    {
        // Min bounds: 1 row, 1 col
        string minTable = MarkdownEditorOperations.GenerateTable(rows: 0, cols: 0);
        Assert.Contains("| Заголовок 1 |", minTable);
        Assert.Contains("| --- |", minTable);
        Assert.Contains("|  |", minTable);

        // Max bounds: 50 rows, 20 cols
        string maxTable = MarkdownEditorOperations.GenerateTable(rows: 100, cols: 50);
        Assert.Contains("Заголовок 20", maxTable);
        Assert.DoesNotContain("Заголовок 21", maxTable);
    }

    [Fact]
    public void HandleEnter_BulletListContinuation_PreservesIndentAndMarker()
    {
        string text = "- Item 1";
        var res = MarkdownEditorOperations.HandleEnter(text, text.Length, 0);
        Assert.True(res.Handled);
        Assert.Equal(ListContinuationAction.Continue, res.Action);
        Assert.Equal("- Item 1\n- ", res.Text);
        Assert.Equal("- Item 1\n- ".Length, res.SelectionStart);
    }

    [Fact]
    public void HandleEnter_IndentedBulletListContinuation()
    {
        string text = "  * Sub-item";
        var res = MarkdownEditorOperations.HandleEnter(text, text.Length, 0);
        Assert.True(res.Handled);
        Assert.Equal(ListContinuationAction.Continue, res.Action);
        Assert.Equal("  * Sub-item\n  * ", res.Text);
    }

    [Fact]
    public void HandleEnter_NumberedListContinuation_IncrementsNumber()
    {
        string text = "4. Fourth point";
        var res = MarkdownEditorOperations.HandleEnter(text, text.Length, 0);
        Assert.True(res.Handled);
        Assert.Equal(ListContinuationAction.Continue, res.Action);
        Assert.Equal("4. Fourth point\n5. ", res.Text);
    }

    [Fact]
    public void HandleEnter_CheckboxListContinuation_ContinuesWithEmptyBox()
    {
        string text = "- [x] Done task";
        var res = MarkdownEditorOperations.HandleEnter(text, text.Length, 0);
        Assert.True(res.Handled);
        Assert.Equal(ListContinuationAction.Continue, res.Action);
        Assert.Equal("- [x] Done task\n- [ ] ", res.Text);
    }

    [Fact]
    public void HandleEnter_EmptyListItem_TerminatesList()
    {
        string text = "- ";
        var res = MarkdownEditorOperations.HandleEnter(text, 2, 0);
        Assert.True(res.Handled);
        Assert.Equal(ListContinuationAction.Terminate, res.Action);
        Assert.Equal(string.Empty, res.Text);
    }

    [Fact]
    public void HandleEnter_EmptyIndentedNumberedItem_TerminatesList()
    {
        string text = "  1. ";
        var res = MarkdownEditorOperations.HandleEnter(text, 5, 0);
        Assert.True(res.Handled);
        Assert.Equal(ListContinuationAction.Terminate, res.Action);
        Assert.Equal(string.Empty, res.Text);
    }

    [Fact]
    public void HandleEnter_EmptyCheckboxItem_TerminatesList()
    {
        string text = "- [ ] ";
        var res = MarkdownEditorOperations.HandleEnter(text, 6, 0);
        Assert.True(res.Handled);
        Assert.Equal(ListContinuationAction.Terminate, res.Action);
        Assert.Equal(string.Empty, res.Text);
    }

    [Fact]
    public void HandleEnter_OrdinaryParagraph_DoesNotHandle()
    {
        string text = "This is a regular sentence";
        var res = MarkdownEditorOperations.HandleEnter(text, 10, 0);
        Assert.False(res.Handled);
        Assert.Equal(ListContinuationAction.None, res.Action);
    }
}
