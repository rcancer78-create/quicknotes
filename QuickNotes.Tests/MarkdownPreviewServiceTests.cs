using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.ViewModels;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.UiSensitive)]
public class MarkdownPreviewServiceTests
{
    private static void RunInSta(Action action) => StaTestHarness.Run(action, TimeSpan.FromSeconds(15));

    [Fact]
    public void ParseBlocks_Headings_DetectsAllSixLevelsCorrectly()
    {
        var service = new MarkdownPreviewService();
        var markdown = """
            # Heading Level 1
            ## Heading Level 2
            ### Heading Level 3
            #### Heading Level 4
            ##### Heading Level 5
            ###### Heading Level 6
            #NotAHeading
            """;

        var blocks = service.ParseBlocks(markdown);

        Assert.Equal(7, blocks.Count);
        for (int i = 0; i < 6; i++)
        {
            var heading = Assert.IsType<MarkdownHeadingBlock>(blocks[i]);
            Assert.Equal(i + 1, heading.Level);
            var textInline = Assert.IsType<MarkdownTextInline>(Assert.Single(heading.Inlines));
            Assert.Equal($"Heading Level {i + 1}", textInline.Text);
        }

        // #NotAHeading without space after # should be parsed as regular paragraph
        var para = Assert.IsType<MarkdownParagraphBlock>(blocks[6]);
        var paraText = Assert.IsType<MarkdownTextInline>(Assert.Single(para.Inlines));
        Assert.Equal("#NotAHeading", paraText.Text);
    }

    [Fact]
    public void ParseBlocks_Lists_DetectsUnorderedAndOrderedLists()
    {
        var service = new MarkdownPreviewService();
        var markdown = """
            - Bullet minus
            * Bullet asterisk
            + Bullet plus

            1. Numbered first
            2. Numbered second
            """;

        var blocks = service.ParseBlocks(markdown);

        Assert.Equal(2, blocks.Count);

        var unordered = Assert.IsType<MarkdownUnorderedListBlock>(blocks[0]);
        Assert.Equal(3, unordered.Items.Count);
        Assert.Equal("Bullet minus", Assert.IsType<MarkdownTextInline>(unordered.Items[0].Inlines[0]).Text);
        Assert.Equal("Bullet asterisk", Assert.IsType<MarkdownTextInline>(unordered.Items[1].Inlines[0]).Text);
        Assert.Equal("Bullet plus", Assert.IsType<MarkdownTextInline>(unordered.Items[2].Inlines[0]).Text);

        var ordered = Assert.IsType<MarkdownOrderedListBlock>(blocks[1]);
        Assert.Equal(1, ordered.StartNumber);
        Assert.Equal(2, ordered.Items.Count);
        Assert.Equal("Numbered first", Assert.IsType<MarkdownTextInline>(ordered.Items[0].Inlines[0]).Text);
        Assert.Equal("Numbered second", Assert.IsType<MarkdownTextInline>(ordered.Items[1].Inlines[0]).Text);
    }

    [Fact]
    public void ParseBlocks_TaskLists_DetectsCheckedAndUncheckedBoxes()
    {
        var service = new MarkdownPreviewService();
        var markdown = """
            - [ ] Pending task
            - [x] Completed task lowercase
            * [X] Completed task uppercase
            """;

        var blocks = service.ParseBlocks(markdown);

        var taskList = Assert.IsType<MarkdownTaskListBlock>(Assert.Single(blocks));
        Assert.Equal(3, taskList.Items.Count);

        Assert.False(taskList.Items[0].IsChecked);
        Assert.Equal("Pending task", Assert.IsType<MarkdownTextInline>(taskList.Items[0].Inlines[0]).Text);

        Assert.True(taskList.Items[1].IsChecked);
        Assert.Equal("Completed task lowercase", Assert.IsType<MarkdownTextInline>(taskList.Items[1].Inlines[0]).Text);

        Assert.True(taskList.Items[2].IsChecked);
        Assert.Equal("Completed task uppercase", Assert.IsType<MarkdownTextInline>(taskList.Items[2].Inlines[0]).Text);
    }

    [Fact]
    public void ParseBlocks_FencedCode_ExtractsLanguageAndPreservesExactCode()
    {
        var service = new MarkdownPreviewService();
        var markdown = """
            ```csharp
            var greeting = "Hello, world!";
            // *not italic* and [not a link](http://ignore.me)
            Console.WriteLine(greeting);
            ```
            """;

        var blocks = service.ParseBlocks(markdown);

        var codeBlock = Assert.IsType<MarkdownFencedCodeBlock>(Assert.Single(blocks));
        Assert.Equal("csharp", codeBlock.Language);
        Assert.Contains("var greeting = \"Hello, world!\";", codeBlock.Code);
        Assert.Contains("// *not italic* and [not a link](http://ignore.me)", codeBlock.Code);
        Assert.Contains("Console.WriteLine(greeting);", codeBlock.Code);
    }

