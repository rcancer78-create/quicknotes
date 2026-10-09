using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QuickNotes.App.Data;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.Services.DraftJournal;
using QuickNotes.App.Services.NoteProtection;
using QuickNotes.App.Services.Reminders;
using QuickNotes.App.Services.Sync;
using QuickNotes.App.ViewModels;
using QuickNotes.App.Views;

namespace QuickNotes.Tests;

/// <summary>
/// Test-only composition helper that creates a unique temp profile and explicitly
/// supplies every path-bearing dependency needed by MainViewModel, NoteEditorViewModel,
/// MainWindow, and smoke runners.
///
/// Prevents parallel tests from racing through mutable process-global overrides
/// and ensures no production type needs test-runner detection to stay isolated.
/// </summary>
public sealed class TestProfileComposition : IDisposable
{
    private readonly List<IDisposable> _disposables = new();
    private readonly bool _ownsDirectory;
    private MainWindow? _mainWindow;
    private MainViewModel? _mainViewModel;

    public string ProfileDirectory { get; }
    public string DbPath { get; }
    public string SettingsPath { get; }
    public string TagRulesPath { get; }
    public string S3CredentialsPath { get; }
    public string SyncPasswordPath { get; }
    public string LogsDirectory { get; }
    public string AttachmentsDirectory { get; }
    public string BackupsDirectory { get; }
    public string DraftsDirectory { get; }

    public IDisposable ErrorLoggingScope { get; }

    public Func<QuickNotesDbContext> ContextFactory { get; }
    public SettingsService SettingsService { get; }
    public TagRuleService TagRuleService { get; }
    public TagDetectionService TagDetectionService { get; }
    public SearchService SearchService { get; }
    public GlobalHotkeyService HotkeyService { get; }
    public ClipboardCaptureService ClipboardCaptureService { get; }
    private TrayIconService? _trayIconService;

    public TrayIconService TrayIconService => _trayIconService ??= new TrayIconService(visible: false);
    public BackupService BackupService { get; }
    public LocalMutationCoordinator MutationCoordinator { get; }
    public AttachmentStorageService AttachmentStorageService { get; }
    public NoteProtectionService ProtectionService { get; }
    public DraftJournalService DraftJournalService { get; }
    public NoteHistoryService HistoryService { get; }
    public NoteLinkService NoteLinkService { get; }
    public NoteExportService NoteExportService { get; }
    public NoteImportService NoteImportService { get; }
    public NoteTemplateService NoteTemplateService { get; }
    public TaskIndexService TaskIndexService { get; }
    public RecordingToastAdapter ReminderToastAdapter { get; }
    public TaskReminderScheduler TaskReminderScheduler { get; }
    public DpapiS3CredentialsStorage S3CredentialsStorage { get; }
    public DpapiSyncPasswordStorage SyncPasswordStorage { get; }
    public MarkdownPreviewService MarkdownPreviewService { get; }

    public MainWindow Window => _mainWindow ??= CreateMainWindow();
    public MainViewModel MainViewModel
    {
        get
        {
            if (_mainViewModel != null)
            {
                return _mainViewModel;
            }

            if (_mainWindow?.DataContext is MainViewModel existing)
            {
                _mainViewModel = existing;
                return existing;
            }

            return CreateMainViewModel();
        }
    }

