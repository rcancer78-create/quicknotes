using System;
using QuickNotes.App.Data;
using QuickNotes.App.Services;
using QuickNotes.App.Services.EncryptedArchive;
using QuickNotes.App.Services.Sync;
using QuickNotes.App.ViewModels;

namespace QuickNotes.Tests;

/// <summary>
/// Test-only composition for <see cref="ImportExportViewModel"/>. Production
/// always injects the graph's <see cref="ILocalMutationCoordinator"/>.
/// </summary>
public static class ImportExportViewModelTestComposition
{
    public static ImportExportViewModel Create(
        Func<QuickNotesDbContext> contextFactory,
        INoteExportService? exportService = null,
        INoteImportService? importService = null,
        INoteHistoryService? historyService = null,
        TagDetectionService? tagDetectionService = null,
        INoteArchiveService? archiveService = null,
        IAttachmentStorageService? attachmentStorage = null,
        SettingsService? settingsService = null,
        IEncryptedArchiveService? encryptedArchiveService = null,
        ILocalMutationCoordinator? mutationCoordinator = null)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        return new ImportExportViewModel(
            contextFactory,
            mutationCoordinator ?? new LocalMutationCoordinator(),
            exportService,
            importService,
            historyService,
            tagDetectionService,
            archiveService,
            attachmentStorage,
            settingsService,
            encryptedArchiveService);
    }
}