    [Fact]
    public void ParseInlines_Formatting_SupportsBoldItalicAndStrikethrough()
    {
        var service = new MarkdownPreviewService();
        var text = "**bold1** and __bold2__ and *italic1* and _italic2_ and ***bolditalic*** and ~~deleted~~";

        var inlines = service.ParseInlines(text);

        Assert.Contains(inlines, i => i is MarkdownBoldInline);
        Assert.Contains(inlines, i => i is MarkdownItalicInline);
        Assert.Contains(inlines, i => i is MarkdownBoldItalicInline);
        Assert.Contains(inlines, i => i is MarkdownStrikethroughInline);

        var bold = inlines.OfType<MarkdownBoldInline>().First();
        Assert.Equal("bold1", Assert.IsType<MarkdownTextInline>(bold.Children[0]).Text);

        var italic = inlines.OfType<MarkdownItalicInline>().First();
        Assert.Equal("italic1", Assert.IsType<MarkdownTextInline>(italic.Children[0]).Text);

        var strike = inlines.OfType<MarkdownStrikethroughInline>().First();
        Assert.Equal("deleted", Assert.IsType<MarkdownTextInline>(strike.Children[0]).Text);
    }

    [Fact]
    public void ParseInlines_InlineCode_DoesNotInterpretMarkdownInside()
    {
        var service = new MarkdownPreviewService();
        var text = "Use `var list = **not bold**;` for initialization";

        var inlines = service.ParseInlines(text);

        var code = inlines.OfType<MarkdownCodeInline>().Single();
        Assert.Equal("var list = **not bold**;", code.Code);
    }

    [Fact]
    public void ParseInlines_Links_SeparatesSafeHttpAndHttpsFromUnsafeUrls()
    {
        var service = new MarkdownPreviewService();
        var text = "[Https](https://example.com) [Http](http://insecure.org) [Script](javascript:alert(1)) [LocalFile](file:///c:/passwords.txt)";

        var inlines = service.ParseInlines(text);
        var links = inlines.OfType<MarkdownLinkInline>().ToList();

        Assert.Equal(4, links.Count);

        var httpsLink = links.Single(l => l.Text == "Https");
        Assert.True(httpsLink.IsSafe);
        Assert.Equal("https://example.com", httpsLink.Url);

        var httpLink = links.Single(l => l.Text == "Http");
        Assert.True(httpLink.IsSafe);
        Assert.Equal("http://insecure.org", httpLink.Url);

        var scriptLink = links.Single(l => l.Text == "Script");
        Assert.False(scriptLink.IsSafe);
        Assert.Equal("javascript:alert(1)", scriptLink.Url);

        var fileLink = links.Single(l => l.Text == "LocalFile");
        Assert.False(fileLink.IsSafe);
        Assert.Equal("file:///c:/passwords.txt", fileLink.Url);
    }

    [Fact]
    public void BuildFlowDocument_EscapesHtmlAndScriptsAsPlainText()
    {
        RunInSta(() =>
        {
            var service = new MarkdownPreviewService();
            var dangerousInput = """
                <script>alert('xss')</script>
                <img src="x" onerror="alert(1)" />
                <iframe src="https://evil.com"></iframe>
                """;

            var doc = service.BuildFlowDocument(dangerousInput);

            Assert.NotNull(doc);
            var paragraph = Assert.IsType<Paragraph>(Assert.Single(doc.Blocks));

            // Verify all text is rendered into literal Text/Run without HTML execution
            var runs = paragraph.Inlines.OfType<Run>().ToList();
            var combinedText = string.Join("", runs.Select(r => r.Text));

            Assert.Contains("<script>alert('xss')</script>", combinedText);
            Assert.Contains("<img src=\"x\" onerror=\"alert(1)\" />", combinedText);
            Assert.Contains("<iframe src=\"https://evil.com\"></iframe>", combinedText);

            // Ensure no UI containers, active scripts or web browsers exist
            Assert.Empty(paragraph.Inlines.OfType<InlineUIContainer>());
        });
    }

