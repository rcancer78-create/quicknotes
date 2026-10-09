using QuickNotes.App.Helpers;

namespace QuickNotes.App.Models;

public class AppSettings
{
    public bool HotkeyCtrl { get; set; } = true;
    public bool HotkeyShift { get; set; } = true;
    public bool HotkeyAlt { get; set; } = false;
    public bool HotkeyWin { get; set; } = false;
    public string HotkeyKey { get; set; } = "Space";

    // Instant Save hotkey ("Мгновенно сохранить выделенный текст") - default Ctrl+Alt+Space
    public bool InstantHotkeyCtrl { get; set; } = true;
    public bool InstantHotkeyShift { get; set; } = false;
    public bool InstantHotkeyAlt { get; set; } = true;
    public bool InstantHotkeyWin { get; set; } = false;
    public string InstantHotkeyKey { get; set; } = "Space";

    // OCR hotkey ("Снимок экрана и OCR") - default Ctrl+Shift+R (avoids Markdown numbered-list Ctrl+Shift+O)
    public bool OcrHotkeyCtrl { get; set; } = true;
    public bool OcrHotkeyShift { get; set; } = true;
    public bool OcrHotkeyAlt { get; set; } = false;
    public bool OcrHotkeyWin { get; set; } = false;
    public string OcrHotkeyKey { get; set; } = "R";

    public bool RunOnStartup { get; set; } = false;
    public bool StartMinimizedToTray { get; set; } = false;

    /// <summary>
    /// When true, the window close (X) hides to the tray. When false, close exits.
    /// Existing settings files keep the historic hide-to-tray behavior.
    /// </summary>
    public bool PreferCloseToTray { get; set; } = true;

    /// <summary>
    /// True after the user chose Hide/Exit on first window close, or saved the
    /// preference in settings. Missing new-profile files set this to false.
    /// Existing settings files keep the default (true) so the prompt is not shown again.
    /// </summary>
    public bool CloseToTrayPromptCompleted { get; set; } = true;

    public bool ShowNotificationOnSave { get; set; } = true;

    /// <summary>
    /// Local Windows reminders for open Markdown tasks with a valid @YYYY-MM-DD token.
    /// Default is on; delivery requires the in-process scheduler (no toast after exit).
    /// </summary>
    public bool LocalRemindersEnabled { get; set; } = true;

    public double NavigationPanelWidth { get; set; } = 260;

    /// <summary>
    /// Legacy alias for NavigationPanelWidth to preserve backward compatibility with existing settings files.
    /// </summary>
    public double TagPanelWidth
    {
        get => NavigationPanelWidth;
        set => NavigationPanelWidth = value;
    }

    public NavigationPanelState NavigationPanelState { get; set; } = NavigationPanelState.Normal;

    public double NoteListPanelWidth { get; set; } = WorkspaceLayoutHelper.DefaultListWidth;

    public double? WindowLeft { get; set; } = null;
    public double? WindowTop { get; set; } = null;
    public double WindowWidth { get; set; } = 1100;
    public double WindowHeight { get; set; } = 720;
    public System.Windows.WindowState WindowState { get; set; } = System.Windows.WindowState.Normal;

    public bool CompactCards { get; set; } = false;

    public bool AutoPurgeTrashEnabled { get; set; } = false;

    public int AutoPurgeTrashDays { get; set; } = 30;

    public AppTheme Theme { get; set; } = AppTheme.System;

    public NoteSortMode SortMode { get; set; } = NoteSortMode.Pinned;

    /// <summary>
    /// Persisted main-window representation (List / Board). Missing, unknown, or
    /// corrupt JSON values resolve to <see cref="WorkspaceViewMode.List"/>.
    /// </summary>
    public WorkspaceViewMode WorkspaceViewMode { get; set; } = global::QuickNotes.App.Models.WorkspaceViewMode.List;

    /// <summary>
    /// Local saved workspace views (spec §7). A view is a named filter query over the
    /// existing notes, never a container. Missing, unknown, or corrupt JSON entries
    /// are skipped or fall back to safe defaults without throwing.
    /// </summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(SavedWorkspaceViewsJsonConverter))]
    public List<SavedWorkspaceView> SavedWorkspaceViews { get; set; } = new();

    public int MaxAttachmentSizeMb { get; set; } = 25;

    /// <summary>
    /// True after the first-run tips were shown. Existing settings files without this
    /// field keep the default (true) so the wizard is not shown to current users.
    /// </summary>
    public bool HasCompletedOnboarding { get; set; } = true;
    public System.Guid DeviceId { get; set; } = System.Guid.Empty;
    public QuickNotes.App.Models.Sync.SyncCloudSettings CloudSync { get; set; } = new();

    /// <summary>Absolute path of the last successful open (plaintext) export snapshot.</summary>
    public string? LastOpenExportPath { get; set; }

    public System.DateTime? LastOpenExportUtc { get; set; }

    public string? LastOpenExportCompleteness { get; set; }

    /// <summary>
    /// Absolute path of the last QNAR whose recovery-key dry-run verification succeeded.
    /// Must not store archive password or recovery key.
    /// </summary>
    public string? LastEncryptedArchivePath { get; set; }

    public System.DateTime? LastEncryptedArchiveUtc { get; set; }
}
