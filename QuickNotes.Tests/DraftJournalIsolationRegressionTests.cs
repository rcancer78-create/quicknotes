using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.Services.DraftJournal;
using QuickNotes.App.ViewModels;
using Xunit;

namespace QuickNotes.Tests;

/// <summary>
/// Regression tests verifying that editor and ViewModel tests never read or write
/// to the production default DraftJournals directory (%LOCALAPPDATA%\QuickNotes\DraftJournals).
/// Placed in LiveProfileSnapshot collection to ensure serialized execution with other snapshot tests.
/// </summary>
[Collection(LiveProfileSnapshotCollection.Name)]
[TestCategory(TestCategories.Integration)]
public sealed class DraftJournalIsolationRegressionTests
{
    private static string[] SnapshotDirectory(string dirPath)
    {
        if (!Directory.Exists(dirPath))
        {
            return Array.Empty<string>();
        }

        return Directory.GetFileSystemEntries(dirPath, "*", SearchOption.AllDirectories)
            .Select(p => p + "|" + (File.Exists(p) ? new FileInfo(p).Length + "|" + File.GetLastWriteTimeUtc(p).Ticks : "dir"))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    [Fact]
    public async Task OrdinaryEditorAndViewModelTests_DoNotTouchProductionDraftJournalsDirectory()
    {
        string liveProfile = QuickNotesDbContext.LiveProfileDirectory;
        string liveJournals = Path.Combine(liveProfile, "DraftJournals");

        string[] beforeSnapshot = SnapshotDirectory(liveJournals);

        using (var composition = new TestProfileComposition("qn_draft_isolation"))
        {
            using (var editor1 = composition.CreateNoteEditorViewModel(
                       initialTitle: "Test Isolation Title " + Guid.NewGuid().ToString("N"),
                       initialText: "Test Isolation Text with some changes " + Guid.NewGuid().ToString("N")))
            {
                editor1.Title = "Test Isolation Title " + Guid.NewGuid().ToString("N");
                editor1.Text = "Test Isolation Text with some changes " + Guid.NewGuid().ToString("N");
                await Task.Delay(700);
                await editor1.FlushDraftJournalAsync();
            }

            using (var editor2 = composition.CreateNoteEditorViewModel(initialTitle: "Factory Title", initialText: "Factory Content"))
            {
                editor2.Text = "Updated factory content";
                await Task.Delay(700);
            }

            using (var mainVm = composition.CreateMainViewModel())
            {
                mainVm.CreateEmptyNote();
            }
        }

        string[] afterSnapshot = SnapshotDirectory(liveJournals);
        Assert.Equal(beforeSnapshot, afterSnapshot);
    }

    [Fact]
    public void DraftJournalService_Constructor_ResolvesExplicitOrProfileDirectoryDeterministically()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "qn_explicit_draft_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var service = new DraftJournalService(baseDirectory: tempDir);
            Assert.Equal(Path.Combine(tempDir, "DraftJournals"), service.JournalsDirectory);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task EditorDisposal_CancelsAndDrainsDebounceTimer_WithoutPostDisposalWrites()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "qn_disposal_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var service = new DraftJournalService(baseDirectory: tempDir);
            var editor = new NoteEditorViewModel(
                new TagDetectionService(),
                new System.Collections.Generic.List<Tag>(),
                draftJournalService: service);

            editor.DebounceDelay = TimeSpan.FromMilliseconds(50);
            editor.Text = "Some typing just before close";

            // Immediately dispose
            editor.Dispose();

            // Wait beyond debounce delay
            await Task.Delay(200);

            // Verify no journal was saved or orphaned
            string journalsDir = Path.Combine(tempDir, "DraftJournals");
            if (Directory.Exists(journalsDir))
            {
                Assert.Empty(Directory.GetFiles(journalsDir));
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
