using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Services.Crypto;

namespace QuickNotes.App.Services.NoteProtection;

public sealed class NoteProtectionResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public int SkippedProtectedCount { get; init; }

    public static NoteProtectionResult Ok() => new() { Success = true };
    public static NoteProtectionResult Fail(string message) => new() { Success = false, ErrorMessage = message };
}

/// <summary>Lightweight search hit from the in-memory protected note search.</summary>
public readonly record struct ProtectedSearchHit(int NoteId, string Title);

/// <summary>
/// Coordinates per-note password protection across the note text, source context,
/// revision history, attachments and FTS index.
/// All multi-entity transformations are atomic (single DB transaction).
/// </summary>
public interface INoteProtectionService
{
    /// <summary>File system adapter used for protected attachments (allows failure injection in tests).</summary>
    IProtectionFileAdapter FileAdapter { get; }

    /// <summary>True when the note is protected by a password (regardless of unlock state).</summary>
    bool IsProtected(QuickNotesDbContext db, int noteId);

    /// <summary>
    /// Creates an encrypted revision snapshot for an unlocked protected note.
    /// Performs no-op detection against the last decrypted revision. Returns null when unchanged.
    /// </summary>
    NoteRevision? SaveProtectedRevision(QuickNotesDbContext db, Note note, string plaintext, int maxRevisions = 20);

    /// <summary>
    /// Creates an encrypted revision snapshot for an unlocked protected note with title.
    /// Performs no-op detection against the last decrypted revision. Returns null when unchanged.
    /// </summary>
    NoteRevision? SaveProtectedRevision(QuickNotesDbContext db, Note note, string title, string plaintext, int maxRevisions = 20);

    /// <summary>True when the note is currently unlocked in this process.</summary>
    bool IsUnlocked(int noteId);

    /// <summary>Gets the in-memory session (key + decrypted payload) or null.</summary>
    ProtectedNoteSession? GetSession(int noteId);

    /// <summary>
    /// Sets a password on an existing note. Encrypts text + source context, converts all
    /// revisions and attachments atomically, removes the note from FTS.
    /// On any failure the note remains fully readable and unchanged.
    /// </summary>
    NoteProtectionResult ProtectNote(QuickNotesDbContext db, int noteId, string password);

    /// <summary>
    /// Verifies the password and creates an in-memory session. Returns the decrypted payload.
    /// Wrong password throws <see cref="NoteProtectionSecurityException"/> without modifying data.
    /// </summary>
    NoteProtectedPayload UnlockNote(QuickNotesDbContext db, int noteId, string password);

    /// <summary>Locks the note in this process and wipes the derived key.</summary>
    void LockNote(int noteId);
    /// <summary>
    /// Reports whether an unlocked note still stores a legacy envelope without an explicit
    /// versioned KDF descriptor. Never rewrites anything: the descriptor is added on the next
    /// authenticated write (unlock + save) or an explicit password change/rotation.
    /// </summary>
    bool NeedsKdfMigration(QuickNotesDbContext db, int noteId);

    /// <summary>
    /// Reads the stored KDF compatibility state for a note row without deriving any key.
    /// Legacy rows are reported as needs-migration; unknown descriptors are rejected.
    /// </summary>
    KdfEnvelopeReading InspectKdfEnvelope(Note note);

    /// <summary>
    /// Changes the password. Requires the current password to be verified first.
    /// Re-encrypts the note envelope, all revisions and all attachments atomically.
    /// </summary>
    NoteProtectionResult ChangePassword(QuickNotesDbContext db, int noteId, string currentPassword, string newPassword);

    /// <summary>
    /// Removes protection. Requires the current password. Restores plaintext note,
    /// revisions and attachments atomically and re-indexes FTS.
    /// </summary>
    NoteProtectionResult RemoveProtection(QuickNotesDbContext db, int noteId, string password);

    /// <summary>
    /// Decrypts a revision body for an unlocked note. Returns null when the revision is not protected.
    /// </summary>
    string? DecryptRevisionText(QuickNotesDbContext db, int noteId, NoteRevision revision);

