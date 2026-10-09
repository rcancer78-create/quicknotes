using System;

namespace QuickNotes.App.Models;

public class NoteRevision
{
    public int Id { get; set; }

    public int NoteId { get; set; }
    public Note Note { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// Explicit title snapshot for the revision.
    /// Kept empty in plaintext DB storage for protected revisions.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Plain text for unprotected notes. For protected notes this stores the encrypted
    /// revision envelope (see protection columns) - never plaintext.
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// JSON serialized list of NoteRevisionTagSnapshot.
    /// </summary>
    public string TagsJson { get; set; } = "[]";

    // ------------------------------------------------------------------
    // Per-revision protection state (for protected notes)
    // ------------------------------------------------------------------

    /// <summary>True when this revision body is encrypted for a protected note.</summary>
    public bool IsProtected { get; set; } = false;

    public int ProtectedFormatVersion { get; set; } = 0;
    public int ProtectedKdfIterations { get; set; } = 0;

    /// <summary>
    /// Explicit versioned KDF descriptor (<c>PBKDF2-HMAC-SHA256|1|iterations|32</c>).
    /// Null/empty means legacy: readers inherit the owning note's exact historical parameters.
    /// Non-secret metadata only.
    /// </summary>
    public string? ProtectedKdfDescriptor { get; set; }

    public string? ProtectedSaltBase64 { get; set; }
    public string? ProtectedNonceBase64 { get; set; }
    public string? ProtectedTagBase64 { get; set; }
    public string? ProtectedCiphertextBase64 { get; set; }
}
