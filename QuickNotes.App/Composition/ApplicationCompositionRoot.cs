using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Data;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.Services;
using QuickNotes.App.Services.DraftJournal;
using QuickNotes.App.Services.NoteProtection;
using QuickNotes.App.Services.Reminders;
using QuickNotes.App.Services.Sync;
using QuickNotes.App.ViewModels;

namespace QuickNotes.App.Composition;

/// <summary>
/// Explicit typed composition root. No DI container, no <c>IServiceProvider</c>.
/// Public <see cref="MainViewModel"/> constructor takes these six bundles only.
/// </summary>
public sealed class ApplicationCompositionRoot : IDisposable
{
    public const int MainViewModelPublicConstructorParameterBound = 6;

    private readonly bool _ownsHotkeys;
    private readonly bool _ownsTray;
    private readonly object _teardownGate = new();
    private Task? _teardown;
    private bool _disposed;

    private ApplicationCompositionRoot(
        ApplicationCompositionOptions options,
        CoreNotesServices core,
        CaptureServices capture,
        SyncCloudServices syncCloud,
        SecurityServices security,
        TasksRemindersServices tasks,
        UiHostServices ui,
        bool ownsHotkeys,
        bool ownsTray)
    {
        Options = options;
        Core = core;
        Capture = capture;
        SyncCloud = syncCloud;
        Security = security;
        Tasks = tasks;
        Ui = ui;
        _ownsHotkeys = ownsHotkeys;
        _ownsTray = ownsTray;
    }

    public ApplicationCompositionOptions Options { get; }
    public CoreNotesServices Core { get; }
    public CaptureServices Capture { get; }
    public SyncCloudServices SyncCloud { get; }
    public SecurityServices Security { get; }
    public TasksRemindersServices Tasks { get; }
    public UiHostServices Ui { get; }

    public string ProfileDirectory => Options.ProfileDirectory;
    public string DbPath => Path.Combine(ProfileDirectory, "quicknotes.db");
    public string SettingsPath => Path.Combine(ProfileDirectory, "settings.json");
    public string TagRulesPath => Path.Combine(ProfileDirectory, "tag_rules.json");
    public string S3CredentialsPath => Path.Combine(ProfileDirectory, "s3_credentials.dat");
    public string SyncPasswordPath => Path.Combine(ProfileDirectory, "sync_password.dat");
    public string ReminderLedgerPath => Path.Combine(ProfileDirectory, ReminderLedgerStore.FileName);

    public static string ResolveProfileDirectory(IsolatedProfileAppOptions isolated)
    {
        bool isIsolated = isolated.IsIsolated && !string.IsNullOrWhiteSpace(isolated.ProfileDirectory);
        return Path.GetFullPath(isIsolated
            ? isolated.ProfileDirectory!
            : QuickNotesDbContext.GetDefaultProfileDirectory());
    }

    public static BackupService CreateProfileBackupService(string profileDirectory)
    {
        if (string.IsNullOrWhiteSpace(profileDirectory))
        {
            throw new ArgumentException("Profile directory is required.", nameof(profileDirectory));
        }

        string profile = Path.GetFullPath(profileDirectory);
        return new BackupService(
            dbPath: Path.Combine(profile, "quicknotes.db"),
            backupDirectory: Path.Combine(profile, "Backups"));
    }

    public static ApplicationCompositionRoot CreateForStartup(IsolatedProfileAppOptions isolated, BackupService? backupService = null)
    {
        string profile = ResolveProfileDirectory(isolated);
        return Create(new ApplicationCompositionOptions
        {
            ProfileDirectory = profile,
            HostKind = isolated.IsIsolated && !string.IsNullOrWhiteSpace(isolated.ProfileDirectory)
                ? CompositionHostKind.IsolatedProfile
                : CompositionHostKind.ProductionDesktop,
            RegisterGlobalHotkeys = !(isolated.IsIsolated && !string.IsNullOrWhiteSpace(isolated.ProfileDirectory)),
            TrayVisible = true,
            BackupService = backupService ?? CreateProfileBackupService(profile)
        });
    }

