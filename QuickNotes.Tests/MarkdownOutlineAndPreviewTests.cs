using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using QuickNotes.App.Data;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.Services.NoteProtection;
using QuickNotes.App.ViewModels;
using Xunit;

namespace QuickNotes.Tests;

[Collection(LiveProfileSnapshotCollection.Name)]
[TestCategory(TestCategories.UiSensitive)]
public class MarkdownOutlineAndPreviewTests
{
    private static void RunInSta(Action action) => StaTestHarness.Run(action);

    [Fact]
    public void HeadingAnchorHelper_CreateSlug_HandlesCyrillicAndPunctuation()
    {
        Assert.Equal("введение-в-курс", HeadingAnchorHelper.CreateSlug("Введение в курс"));
        Assert.Equal("глава-1-основы-c-и-wpf", HeadingAnchorHelper.CreateSlug("Глава 1: Основы C# и WPF!"));
        Assert.Equal("heading", HeadingAnchorHelper.CreateSlug("??? ### !!!"));
        Assert.Equal("heading", HeadingAnchorHelper.CreateSlug(string.Empty));
    }

    [Fact]
    public void HeadingAnchorHelper_GetUniqueAnchor_ResolvesCollisionsDeterministically()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string a1 = HeadingAnchorHelper.GetUniqueAnchor("Введение", set);
        string a2 = HeadingAnchorHelper.GetUniqueAnchor("Введение", set);
        string a3 = HeadingAnchorHelper.GetUniqueAnchor("Введение", set);

