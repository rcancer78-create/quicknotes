using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;

namespace QuickNotes.App.Models;

public class Note
{
    public int Id { get; set; }

    public Guid SyncId { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Explicit title for the note.
    /// For protected notes this is kept empty in plaintext database storage and stored
    /// encrypted inside the protection envelope (NoteProtectedPayload).
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Plain text for unprotected notes. For protected notes this stores an empty/placeholder
    /// value; the encrypted payload lives in the protection envelope columns. Never store
    /// plaintext of a protected note here - it would leak into FTS/backups/exports.
    /// </summary>
    public string Text { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public bool IsPinned { get; set; } = false;

    public bool IsFavorite { get; set; } = false;

    public bool IsInbox { get; set; } = false;

    public DateTime? DeletedAt { get; set; }

    /// <summary>
    /// Source context fields are sensitive. For protected notes they are cleared here and
    /// stored encrypted inside the protection envelope payload.
    /// </summary>
    public string? SourceProcessName { get; set; }

    public string? SourceWindowTitle { get; set; }

    public string? SourceUrl { get; set; }

    public DateTime? CapturedAt { get; set; }

    [NotMapped]
    public string? SourceAppName
    {
        get => SourceProcessName;
        set => SourceProcessName = value;
    }

    [NotMapped]
    public string? Url
    {
        get => SourceUrl;
        set => SourceUrl = value;
    }

    // ------------------------------------------------------------------
    // Per-note password protection state (technical fields only)
    // ------------------------------------------------------------------

    /// <summary>True when the note is protected by its own password.</summary>
    public bool IsProtected { get; set; } = false;

    /// <summary>Crypto payload format version for the note envelope.</summary>
    public int ProtectedFormatVersion { get; set; } = 0;

    /// <summary>PBKDF2 iteration count for the note envelope.</summary>
    public int ProtectedKdfIterations { get; set; } = 0;

    /// <summary>
    /// Explicit versioned KDF descriptor for the note envelope
    /// (<c>PBKDF2-HMAC-SHA256|1|iterations|32</c>).
    /// Null/empty means a legacy envelope: readers use <see cref="ProtectedKdfIterations"/>
    /// exactly and report the note as needing migration. Non-secret metadata only.
    /// </summary>
    public string? ProtectedKdfDescriptor { get; set; }

    /// <summary>Base64 of the PBKDF2 salt (32 bytes).</summary>
    public string? ProtectedSaltBase64 { get; set; }

    /// <summary>Base64 of the AES-GCM nonce for the note text envelope (12 bytes).</summary>
    public string? ProtectedNonceBase64 { get; set; }

    /// <summary>Base64 of the AES-GCM authentication tag for the note text envelope (16 bytes).</summary>
    public string? ProtectedTagBase64 { get; set; }

    /// <summary>Base64 of the ciphertext of the JSON payload with Text + source context.</summary>
    public string? ProtectedCiphertextBase64 { get; set; }

    /// <summary>
    /// If this note was branched during a sync conflict while protected,
    /// this records the original SyncId under which the ciphertext was authenticated (AAD).
    /// Once the note is unlocked and re-saved, it is re-encrypted under its own SyncId and this is cleared.
    /// </summary>
    public Guid? ProtectedOriginalSyncId { get; set; }

    /// <summary>
    /// Stable identity of a migrated source file (normalized relative path + content SHA-256).
    /// File timestamps are not part of this identity.
    /// </summary>
    public string? ImportSourceFingerprint { get; set; }

    public string? ImportSourceRelativePath { get; set; }

    public ICollection<NoteTag> NoteTags { get; set; } = new List<NoteTag>();
    public ICollection<NoteRevision> Revisions { get; set; } = new List<NoteRevision>();
    public ICollection<NoteAttachment> Attachments { get; set; } = new List<NoteAttachment>();
}
