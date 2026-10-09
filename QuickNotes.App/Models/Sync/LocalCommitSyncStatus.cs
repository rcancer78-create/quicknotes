namespace QuickNotes.App.Models.Sync;

/// <summary>
/// Compact honest status model describing the local commit and cloud sync boundary.
/// Suitable for UI indicators, note cards, accessibility, and background coordination.
/// </summary>
public enum LocalCommitSyncStatus
{
    /// <summary>
    /// Confirmed committed to local SQLite database. Cloud sync is disabled or unconfigured.
    /// </summary>
    SavedLocally,

    /// <summary>
    /// Confirmed committed to local SQLite database; queued or pending cloud upload.
    /// </summary>
    PendingUpload,

    /// <summary>
    /// Actively synchronizing with remote cloud storage.
    /// </summary>
    Syncing,

    /// <summary>
    /// Confirmed committed to local SQLite database and fully synchronized with cloud state.
    /// </summary>
    Synchronized,

    /// <summary>
    /// Concurrent modifications require user decision / manual conflict resolution.
    /// </summary>
    Conflict,

    /// <summary>
    /// Sync failure occurred; committed local data remains safe and pending retry.
    /// </summary>
    Error
}