    /// <summary>
    /// Decrypts a revision payload (title and text) for an unlocked note. Returns null when the revision is not protected.
    /// </summary>
    NoteRevisionProtectedPayload? DecryptRevisionPayload(QuickNotesDbContext db, int noteId, NoteRevision revision);

    /// <summary>
    /// Encrypts a revision body for a protected note using the session key (unique nonce per revision).
    /// </summary>
    void EncryptRevision(QuickNotesDbContext db, int noteId, NoteRevision revision, string plaintext);

    /// <summary>
    /// Encrypts a revision title and body for a protected note using the session key (unique nonce per revision).
    /// </summary>
    void EncryptRevision(QuickNotesDbContext db, int noteId, NoteRevision revision, string title, string plaintext);

    /// <summary>
    /// Decrypts an attachment's original file name for an unlocked note.
    /// </summary>
    string DecryptAttachmentName(QuickNotesDbContext db, int noteId, NoteAttachment attachment);

    /// <summary>
    /// Decrypts an attachment's content to a temporary file (safe random name).
    /// The caller must delete the returned temp path when done.
    /// </summary>
    string DecryptAttachmentToTempFile(QuickNotesDbContext db, int noteId, NoteAttachment attachment);

    /// <summary>
    /// Encrypts an attachment's content file and original name for a protected note.
    /// Supports two-phase tracking of created files and candidate files to delete.
    /// </summary>
    void EncryptAttachment(
        QuickNotesDbContext db,
        int noteId,
        NoteAttachment attachment,
        string originalFileName,
        string sourceFilePath,
        List<string>? createdFiles = null,
        List<string>? oldFilesToDelete = null);

    /// <summary>
    /// Removes the note from the persistent FTS index (used when protection is enabled).
    /// </summary>
    void RemoveFromFts(QuickNotesDbContext db, int noteId);

    /// <summary>
    /// Re-indexes the note into FTS (used when protection is removed).
    /// </summary>
    void ReindexIntoFts(QuickNotesDbContext db, int noteId, string text);

    /// <summary>
    /// Re-indexes the note into FTS with title (used when protection is removed).
    /// </summary>
    void ReindexIntoFts(QuickNotesDbContext db, int noteId, string title, string text);

    /// <summary>
    /// Searches only unlocked protected notes in memory (never writes to persistent indexes).
    /// </summary>
    IReadOnlyList<ProtectedSearchHit> SearchUnlockedInMemory(string query);

    /// <summary>Wipes all in-memory sessions (application exit).</summary>
    void WipeAllSessions();

    // ------------------------------------------------------------------
    // Commit-path helpers used by the editor / MainViewModel when saving
    // an unlocked protected note (the note stays protected).
    // ------------------------------------------------------------------

    /// <summary>
    /// Applies the current editor text + source context to an unlocked protected note:
    /// re-encrypts the note envelope with the session key (fresh nonce) and clears the
    /// plaintext columns. Must be called inside the save transaction.
    /// </summary>
    void ApplyUnlockedEdits(QuickNotesDbContext db, Note note, string text, string? sourceProcessName, string? sourceWindowTitle, string? sourceUrl);

    /// <summary>
    /// Applies the current editor title + text + source context to an unlocked protected note:
    /// re-encrypts the note envelope with the session key (fresh nonce) and clears the
    /// plaintext columns. Must be called inside the save transaction.
    /// </summary>
    void ApplyUnlockedEdits(QuickNotesDbContext db, Note note, string title, string text, string? sourceProcessName, string? sourceWindowTitle, string? sourceUrl);

    /// <summary>
    /// Derives a key from the password and verifies it against the note envelope.
    /// Returns the derived key (caller must zero it) or throws
    /// <see cref="NoteProtectionSecurityException"/>.
    /// </summary>
    byte[] VerifyPasswordAndDeriveKey(QuickNotesDbContext db, int noteId, string password);
}
