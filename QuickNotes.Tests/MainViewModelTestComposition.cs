using System;
using System.IO;
using QuickNotes.App.Composition;
using QuickNotes.App.Data;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.Services;
using QuickNotes.App.Services.DraftJournal;
using QuickNotes.App.Services.NoteProtection;
using QuickNotes.App.Services.Reminders;
using QuickNotes.App.Services.Sync;
using QuickNotes.App.ViewModels;

namespace QuickNotes.Tests;

/// <summary>
/// Test-only composition for <see cref="MainViewModel"/>. Production code uses
/// <see cref="ApplicationCompositionRoot"/> and the six typed bundles.
/// </summary>
public static class MainViewModelTestComposition
{
    public static MainViewModel Create(
        Func<QuickNotesDbContext> contextFactory,
        TagDetectionService tagDetectionService,
        SearchService searchService,
        SettingsService settingsService,
        GlobalHotkeyService hotkeyService,
        ClipboardCaptureService clipboardCaptureService,
        TrayIconService trayIconService,
        BackupService? backupService = null,
        SearchDebouncer? searchDebouncer = null,
        TagMergeService? tagMergeService = null,
        TagRescanPreviewService? tagRescanPreviewService = null,
        TagSuggestionService? tagSuggestionService = null,
        TagRuleService? tagRuleService = null,
        INoteHistoryService? noteHistoryService = null,
        INoteExportService? noteExportService = null,
        INoteImportService? noteImportService = null,
        INoteLinkService? noteLinkService = null,
        IAttachmentStorageService? attachmentStorageService = null,
        IScreenCaptureService? screenCaptureService = null,
        IOcrService? ocrService = null,
        IScreenOcrCoordinator? screenOcrCoordinator = null,
        INoteTemplateService? noteTemplateService = null,
        ITemplateExpansionService? templateExpansionService = null,
        ActionCoalescer? refreshCoalescer = null,
        ISyncEngine? syncEngine = null,
        IS3CredentialsStorage? credentialsStorage = null,
        ISyncPasswordStorage? passwordStorage = null,
        ISyncConflictService? conflictService = null,
        ISyncScheduler? syncScheduler = null,
        ICloudUsageService? cloudUsageService = null,
        ISyncClock? syncClock = null,
        INoteProtectionService? noteProtectionService = null,
        IDraftJournalService? draftJournalService = null,
        ILocalMutationCoordinator? mutationCoordinator = null,
        INoteAssemblyService? noteAssemblyService = null,
        ITaskIndexService? taskIndexService = null,
        ITaskReminderScheduler? taskReminderScheduler = null)
    {
        var mutation = mutationCoordinator ?? new LocalMutationCoordinator();
        var attachments = attachmentStorageService ?? new AttachmentStorageService();
        var backup = backupService ?? new BackupService();
        var protection = noteProtectionService ?? new NoteProtectionService(attachmentStorage: attachments);
        var draft = draftJournalService ?? NoOpDraftJournalService.Instance;
        var tagMerge = tagMergeService ?? new TagMergeService();
        var tagRules = tagRuleService ?? new TagRuleService();
        var tagRescan = tagRescanPreviewService ?? new TagRescanPreviewService(tagDetectionService, tagRules);
        var tagSuggestions = tagSuggestionService ?? new TagSuggestionService();
        var history = noteHistoryService ?? new NoteHistoryService();
        var export = noteExportService ?? new NoteExportService();
        var import = noteImportService ?? new NoteImportService();
        var assembly = noteAssemblyService ?? new NoteAssemblyService();
        var taskIndex = taskIndexService ?? new TaskIndexService(contextFactory);
        var clock = syncClock ?? new SystemSyncClock();
        var reminders = taskReminderScheduler ?? CreateIsolatedReminders(taskIndex, settingsService, clock);
        var links = noteLinkService ?? new NoteLinkService();
        var screenCapture = screenCaptureService ?? new ScreenCaptureService();
        var ocr = ocrService ?? new WindowsOcrService();
        var screenOcr = screenOcrCoordinator ?? new ScreenOcrCoordinator(screenCapture, ocr);
        var templates = noteTemplateService ?? new NoteTemplateService(contextFactory);
        var templateExpansion = templateExpansionService ?? new TemplateExpansionService();
        var credentials = credentialsStorage ?? new EphemeralS3CredentialsStorage();
        var password = passwordStorage ?? new EphemeralSyncPasswordStorage();
        var deviceId = new DeviceIdProvider(settingsService);
        var conflicts = conflictService ?? new SyncConflictService(contextFactory, deviceId, history, mutation);
        ICloudTransportFactory transportFactory = new UnavailableCloudTransportFactory();

        SyncCloudServices? syncCloud = null;
        ISyncScheduler scheduler;
        bool ownsScheduler;
        if (syncScheduler != null)
        {
            scheduler = syncScheduler;
            ownsScheduler = false;
        }
        else
        {
            var cloudSettings = settingsService.CurrentSettings.CloudSync ?? new SyncCloudSettings();
            scheduler = new SyncScheduler(
                () => syncCloud!.GetOrCreateEngine(),
                contextFactory,
                cloudSettings,
                credentials,
                password,
                conflicts,
                clock,
                () => settingsService.CurrentSettings.CloudSync ?? new SyncCloudSettings(),
                deviceIdProvider: deviceId);
            ownsScheduler = true;
        }

        syncCloud = new SyncCloudServices(
            contextFactory,
            settingsService,
            attachments,
            mutation,
            history,
            credentials,
            password,
            deviceId,
            transportFactory,
            clock,
            conflicts,
            scheduler,
            ownsScheduler,
            syncEngine,
            cloudUsageService);

        Action<Action> dispatch = action =>
        {
            var app = System.Windows.Application.Current;
            if (app?.Dispatcher != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(action);
            }
            else
            {
                action();
            }
        };

        var core = new CoreNotesServices(
            contextFactory,
            settingsService,
            searchService,
            tagDetectionService,
            tagRules,
            tagMerge,
            tagRescan,
            tagSuggestions,
            history,
            export,
            import,
            assembly,
            links,
            templates,
            templateExpansion,
            attachments,
            mutation,
            draft);

        var capture = new CaptureServices(hotkeyService, clipboardCaptureService, screenCapture, ocr, screenOcr);
        var security = new SecurityServices(protection);
        var toast = reminders is TaskReminderScheduler
            ? (ILocalToastAdapter)new RecordingToastAdapter()
            : new UnavailableToastAdapter();
        string ledgerPath = Path.Combine(Path.GetTempPath(), "qn_test_ledger_" + Guid.NewGuid().ToString("N"), ReminderLedgerStore.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(ledgerPath)!);
        var tasks = new TasksRemindersServices(taskIndex, reminders, toast, new ReminderLedgerStore(ledgerPath));
        var ui = new UiHostServices(
        trayIconService,
        backup,
        searchDebouncer ?? new SearchDebouncer(300, dispatch),
        refreshCoalescer ?? new ActionCoalescer(50, dispatch));

        var vm = new MainViewModel(core, capture, syncCloud, security, tasks, ui);
        vm.AttachTestOwnedInfrastructure(new TestOwnedCompositionGraph(syncCloud, reminders, ui.SearchDebouncer, ui.RefreshCoalescer));
        return vm;
    }