    [Fact]
    public void BuildFlowDocument_RendersSafeHyperlinksAndRejectsUnsafeSchemes()
    {
        RunInSta(() =>
        {
            var service = new MarkdownPreviewService();
            var markdown = "[Trusted](https://example.com) and [Attack](javascript:alert(1))";

            var doc = service.BuildFlowDocument(markdown);
            var paragraph = Assert.IsType<Paragraph>(Assert.Single(doc.Blocks));

            // Safe link must be a Hyperlink
            var hyperlink = Assert.Single(paragraph.Inlines.OfType<Hyperlink>());
            Assert.Equal(new Uri("https://example.com"), hyperlink.NavigateUri);

            // Unsafe link must NOT be a Hyperlink; it must be plain text Run
            var runs = paragraph.Inlines.OfType<Run>().ToList();
            Assert.Contains(runs, r => r.Text.Contains("javascript:alert(1)"));
        });
    }

    [Fact]
    public void BuildFlowDocument_RendersTaskListsWithCheckBoxes()
    {
        RunInSta(() =>
        {
            var service = new MarkdownPreviewService();
            var markdown = """
                - [ ] Item 1
                - [x] Item 2
                """;

            var doc = service.BuildFlowDocument(markdown);
            Assert.Equal(2, doc.Blocks.Count);

            var p1 = Assert.IsType<Paragraph>(doc.Blocks.ElementAt(0));
            var container1 = Assert.IsType<InlineUIContainer>(p1.Inlines.FirstInline);
            var cb1 = Assert.IsType<CheckBox>(container1.Child);
            Assert.False(cb1.IsChecked);

            var p2 = Assert.IsType<Paragraph>(doc.Blocks.ElementAt(1));
            var container2 = Assert.IsType<InlineUIContainer>(p2.Inlines.FirstInline);
            var cb2 = Assert.IsType<CheckBox>(container2.Child);
            Assert.True(cb2.IsChecked);
        });
    }

    [Fact]
    public void BuildFlowDocument_EmptyAndInvalidMarkdown_DoesNotThrowAndReturnsContent()
    {
        RunInSta(() =>
        {
            var service = new MarkdownPreviewService();

            // Null input
            var nullDoc = service.BuildFlowDocument(null);
            Assert.NotNull(nullDoc);
            Assert.Single(nullDoc.Blocks);

            // Empty input
            var emptyDoc = service.BuildFlowDocument(string.Empty);
            Assert.NotNull(emptyDoc);
            Assert.Single(emptyDoc.Blocks);

            // Whitespace input
            var wsDoc = service.BuildFlowDocument("   \t  \n  ");
            Assert.NotNull(wsDoc);

            // Unclosed formatting
            var unclosedDoc = service.BuildFlowDocument("***unclosed bold italic and [broken link(https://test.org");
            Assert.NotNull(unclosedDoc);
            Assert.NotEmpty(unclosedDoc.Blocks);

            // Unclosed code block
            var unclosedCodeDoc = service.BuildFlowDocument("```csharp\nint a = 1;");
            Assert.NotNull(unclosedCodeDoc);
            Assert.NotEmpty(unclosedCodeDoc.Blocks);
        });
    }

    [Fact]
    public void NoteEditorViewModel_PreservesRawTextAndTogglesModes()
    {
        RunInSta(() =>
        {
            var rawMarkdown = """
                # Plan
                - [ ] Write tests
                - [x] Implement preview

                Check **bold** and `code` at [site](https://example.com).
                """;

            var tagService = new TagDetectionService();
            var tags = new List<Tag>();
            var note = new Note
            {
                Id = 1,
                Text = rawMarkdown,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };

            using var vm = new NoteEditorViewModel(tagService, tags, existingNote: note, draftJournalService: NoOpDraftJournalService.Instance);

            // 1. Initial state must be plain-text editing mode
            Assert.False(vm.IsPreviewMode);
            Assert.True(vm.IsEditMode);
            Assert.Equal(rawMarkdown, vm.Text);

            // 2. Switch to Preview mode
            vm.ShowPreviewModeCommand.Execute(null);
            Assert.True(vm.IsPreviewMode);
            Assert.False(vm.IsEditMode);
            Assert.NotNull(vm.PreviewDocument);
            Assert.NotEmpty(vm.PreviewDocument.Blocks);

            // 3. Toggle back to Edit mode
            vm.TogglePreviewModeCommand.Execute(null);
            Assert.False(vm.IsPreviewMode);
            Assert.True(vm.IsEditMode);

            // 4. Modify plain text in edit mode
            vm.Text += "\n\nAdditional text line";

            // 5. Apply changes back to Note and verify format is strictly preserved plain text
            vm.ApplyToNote(note);
            Assert.Equal(vm.Text, note.Text);
            Assert.DoesNotContain("<html", note.Text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<body", note.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("# Plan", note.Text);
            Assert.Contains("Additional text line", note.Text);
        });
    }
}