    public TestProfileComposition(string? profileDirectory = null, int seedNotes = 0, bool ownsDirectory = true)
    {
        _ownsDirectory = ownsDirectory;

        if (string.IsNullOrWhiteSpace(profileDirectory))
        {
            ProfileDirectory = Path.Combine(Path.GetTempPath(), "QuickNotes_TestProfile_" + Guid.NewGuid().ToString("N"));
        }
        else if (!Path.IsPathRooted(profileDirectory))
        {
            ProfileDirectory = Path.Combine(Path.GetTempPath(), profileDirectory + "_" + Guid.NewGuid().ToString("N"));
        }
        else
        {
            ProfileDirectory = Path.GetFullPath(profileDirectory);
        }

        Directory.CreateDirectory(ProfileDirectory);

        DbPath = Path.Combine(ProfileDirectory, "quicknotes.db");
        SettingsPath = Path.Combine(ProfileDirectory, "settings.json");
        TagRulesPath = Path.Combine(ProfileDirectory, "tag_rules.json");
        S3CredentialsPath = Path.Combine(ProfileDirectory, "s3_credentials.dat");
        SyncPasswordPath = Path.Combine(ProfileDirectory, "sync_password.dat");

        LogsDirectory = Path.Combine(ProfileDirectory, "Logs");
        AttachmentsDirectory = Path.Combine(ProfileDirectory, "Attachments");
        BackupsDirectory = Path.Combine(ProfileDirectory, "Backups");
        DraftsDirectory = Path.Combine(ProfileDirectory, "DraftJournals");

        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(AttachmentsDirectory);
        Directory.CreateDirectory(BackupsDirectory);
        Directory.CreateDirectory(DraftsDirectory);

        ErrorLoggingScope = ErrorLogService.UseScopedDirectory(LogsDirectory);

        using (var db = new QuickNotesDbContext(DbPath))
        {
            DbInitializer.Initialize(db);
        }

        ContextFactory = () => new QuickNotesDbContext(DbPath);
        SettingsService = new SettingsService(SettingsPath, _ => { });
        TagRuleService = new TagRuleService(TagRulesPath);
        TagDetectionService = new TagDetectionService(TagRuleService);
        SearchService = new SearchService();
        HotkeyService = new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero);
        ClipboardCaptureService = new ClipboardCaptureService();
        BackupService = new BackupService(dbPath: DbPath, backupDirectory: BackupsDirectory);
        MutationCoordinator = new LocalMutationCoordinator();
        AttachmentStorageService = new AttachmentStorageService(baseDirectory: ProfileDirectory);
        ProtectionService = new NoteProtectionService(
            attachmentStorage: AttachmentStorageService);
        DraftJournalService = new DraftJournalService(
            baseDirectory: ProfileDirectory,
            noteProtectionService: ProtectionService);
        HistoryService = new NoteHistoryService();
        NoteLinkService = new NoteLinkService();
        NoteExportService = new NoteExportService();
        NoteImportService = new NoteImportService();
        NoteTemplateService = new NoteTemplateService(ContextFactory);
        TaskIndexService = new TaskIndexService(ContextFactory);
        ReminderToastAdapter = new RecordingToastAdapter();
        TaskReminderScheduler = new TaskReminderScheduler(
            TaskIndexService,
            new ReminderLedgerStore(Path.Combine(ProfileDirectory, ReminderLedgerStore.FileName)),
            ReminderToastAdapter,
            new SystemSyncClock(),
            () => SettingsService.CurrentSettings.LocalRemindersEnabled);
        S3CredentialsStorage = new DpapiS3CredentialsStorage(S3CredentialsPath);
        SyncPasswordStorage = new DpapiSyncPasswordStorage(SyncPasswordPath);
        MarkdownPreviewService = new MarkdownPreviewService();

        var isolatedSettings = SettingsService.CurrentSettings;
        isolatedSettings.HasCompletedOnboarding = true;
        isolatedSettings.CloseToTrayPromptCompleted = true;
        SettingsService.SaveSettings(isolatedSettings);

