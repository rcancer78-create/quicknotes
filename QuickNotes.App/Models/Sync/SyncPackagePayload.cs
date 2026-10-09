using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using QuickNotes.App.Models;

namespace QuickNotes.App.Models.Sync;

public enum SyncOperationType
{
    Upsert = 1,
    Delete = 2
}

/// <summary>
/// Decrypted portable payload structure for sync package.
/// Contains complete user data, relationships, and revision identifiers.
/// Machine-specific settings are excluded.
/// </summary>
public class SyncPackagePayload
{
    public int PayloadVersion { get; set; } = 1;
    public Guid PackageId { get; set; } = Guid.NewGuid();
    public Guid SourceDeviceId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<SyncTagDto> Tags { get; set; } = new();
    public List<SyncNoteDto> Notes { get; set; } = new();
    public List<SyncTemplateDto> Templates { get; set; } = new();
    public List<SyncAttachmentDto> Attachments { get; set; } = new();
}

public class SyncTagDto
{
    public Guid SyncId { get; set; }
    public Guid RevisionId { get; set; }
    public Guid? ParentRevisionId { get; set; }
    public Guid DeviceId { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public SyncOperationType Operation { get; set; } = SyncOperationType.Upsert;

    public string Name { get; set; } = string.Empty;
    public Guid? ParentTagSyncId { get; set; }
    public List<string> Synonyms { get; set; } = new();
}

public class SyncNoteDto
{
    public Guid SyncId { get; set; }
    public Guid RevisionId { get; set; }
    public Guid? ParentRevisionId { get; set; }
    public Guid DeviceId { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public SyncOperationType Operation { get; set; } = SyncOperationType.Upsert;

    public string? Title { get; set; }
    public string Text { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public bool IsPinned { get; set; }
    public bool IsFavorite { get; set; }
    public bool IsInbox { get; set; }
    public string? SourceProcessName { get; set; }
    public string? SourceWindowTitle { get; set; }
    public string? SourceUrl { get; set; }
    public DateTime? CapturedAtUtc { get; set; }

    public List<SyncNoteTagDto> Tags { get; set; } = new();

    // ------------------------------------------------------------------
    // Per-note password protection (technical crypto envelope only; never the password)
    // ------------------------------------------------------------------

    /// <summary>True when the note is protected by its own password.</summary>
    public bool IsProtected { get; set; }

    public int ProtectedFormatVersion { get; set; }
    public int ProtectedKdfIterations { get; set; }

    /// <summary>
    /// Explicit versioned KDF descriptor of the transported note envelope
    /// (<c>PBKDF2-HMAC-SHA256|1|iterations|32</c>). Null/empty for envelopes written before
    /// descriptor versioning: the receiver keeps using <see cref="ProtectedKdfIterations"/>.
    /// Non-secret metadata; the note AEAD is unchanged.
    ///
    /// Omitted from JSON when null so a legacy-shaped payload stays byte-compatible for older readers.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProtectedKdfDescriptor { get; set; }

    public string ProtectedSaltBase64 { get; set; } = string.Empty;
    public string ProtectedNonceBase64 { get; set; } = string.Empty;
    public string ProtectedTagBase64 { get; set; } = string.Empty;
    public string ProtectedCiphertextBase64 { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ProtectedOriginalSyncId { get; set; }
}

public class SyncNoteTagDto
{
    public Guid TagSyncId { get; set; }
    public TagOrigin Origin { get; set; } = TagOrigin.Auto;
    public bool IsSuppressed { get; set; }
}

public class SyncTemplateDto
{
    public Guid SyncId { get; set; }
    public Guid RevisionId { get; set; }
    public Guid? ParentRevisionId { get; set; }
    public Guid DeviceId { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public SyncOperationType Operation { get; set; } = SyncOperationType.Upsert;

    public string Title { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public List<Guid> TagSyncIds { get; set; } = new();
}

public class SyncAttachmentDto
{
    public Guid SyncId { get; set; }
    public Guid NoteSyncId { get; set; }
    public Guid RevisionId { get; set; }
    public Guid? ParentRevisionId { get; set; }
    public Guid DeviceId { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public SyncOperationType Operation { get; set; } = SyncOperationType.Upsert;

    public string OriginalFileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }

    // ------------------------------------------------------------------
    // Per-attachment password protection (crypto envelope only; never the password)
    // ------------------------------------------------------------------
    public bool IsProtected { get; set; }
    public int ProtectedFormatVersion { get; set; }
    public int ProtectedKdfIterations { get; set; }

    /// <summary>
    /// Explicit versioned KDF descriptor of the attachment metadata envelope; null when legacy.
    /// Omitted from JSON when null so a legacy-shaped payload stays byte-compatible for older readers.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProtectedKdfDescriptor { get; set; }

    public string? ProtectedSaltBase64 { get; set; }
    public string? ProtectedNonceBase64 { get; set; }
    public string? ProtectedTagBase64 { get; set; }
    public string? ProtectedCiphertextBase64 { get; set; }
}