    public static ApplicationCompositionRoot Create(ApplicationCompositionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ProfileDirectory))
        {
            throw new ArgumentException("Profile directory is required.", nameof(options));
        }

        string profile = Path.GetFullPath(options.ProfileDirectory);
        Directory.CreateDirectory(profile);

        string dbPath = Path.Combine(profile, "quicknotes.db");
        string settingsPath = Path.Combine(profile, "settings.json");
        string tagRulesPath = Path.Combine(profile, "tag_rules.json");
        string s3Path = Path.Combine(profile, "s3_credentials.dat");
        string passwordPath = Path.Combine(profile, "sync_password.dat");
        string backupsDir = Path.Combine(profile, "Backups");
        string ledgerPath = Path.Combine(profile, ReminderLedgerStore.FileName);

        var contextFactory = options.ContextFactory ?? (() => new QuickNotesDbContext(dbPath));
        var settings = options.SettingsService ?? new SettingsService(settingsPath);
        var tagRules = new TagRuleService(tagRulesPath);
        var tagDetection = new TagDetectionService(tagRules);
        var search = new SearchService();
        var mutation = options.MutationCoordinator ?? new LocalMutationCoordinator();
        var attachments = new AttachmentStorageService(baseDirectory: profile);
        var history = new NoteHistoryService();
        var export = new NoteExportService();
        var import = new NoteImportService();
        var assembly = new NoteAssemblyService();
        var links = new NoteLinkService();
        var templates = new NoteTemplateService(contextFactory);
        var templateExpansion = new TemplateExpansionService();
        var tagMerge = new TagMergeService();
        var tagRescan = new TagRescanPreviewService(tagDetection, tagRules);
        var tagSuggestions = new TagSuggestionService();
        var resolvedProtection = options.NoteProtection ?? new NoteProtectionService(attachmentStorage: attachments);
        var resolvedJournal = options.DraftJournal ?? new DraftJournalService(
            baseDirectory: profile,
            noteProtectionService: resolvedProtection);

        var core = new CoreNotesServices(
            contextFactory,
            settings,
            search,
            tagDetection,
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
            resolvedJournal);

        bool ownsHotkeys = options.HotkeyService == null;
        GlobalHotkeyService hotkeys;
        if (options.HotkeyService != null)
        {
            hotkeys = options.HotkeyService;
        }
        else if (options.HostKind == CompositionHostKind.IsolatedProfile)
        {
            hotkeys = new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero);
        }
        else
        {
            hotkeys = new GlobalHotkeyService();
        }

        var clipboard = new ClipboardCaptureService();
        var screenCapture = new ScreenCaptureService();
        IOcrService ocr = new WindowsOcrService();
        var screenOcr = new ScreenOcrCoordinator(screenCapture, ocr);
        var capture = new CaptureServices(hotkeys, clipboard, screenCapture, ocr, screenOcr);

        var security = new SecurityServices(resolvedProtection);

        ICloudTransportFactory transportFactory = options.TransportFactory
            ?? (options.HostKind == CompositionHostKind.IsolatedProfile
                ? new UnavailableCloudTransportFactory()
                : new S3CloudTransportFactory());

        var credentials = options.CredentialsStorage ?? new DpapiS3CredentialsStorage(s3Path);
        var password = options.PasswordStorage ?? new DpapiSyncPasswordStorage(passwordPath);
        var deviceId = new DeviceIdProvider(settings);
        var clock = options.Clock ?? new SystemSyncClock();
        var conflicts = options.ConflictService
            ?? new SyncConflictService(contextFactory, deviceId, history, mutation);

        SyncCloudServices? syncCloud = null;
        ISyncScheduler scheduler;
        bool ownsScheduler;
        if (options.SyncScheduler != null)
        {
            scheduler = options.SyncScheduler;
            ownsScheduler = false;
        }
        else
        {
            var cloudSettings = settings.CurrentSettings.CloudSync ?? new SyncCloudSettings();
            scheduler = new SyncScheduler(
                () => syncCloud!.GetOrCreateEngine(),
                contextFactory,
                cloudSettings,
                credentials,
                password,
                conflicts,
                clock,
                () => settings.CurrentSettings.CloudSync ?? new SyncCloudSettings(),
                deviceIdProvider: deviceId);
            ownsScheduler = true;
        }

        syncCloud = new SyncCloudServices(
            contextFactory,
            settings,
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
            options.SyncEngine,
            options.CloudUsageService);

        var taskIndex = new TaskIndexService(contextFactory);
        ILocalToastAdapter toast = options.ToastAdapter
            ?? (options.HostKind == CompositionHostKind.IsolatedProfile
                ? new RecordingToastAdapter()
                : new WindowsToastAdapter());
        var ledger = new ReminderLedgerStore(ledgerPath);
        var reminders = options.TaskReminderScheduler ?? new TaskReminderScheduler(
            taskIndex,
            ledger,
            toast,
            clock,
            () => settings.CurrentSettings.LocalRemindersEnabled);
        var tasks = new TasksRemindersServices(taskIndex, reminders, toast, ledger);

        bool ownsTray = options.TrayIconService == null;
        var tray = options.TrayIconService ?? new TrayIconService(visible: options.TrayVisible);
        var backup = options.BackupService ?? new BackupService(dbPath: dbPath, backupDirectory: backupsDir);

        Action<Action> dispatch = options.UiDispatch ?? (action =>
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
        });
        var searchDebouncer = new SearchDebouncer(300, dispatch);
        var refreshCoalescer = new ActionCoalescer(50, dispatch);
        var ui = new UiHostServices(tray, backup, searchDebouncer, refreshCoalescer);

        return new ApplicationCompositionRoot(
            options,
            core,
            capture,
            syncCloud,
            security,
            tasks,
            ui,
            ownsHotkeys,
            ownsTray);
    }

    public MainViewModel CreateMainViewModel()
    {
        return new MainViewModel(Core, Capture, SyncCloud, Security, Tasks, Ui);
    }

    /// <summary>
    /// Single teardown owner: drain VM/schedulers under one cancellation bound, then dispose
    /// the graph only after that task has reached a terminal state (ADR-014).
    /// </summary>
    public void TeardownThenDispose(MainViewModel? main, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource();
        Task shutdown;
        lock (_teardownGate)
        {
            if (_disposed)
            {
                return;
            }

            _teardown ??= TeardownCoreAsync(main, cts.Token);
            shutdown = _teardown;
        }

        bool reachedTerminal = LifecycleShutdown.WaitOwned(shutdown, timeout, cts);
        if (reachedTerminal || shutdown.IsCompleted)
        {
            Dispose();
        }
        else
        {
            ErrorLogService.Write(
                "ApplicationCompositionRoot.Teardown",
                "composition dispose skipped while teardown is still using dependencies");
        }
    }

    private async Task TeardownCoreAsync(MainViewModel? main, CancellationToken ct)
    {
        try
        {
            if (main != null)
            {
                await main.ShutdownAsync(BoundedOperation.ShutdownDrainTimeout, ct).ConfigureAwait(false);
            }
            else
            {
                try
                {
                    Tasks.Reminders.Stop();
                }
                catch (Exception ex)
                {
                    ErrorLogService.Write("ApplicationCompositionRoot.Teardown.Reminders", ex);
                }

                try
                {
                    await SyncCloud.Scheduler.DrainAndStopAsync(BoundedOperation.ShutdownDrainTimeout, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    ErrorLogService.Write("ApplicationCompositionRoot.Teardown.Scheduler", ex);
                }
            }
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("ApplicationCompositionRoot.Teardown", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try { Tasks.Reminders.Stop(); } catch { }
        try { Tasks.Reminders.Dispose(); } catch { }
        try { SyncCloud.Dispose(); } catch { }
        try { Ui.SearchDebouncer.Dispose(); } catch { }
        try { Ui.RefreshCoalescer.Dispose(); } catch { }
        if (_ownsHotkeys)
        {
            try { Capture.Hotkeys.Dispose(); } catch { }
        }

        if (_ownsTray)
        {
            try { Ui.Tray.Dispose(); } catch { }
        }

        try { Security.Protection.WipeAllSessions(); } catch { }
    }
}