        if (seedNotes > 0)
        {
            SeedNotes(seedNotes);
        }
    }

    public static TestProfileComposition CreateUnique(string? prefix = null, int seedNotes = 0)
    {
        string pfx = string.IsNullOrWhiteSpace(prefix) ? "qn_comp" : prefix;
        return new TestProfileComposition(pfx, seedNotes);
    }

    public QuickNotesDbContext CreateDbContext() => new(DbPath);

    public (Tag ProjectTag, Tag WorkTag, List<Note> Notes) SeedNotes(int seedNotes = 3)
    {
        using var db = CreateDbContext();
        var tagProject = new Tag { Name = "Проект" };
        var tagWork = new Tag { Name = "Работа" };
        db.Tags.AddRange(tagProject, tagWork);
        db.SaveChanges();

        var notes = new List<Note>();
        for (int i = 1; i <= seedNotes; i++)
        {
            var n = new Note
            {
                Title = $"Заметка #{i}: Тестирование трёх панелей",
                Text = $"Содержимое тестовой заметки #{i}.\n\nПоддерживается форматирование и ссылки.",
                IsPinned = i == 1,
                IsFavorite = i == 2,
                CreatedAt = DateTime.Now.AddHours(-i),
                UpdatedAt = DateTime.Now.AddMinutes(-i)
            };
            n.NoteTags.Add(new NoteTag { TagId = (i % 2 == 0 ? tagWork.Id : tagProject.Id), Origin = TagOrigin.Manual });
            db.Notes.Add(n);
            notes.Add(n);
        }
        db.SaveChanges();
        return (tagProject, tagWork, notes);
    }

    public MainViewModel CreateMainViewModel(
        ActionCoalescer? refreshCoalescer = null,
        SearchDebouncer? searchDebouncer = null,
        TagMergeService? tagMergeService = null,
        TagRescanPreviewService? tagRescanPreviewService = null,
        TagSuggestionService? tagSuggestionService = null,
        IScreenCaptureService? screenCaptureService = null,
        IOcrService? ocrService = null,
        IScreenOcrCoordinator? screenOcrCoordinator = null,
        ITemplateExpansionService? templateExpansionService = null,
        ISyncEngine? syncEngine = null,
        ISyncConflictService? conflictService = null,
        ISyncScheduler? syncScheduler = null,
        ICloudUsageService? cloudUsageService = null,
        ISyncClock? syncClock = null)
    {
        if (_mainViewModel != null)
        {
            return _mainViewModel;
        }

        var created = MainViewModelTestComposition.Create(
            ContextFactory,
            TagDetectionService,
            SearchService,
            SettingsService,
            HotkeyService,
            ClipboardCaptureService,
            TrayIconService,
            BackupService,
            searchDebouncer: searchDebouncer,
            tagMergeService: tagMergeService,
            tagRescanPreviewService: tagRescanPreviewService,
            tagSuggestionService: tagSuggestionService,
            tagRuleService: TagRuleService,
            noteHistoryService: HistoryService,
            noteExportService: NoteExportService,
            noteImportService: NoteImportService,
            noteLinkService: NoteLinkService,
            attachmentStorageService: AttachmentStorageService,
            screenCaptureService: screenCaptureService,
            ocrService: ocrService,
            screenOcrCoordinator: screenOcrCoordinator,
            noteTemplateService: NoteTemplateService,
            templateExpansionService: templateExpansionService,
            refreshCoalescer: refreshCoalescer,
            syncEngine: syncEngine,
            credentialsStorage: S3CredentialsStorage,
            passwordStorage: SyncPasswordStorage,
            conflictService: conflictService,
            syncScheduler: syncScheduler,
            cloudUsageService: cloudUsageService,
            syncClock: syncClock,
            noteProtectionService: ProtectionService,
            draftJournalService: DraftJournalService,
            mutationCoordinator: MutationCoordinator,
            taskIndexService: TaskIndexService,
            taskReminderScheduler: TaskReminderScheduler);

        _mainViewModel = created;
        _disposables.Add(created);
        return created;
    }

    public MainWindow CreateMainWindow(MainViewModel? vm = null)
    {
        var mainVm = vm ?? CreateMainViewModel();
        _mainViewModel = mainVm;
        var window = new MainWindow(mainVm);
        _mainWindow = window;
        _disposables.Add(new WindowCloser(window));
        return window;
    }

    public NoteEditorViewModel CreateNoteEditorViewModel(
        Note? existingNote = null,
        string? initialTitle = null,
        string? initialText = null,
        List<Tag>? initialAutoTags = null,
        string? sourceProcessName = null,
        string? sourceWindowTitle = null,
        string? sourceUrl = null,
        DateTime? capturedAt = null,
        List<Tag>? allTags = null,
        List<TagDetectionMatch>? initialAutoMatches = null,
        IEnumerable<Tag>? initialManualTags = null,
        string? initialDraftId = null,
        IDraftJournalService? draftJournalService = null)
    {
        var editor = new NoteEditorViewModel(
            TagDetectionService,
            allTags ?? new List<Tag>(),
            existingNote: existingNote,
            initialText: initialText,
            initialAutoTags: initialAutoTags,
            sourceProcessName: sourceProcessName,
            sourceWindowTitle: sourceWindowTitle,
            sourceUrl: sourceUrl,
            capturedAt: capturedAt,
            markdownPreviewService: MarkdownPreviewService,
            initialAutoMatches: initialAutoMatches,
            rules: TagRuleService.GetAllRules(),
            noteHistoryService: HistoryService,
            contextFactory: ContextFactory,
            noteLinkService: NoteLinkService,
            attachmentStorageService: AttachmentStorageService,
            settingsService: SettingsService,
            initialManualTags: initialManualTags,
            noteProtectionService: ProtectionService,
            initialTitle: initialTitle,
            draftJournalService: draftJournalService ?? DraftJournalService,
            initialDraftId: initialDraftId,
            mutationCoordinator: MutationCoordinator);

        _disposables.Add(editor);
        return editor;
    }

    public void Deconstruct(out MainWindow window, out MainViewModel vm)
    {
        window = Window;
        vm = MainViewModel;
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            try { disposable.Dispose(); } catch { }
        }
        _disposables.Clear();

        try { _trayIconService?.Dispose(); } catch { }
        try { TaskReminderScheduler.Dispose(); } catch { }
        try { HotkeyService.Dispose(); } catch { }
        try { ErrorLoggingScope.Dispose(); } catch { }

        if (_ownsDirectory)
        {
            SqliteTestUtil.TryDeleteDirectory(ProfileDirectory);
        }
    }

    private sealed class WindowCloser : IDisposable
    {
        private MainWindow? _window;
        public WindowCloser(MainWindow window) => _window = window;
        public void Dispose()
        {
            try { _window?.CloseWithoutShutdown(); } catch { }
            _window = null;
        }
    }
}