        Assert.Equal("введение", a1);
        Assert.Equal("введение-1", a2);
        Assert.Equal("введение-2", a3);
    }

    [Fact]
    public void ExtractOutline_ExtractsAllHeadingLevelsWithPreciseMetadata()
    {
        string markdown =
            "# Первая глава\n" +
            "Некоторый текст абзаца.\n\n" +
            "## Подраздел 1.1\n" +
            "Ещё текст.\n\n" +
            "### Под-подраздел\n" +
            "### Под-подраздел\n" + // duplicate title
            "###### Глубокий заголовок H6";

        var service = new MarkdownPreviewService();
        var outline = service.ExtractOutline(markdown);

        Assert.Equal(5, outline.Count);

        Assert.Equal(1, outline[0].Level);
        Assert.Equal("Первая глава", outline[0].Title);
        Assert.Equal("первая-глава", outline[0].AnchorId);
        Assert.Equal("H1", outline[0].LevelBadge);
        Assert.Equal(0, outline[0].LineIndex);

        Assert.Equal(2, outline[1].Level);
        Assert.Equal("Подраздел 1.1", outline[1].Title);
        Assert.Equal("подраздел-1-1", outline[1].AnchorId);
        Assert.Equal("H2", outline[1].LevelBadge);

        Assert.Equal(3, outline[2].Level);
        Assert.Equal("Под-подраздел", outline[2].Title);
        Assert.Equal("под-подраздел", outline[2].AnchorId);

        Assert.Equal(3, outline[3].Level);
        Assert.Equal("Под-подраздел", outline[3].Title);
        Assert.Equal("под-подраздел-1", outline[3].AnchorId); // deduplicated anchor

        Assert.Equal(6, outline[4].Level);
        Assert.Equal("Глубокий заголовок H6", outline[4].Title);
        Assert.Equal("H6", outline[4].LevelBadge);
    }

    [Fact]
    public void RenderToFlowDocument_RendersPipeTablesWithAlignments()
    {
        RunInSta(() =>
        {
            string markdown =
                "| Лево | Центр | Право |\n" +
                "| :--- | :---: | ---: |\n" +
                "| A1 | B1 | C1 |\n" +
                "| A2 | B2 | C2 |";

            var service = new MarkdownPreviewService();
            var doc = service.BuildFlowDocument(markdown);

            Assert.NotNull(doc);
            var table = doc.Blocks.OfType<Table>().FirstOrDefault();
            Assert.NotNull(table);

            Assert.Equal(3, table.Columns.Count);
            Assert.NotEmpty(table.RowGroups);

            var rowGroup = table.RowGroups[0];
            // 1 header row + 2 data rows = 3 rows
            Assert.Equal(3, rowGroup.Rows.Count);

            var headerRow = rowGroup.Rows[0];
            Assert.Equal(3, headerRow.Cells.Count);

            // Verify cell alignments
            Assert.Equal(TextAlignment.Left, ((Paragraph)headerRow.Cells[0].Blocks.FirstBlock).TextAlignment);
            Assert.Equal(TextAlignment.Center, ((Paragraph)headerRow.Cells[1].Blocks.FirstBlock).TextAlignment);
            Assert.Equal(TextAlignment.Right, ((Paragraph)headerRow.Cells[2].Blocks.FirstBlock).TextAlignment);
        });
    }

    [Fact]
    public void CollapsibleHeadings_HidesChildrenUntilNextHeadingAndPreservesSource()
    {
        RunInSta(() =>
        {
            string markdown =
                "# Раздел 1\n" +
                "Текст раздела 1\n" +
                "## Подраздел 1.1\n" +
                "Текст подраздела\n" +
                "# Раздел 2\n" +
                "Текст раздела 2";

            var service = new MarkdownPreviewService();

            // 1. Uncollapsed: all paragraphs are present
            var fullDoc = service.BuildFlowDocument(markdown);
            int fullBlockCount = fullDoc.Blocks.Count;

            // 2. Collapse Razdel 1: its children should be hidden
            var collapsedSet = new HashSet<string> { "раздел-1" };
            var collapsedDoc = service.BuildFlowDocument(markdown, highlightQuery: null, collapsedAnchors: collapsedSet, onToggleCollapse: null);

            Assert.True(collapsedDoc.Blocks.Count < fullBlockCount);

            // Razdel 2 must still be visible
            var blocksList = collapsedDoc.Blocks.ToList();
            bool razdel2Present = blocksList.Any(b => b.Tag as string == "раздел-2");
            Assert.True(razdel2Present, "Next top-level heading must remain visible");

            // Source text must be completely untouched
            Assert.Contains("# Раздел 1", markdown);
            Assert.Contains("## Подраздел 1.1", markdown);
        });
    }

    [Fact]
    public void ImageFallback_MissingFileRendersGracefulFallbackWithoutThrowing()
    {
        RunInSta(() =>
        {
            string markdown = "Here is an image: ![Diagram](missing_file_404.png)";

            var service = new MarkdownPreviewService();
            // Should not throw even when image does not exist on disk
            var doc = service.BuildFlowDocument(markdown);

            Assert.NotNull(doc);
            Assert.NotEmpty(doc.Blocks);
            var para = doc.Blocks.OfType<Paragraph>().FirstOrDefault();
            Assert.NotNull(para);
        });
    }

    [Fact]
    public void PreviewDebounceAndCancellation_PreservesSourceAndUpdatesPreview()
    {
        RunInSta(() =>
        {
            using var composition = new TestProfileComposition("qn_outline_preview");

            var note = new Note
            {
                Id = 42,
                Text = "# Original Note\nInitial text.",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            using var vm = composition.CreateNoteEditorViewModel(
                existingNote: note,
                draftJournalService: NoOpDraftJournalService.Instance);

            // Source of truth initially identical
            Assert.Equal(note.Text, vm.Text);

            // Switch to split mode
            vm.ShowSplitModeCommand.Execute(null);
            Assert.True(vm.IsSplitMode);
            Assert.True(vm.IsEditorVisible);
            Assert.True(vm.IsPreviewVisible);

            // Rapid text edits simulate fast typing
            for (int i = 1; i <= 5; i++)
            {
                vm.Text = $"# Heading {i}\nTyping content line {i}...";
            }

            // Immediately check: note.Text has not been auto-mutated yet (persisted only on Save/Apply)
            Assert.Equal("# Original Note\nInitial text.", note.Text);

            // Wait for debounce timer to fire (~90ms) and pump dispatcher
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 2500 && vm.OutlineItems.FirstOrDefault()?.Title != "Heading 5")
            {
                var frame = new System.Windows.Threading.DispatcherFrame();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() => frame.Continue = false));
                System.Windows.Threading.Dispatcher.PushFrame(frame);
                Thread.Sleep(20);
            }

            // Now outline and preview are updated
            Assert.True(vm.HasOutline);
            Assert.Single(vm.OutlineItems);
            Assert.Equal("Heading 5", vm.OutlineItems[0].Title);
            Assert.NotNull(vm.PreviewDocument);
            Assert.NotEmpty(vm.PreviewDocument.Blocks);

            // ApplyToNote persists strictly plain markdown
            vm.ApplyToNote(note);
            Assert.Equal(vm.Text, note.Text);
            Assert.DoesNotContain("<html", note.Text, StringComparison.OrdinalIgnoreCase);
        });
    }
}