    private sealed class TestOwnedCompositionGraph : IDisposable
    {
        private readonly SyncCloudServices _syncCloud;
        private readonly ITaskReminderScheduler _reminders;
        private readonly SearchDebouncer _searchDebouncer;
        private readonly ActionCoalescer _refreshCoalescer;
        private bool _disposed;

        public TestOwnedCompositionGraph(
            SyncCloudServices syncCloud,
            ITaskReminderScheduler reminders,
            SearchDebouncer searchDebouncer,
            ActionCoalescer refreshCoalescer)
        {
            _syncCloud = syncCloud;
            _reminders = reminders;
            _searchDebouncer = searchDebouncer;
            _refreshCoalescer = refreshCoalescer;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try { _reminders.Dispose(); } catch { }
            try { _syncCloud.Dispose(); } catch { }
            try { _searchDebouncer.Dispose(); } catch { }
            try { _refreshCoalescer.Dispose(); } catch { }
        }
    }

    private static ITaskReminderScheduler CreateIsolatedReminders(
        ITaskIndexService taskIndex,
        SettingsService settingsService,
        ISyncClock clock)
    {
        string dir = Path.Combine(Path.GetTempPath(), "qn_test_reminders_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new TaskReminderScheduler(
            taskIndex,
            new ReminderLedgerStore(Path.Combine(dir, ReminderLedgerStore.FileName)),
            new RecordingToastAdapter(),
            clock,
            () => settingsService.CurrentSettings.LocalRemindersEnabled);
    }
}

public static class SettingsViewModelTestComposition
{
    public static SettingsViewModel Create(
        SettingsService settingsService,
        GlobalHotkeyService hotkeyService,
        BackupService? backupService = null,
        IS3CredentialsStorage? credentialsStorage = null,
        ISyncPasswordStorage? passwordStorage = null,
        Action<string, string, System.Windows.MessageBoxImage>? showMessage = null,
        ISyncCloudCoordinator? coordinator = null,
        ISyncScheduler? syncScheduler = null,
        ICloudUsageService? cloudUsageService = null,
        ICloudCleanupService? cloudCleanupService = null,
        ISyncPasswordRotationService? passwordRotationService = null,
        ICloudRetentionService? cloudRetentionService = null,
        ITaskReminderScheduler? taskReminderScheduler = null,
        ICloudTransportFactory? transportFactory = null)
    {
        return new SettingsViewModel(
            settingsService,
            hotkeyService,
            backupService ?? new BackupService(),
            credentialsStorage ?? new EphemeralS3CredentialsStorage(),
            passwordStorage ?? new EphemeralSyncPasswordStorage(),
            showMessage,
            coordinator,
            syncScheduler,
            cloudUsageService,
            cloudCleanupService,
            passwordRotationService,
            cloudRetentionService,
            taskReminderScheduler,
            transportFactory);
    }
}
