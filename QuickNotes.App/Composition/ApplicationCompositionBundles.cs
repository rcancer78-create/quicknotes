using System;
using QuickNotes.App.Data;
using QuickNotes.App.Helpers;
using QuickNotes.App.Services;
using QuickNotes.App.Services.DraftJournal;
using QuickNotes.App.Services.NoteProtection;
using QuickNotes.App.Services.Reminders;
using QuickNotes.App.Services.Sync;

namespace QuickNotes.App.Composition;

/// <summary>
/// Production desktop process versus isolated CLI/smoke. Isolated never uses live
/// profile paths, live network, Windows toast, or registered global hotkeys.
/// </summary>
public enum CompositionHostKind
{
    ProductionDesktop = 0,
    IsolatedProfile = 1
}

public sealed class ApplicationCompositionOptions
{
    public required string ProfileDirectory { get; init; }
    public CompositionHostKind HostKind { get; init; }
    public bool RegisterGlobalHotkeys { get; init; }
    public bool TrayVisible { get; init; } = true;
    public Func<QuickNotesDbContext>? ContextFactory { get; init; }
    public BackupService? BackupService { get; init; }
    public SettingsService? SettingsService { get; init; }
    public ILocalToastAdapter? ToastAdapter { get; init; }
    public ICloudTransportFactory? TransportFactory { get; init; }
    public ISyncEngine? SyncEngine { get; init; }
    public ISyncScheduler? SyncScheduler { get; init; }
    public ICloudUsageService? CloudUsageService { get; init; }
    public ISyncClock? Clock { get; init; }
    public ILocalMutationCoordinator? MutationCoordinator { get; init; }
    public IS3CredentialsStorage? CredentialsStorage { get; init; }
    public ISyncPasswordStorage? PasswordStorage { get; init; }
    public ISyncConflictService? ConflictService { get; init; }
    public INoteProtectionService? NoteProtection { get; init; }
    public IDraftJournalService? DraftJournal { get; init; }
    public ITaskReminderScheduler? TaskReminderScheduler { get; init; }
    public GlobalHotkeyService? HotkeyService { get; init; }
    public TrayIconService? TrayIconService { get; init; }
    public Action<Action>? UiDispatch { get; init; }
}

public sealed class CoreNotesServices
{
    public CoreNotesServices(
        Func<QuickNotesDbContext> contextFactory,
        SettingsService settings,
        SearchService search,
        TagDetectionService tagDetection,
        TagRuleService tagRules,
        TagMergeService tagMerge,
        TagRescanPreviewService tagRescan,
        TagSuggestionService tagSuggestions,
        INoteHistoryService history,
        INoteExportService export,
        INoteImportService import,
        INoteAssemblyService assembly,
        INoteLinkService links,
        INoteTemplateService templates,
        ITemplateExpansionService templateExpansion,
        IAttachmentStorageService attachments,
        ILocalMutationCoordinator mutationCoordinator,
        IDraftJournalService draftJournal)
    {
        ContextFactory = contextFactory;
        Settings = settings;
        Search = search;
        TagDetection = tagDetection;
        TagRules = tagRules;
        TagMerge = tagMerge;
        TagRescan = tagRescan;
        TagSuggestions = tagSuggestions;
        History = history;
        Export = export;
        Import = import;
        Assembly = assembly;
        Links = links;
        Templates = templates;
        TemplateExpansion = templateExpansion;
        Attachments = attachments;
        MutationCoordinator = mutationCoordinator;
        DraftJournal = draftJournal;
    }

    public Func<QuickNotesDbContext> ContextFactory { get; }
    public SettingsService Settings { get; }
    public SearchService Search { get; }
    public TagDetectionService TagDetection { get; }
    public TagRuleService TagRules { get; }
    public TagMergeService TagMerge { get; }
    public TagRescanPreviewService TagRescan { get; }
    public TagSuggestionService TagSuggestions { get; }
    public INoteHistoryService History { get; }
    public INoteExportService Export { get; }
    public INoteImportService Import { get; }
    public INoteAssemblyService Assembly { get; }
    public INoteLinkService Links { get; }
    public INoteTemplateService Templates { get; }
    public ITemplateExpansionService TemplateExpansion { get; }
    public IAttachmentStorageService Attachments { get; }
    public ILocalMutationCoordinator MutationCoordinator { get; }
    public IDraftJournalService DraftJournal { get; }
}

public sealed class CaptureServices
{
    public CaptureServices(
        GlobalHotkeyService hotkeys,
        ClipboardCaptureService clipboard,
        IScreenCaptureService screenCapture,
        IOcrService ocr,
        IScreenOcrCoordinator screenOcr)
    {
        Hotkeys = hotkeys;
        Clipboard = clipboard;
        ScreenCapture = screenCapture;
        Ocr = ocr;
        ScreenOcr = screenOcr;
    }

    public GlobalHotkeyService Hotkeys { get; }
    public ClipboardCaptureService Clipboard { get; }
    public IScreenCaptureService ScreenCapture { get; }
    public IOcrService Ocr { get; }
    public IScreenOcrCoordinator ScreenOcr { get; }
}

public sealed class SecurityServices
{
    public SecurityServices(INoteProtectionService protection)
    {
        Protection = protection;
    }

    public INoteProtectionService Protection { get; }
}

public sealed class TasksRemindersServices
{
    public TasksRemindersServices(
        ITaskIndexService taskIndex,
        ITaskReminderScheduler reminders,
        ILocalToastAdapter toast,
        ReminderLedgerStore ledger)
    {
        TaskIndex = taskIndex;
        Reminders = reminders;
        Toast = toast;
        Ledger = ledger;
    }

    public ITaskIndexService TaskIndex { get; }
    public ITaskReminderScheduler Reminders { get; }
    public ILocalToastAdapter Toast { get; }
    public ReminderLedgerStore Ledger { get; }
}

public sealed class UiHostServices
{
    public UiHostServices(
        TrayIconService tray,
        BackupService backup,
        SearchDebouncer searchDebouncer,
        ActionCoalescer refreshCoalescer)
    {
        Tray = tray;
        Backup = backup;
        SearchDebouncer = searchDebouncer;
        RefreshCoalescer = refreshCoalescer;
    }

    public TrayIconService Tray { get; }
    public BackupService Backup { get; }
    public SearchDebouncer SearchDebouncer { get; }
    public ActionCoalescer RefreshCoalescer { get; }
}
