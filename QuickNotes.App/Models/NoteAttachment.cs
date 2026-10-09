using System;

namespace QuickNotes.App.Models;

public class NoteAttachment
{
    public int Id { get; set; }

    public Guid SyncId { get; set; } = Guid.NewGuid();

    public int NoteId { get; set; }
    public Note? Note { get; set; }

    /// <summary>
    /// Original file name. For protected notes this stores a neutral placeholder;
    /// the real name is encrypted in the attachment protection envelope.
    /// </summary>
    public string OriginalFileName { get; set; } = string.Empty;

    /// <summary>
    /// Safe randomized stored file name. For protected attachments the managed file itself
    /// contains the encrypted blob with the content and the original name inside the envelope.
    /// </summary>
    public string StoredFileName { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public long Size { get; set; }

    public string Sha256 { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    // ------------------------------------------------------------------
    // Per-attachment protection state (for protected notes)
    // ------------------------------------------------------------------

    /// <summary>True when this attachment belongs to a protected note and its file + name are encrypted.</summary>
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
