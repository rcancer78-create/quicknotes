using System;
using System.Collections.Generic;
using System.IO;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.ViewModels;
using Xunit;

namespace QuickNotes.Tests;

[Collection(LiveProfileSnapshotCollection.Name)]
[TestCategory(TestCategories.UiSensitive)]
public class MarkdownTransactionalPasteTests : IDisposable
{
    private readonly TestProfileComposition _composition = new("qn_paste");

    private static void RunInSta(Action action) => StaTestHarness.Run(action);

    public void Dispose() => _composition.Dispose();

    [Fact]
    public void TryPasteImageBytes_Success_InsertsLinkAndCreatesAttachment()
    {
        RunInSta(() =>
        {
            var note = new Note
            {
                Id = 10,
                Title = "Test Note",
                Text = "Paragraph 1\n\nParagraph 2",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            using var vm = _composition.CreateNoteEditorViewModel(
                existingNote: note,
                draftJournalService: NoOpDraftJournalService.Instance);

            int caret = 12; // between the paragraphs
            byte[] imageBytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            bool success = vm.TryPasteImageBytes(imageBytes, "screenshot.png", caret, out var status);

            Assert.True(success);
            Assert.Null(status);

            Assert.Single(vm.Attachments);
            var att = vm.Attachments[0];
            Assert.Equal("screenshot.png", att.OriginalFileName);
            Assert.True(File.Exists(att.FullPath));

            Assert.Contains("![screenshot.png](", vm.Text);
            Assert.Contains("Paragraph 1", vm.Text);
            Assert.Contains("Paragraph 2", vm.Text);
        });
    }

    private class FaultyAttachmentStorageService : IAttachmentStorageService
    {
        public string BaseDirectory => string.Empty;
        public string AttachmentsDirectory => string.Empty;
        public AttachmentSaveResult SaveAttachment(string sourceFilePath, long maxSizeBytes) =>
            throw new IOException("Simulated disk error");
        public AttachmentSaveResult SaveFromBytes(byte[] content, string originalFileName, long maxSizeBytes) =>
            throw new IOException("Simulated disk error");
        public string GetFullPath(string relativePath) => string.Empty;
        public bool FileExists(string relativePath) => false;
        public bool DeleteManagedFileIfUnreferenced(QuickNotesDbContext db, string storedFileName) => true;
        public void CleanupUnreferencedFiles(QuickNotesDbContext db, IEnumerable<string> storedFileNames) { }
    }

    [Fact]
    public void TryPasteImageBytes_RollbackOnFailure_CleansUpAndPreservesOriginalText()
    {
        RunInSta(() =>
        {
            string originalText = "Strictly pristine original text.";
            var note = new Note
            {
                Id = 20,
                Text = originalText,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            using var vm = new NoteEditorViewModel(
                _composition.TagDetectionService,
                new List<Tag>(),
                existingNote: note,
                contextFactory: _composition.ContextFactory,
                attachmentStorageService: new FaultyAttachmentStorageService(),
                settingsService: _composition.SettingsService,
                noteProtectionService: _composition.ProtectionService,
                draftJournalService: NoOpDraftJournalService.Instance,
                mutationCoordinator: _composition.MutationCoordinator);

            byte[] imageBytes = new byte[] { 10, 20, 30 };
            bool success = vm.TryPasteImageBytes(imageBytes, "fail.png", 5, out var status);

            Assert.False(success);
            Assert.NotNull(status);
            Assert.Equal(originalText, vm.Text);
            Assert.Empty(vm.Attachments);
            Assert.Empty(Directory.GetFiles(_composition.AttachmentsDirectory, "*.*", SearchOption.AllDirectories));
        });
    }

    [Fact]
    public void TryPasteImageBytes_WhenNoteLocked_RejectsImmediatelyWithNoDiskLeakage()
    {
        RunInSta(() =>
        {
            var note = new Note
            {
                Id = 30,
                Text = "Encrypted cipher text placeholder",
                IsProtected = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            using var vm = _composition.CreateNoteEditorViewModel(
                existingNote: note,
                draftJournalService: NoOpDraftJournalService.Instance);

            vm.MarkLocked();
            Assert.True(vm.IsLocked);

            byte[] imageBytes = new byte[] { 99, 98, 97 };
            bool success = vm.TryPasteImageBytes(imageBytes, "confidential.png", 0, out var status);

            Assert.False(success);
            Assert.Equal("Заметка заблокирована.", status);

            Assert.Empty(vm.Attachments);
            Assert.Empty(Directory.GetFiles(_composition.AttachmentsDirectory, "*.*", SearchOption.AllDirectories));
        });
    }

    [Fact]
    public void InlineAndStandaloneParity_BothSupportIdenticalMarkdownCommandsAndModes()
    {
        RunInSta(() =>
        {
            var note1 = new Note { Id = 1, Text = "Inline text", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            var note2 = new Note { Id = 2, Text = "Standalone text", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };

            using var inlineVm = _composition.CreateNoteEditorViewModel(
                existingNote: note1,
                draftJournalService: NoOpDraftJournalService.Instance);
            inlineVm.IsInline = true;

            using var standaloneVm = _composition.CreateNoteEditorViewModel(
                existingNote: note2,
                draftJournalService: NoOpDraftJournalService.Instance);
            standaloneVm.IsInline = false;

            Assert.Equal(MarkdownViewMode.Edit, inlineVm.ViewMode);
            Assert.Equal(MarkdownViewMode.Edit, standaloneVm.ViewMode);

            inlineVm.ShowSplitModeCommand.Execute(null);
            standaloneVm.ShowSplitModeCommand.Execute(null);

            Assert.Equal(MarkdownViewMode.Split, inlineVm.ViewMode);
            Assert.Equal(MarkdownViewMode.Split, standaloneVm.ViewMode);
            Assert.True(inlineVm.IsSplitMode && standaloneVm.IsSplitMode);
            Assert.True(inlineVm.IsEditorVisible && standaloneVm.IsEditorVisible);
            Assert.True(inlineVm.IsPreviewVisible && standaloneVm.IsPreviewVisible);

            inlineVm.ShowPreviewModeCommand.Execute(null);
            standaloneVm.ShowPreviewModeCommand.Execute(null);

            Assert.Equal(MarkdownViewMode.Preview, inlineVm.ViewMode);
            Assert.Equal(MarkdownViewMode.Preview, standaloneVm.ViewMode);
            Assert.True(inlineVm.IsPreviewMode && standaloneVm.IsPreviewMode);
            Assert.False(inlineVm.IsEditorVisible || standaloneVm.IsEditorVisible);

            inlineVm.ToggleOutlineCommand.Execute(null);
            standaloneVm.ToggleOutlineCommand.Execute(null);
            Assert.True(inlineVm.IsOutlineOpen && standaloneVm.IsOutlineOpen);
        });
    }
}
