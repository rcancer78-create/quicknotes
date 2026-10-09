using System;
using System.Collections.Generic;
using System.IO;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.Services.DraftJournal;
using QuickNotes.App.ViewModels;

namespace QuickNotes.Tests;

/// <summary>
/// Central factory and helper for creating NoteEditorViewModel, MainViewModel,
/// and IDraftJournalService instances safely in unit and integration tests.
/// Ensures tests default to NoOpDraftJournalService or isolated temp directories,
/// preventing any reading or writing to the production user profile.
/// </summary>
public static class TestEditorFactory
{
    public static IDraftJournalService CreateNoOpDraftService() => NoOpDraftJournalService.Instance;

    public static (IDraftJournalService Service, string TempDir) CreateTempDraftService()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "qn_draft_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        return (new DraftJournalService(baseDirectory: tempDir), tempDir);
    }

    public static NoteEditorViewModel CreateEditor(
        TagDetectionService? tagDetectionService = null,
        List<Tag>? allTags = null,
        Note? existingNote = null,
        string? initialText = null,
        List<Tag>? initialAutoTags = null,
        string? sourceProcessName = null,
        string? sourceWindowTitle = null,
        string? sourceUrl = null,
        DateTime? capturedAt = null,
        MarkdownPreviewService? markdownPreviewService = null,
        List<TagDetectionMatch>? initialAutoMatches = null,
        IReadOnlyDictionary<int, TagRule>? rules = null,
        INoteHistoryService? noteHistoryService = null,
        Func<QuickNotesDbContext>? contextFactory = null,
        INoteLinkService? noteLinkService = null,
        IAttachmentStorageService? attachmentStorageService = null,
        SettingsService? settingsService = null,
        IEnumerable<Tag>? initialManualTags = null,
        QuickNotes.App.Services.NoteProtection.INoteProtectionService? noteProtectionService = null,
        string? initialTitle = null,
        IDraftJournalService? draftJournalService = null,
        string? initialDraftId = null)
    {
        string? tempRoot = null;
        if (contextFactory == null || attachmentStorageService == null || settingsService == null)
        {
            tempRoot = Path.Combine(Path.GetTempPath(), "qn_editor_factory_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
        }

        var effectiveContextFactory = contextFactory ?? (() =>
        {
            var db = new QuickNotesDbContext(Path.Combine(tempRoot!, "quicknotes.db"));
            db.Database.EnsureCreated();
            return db;
        });
        var effectiveAttachmentStorage = attachmentStorageService ?? new AttachmentStorageService(tempRoot!);
        var effectiveSettingsService = settingsService ?? new SettingsService(Path.Combine(tempRoot!, "settings.json"), _ => { });
        var effectiveProtectionService = noteProtectionService ?? new QuickNotes.App.Services.NoteProtection.NoteProtectionService(attachmentStorage: effectiveAttachmentStorage);

        return new NoteEditorViewModel(
            tagDetectionService ?? new TagDetectionService(),
            allTags ?? new List<Tag>(),
            existingNote: existingNote,
            initialText: initialText,
            initialAutoTags: initialAutoTags,
            sourceProcessName: sourceProcessName,
            sourceWindowTitle: sourceWindowTitle,
            sourceUrl: sourceUrl,
            capturedAt: capturedAt,
            markdownPreviewService: markdownPreviewService,
            initialAutoMatches: initialAutoMatches,
            rules: rules,
            noteHistoryService: noteHistoryService,
            contextFactory: effectiveContextFactory,
            noteLinkService: noteLinkService,
            attachmentStorageService: effectiveAttachmentStorage,
            settingsService: effectiveSettingsService,
            initialManualTags: initialManualTags,
            noteProtectionService: effectiveProtectionService,
            initialTitle: initialTitle,
            draftJournalService: draftJournalService ?? CreateNoOpDraftService(),
            initialDraftId: initialDraftId);
    }
}
