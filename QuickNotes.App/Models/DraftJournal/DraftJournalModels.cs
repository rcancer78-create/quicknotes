using System;
using System.Collections.Generic;
using QuickNotes.App.Models;

namespace QuickNotes.App.Models.DraftJournal;

/// <summary>
/// Status of reading a draft journal file.
/// </summary>
public enum DraftJournalReadStatus
{
    Success,
    NotFound,
    ProtectedSessionRequired,
    Corrupt,
    UnsupportedVersion
}

/// <summary>
/// User choice when prompted with an uncommitted draft journal during crash recovery.
/// </summary>
public enum DraftRecoveryChoice
{
    Restore,
    Discard,
    KeepCommitted
}

/// <summary>
/// Tag metadata stored in the draft journal.
/// </summary>
public sealed class DraftJournalTagDto
{
    public int TagId { get; set; }
    public string TagName { get; set; } = string.Empty;
    public TagOrigin Origin { get; set; } = TagOrigin.Auto;
    public bool IsSuppressed { get; set; }
}

/// <summary>
/// Attachment reference stored in the draft journal.
/// </summary>
public sealed class DraftJournalAttachmentDto
{
    public int Id { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string StoredFileName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public bool IsNew { get; set; }
}

/// <summary>
/// Authenticated cryptographic envelope stored inside a protected draft journal.
/// Title and Text are encrypted inside CiphertextBase64 with the session key;
/// no password, derived key, or plaintext is stored.
/// </summary>
public sealed class DraftJournalCryptoEnvelope
{
    public int FormatVersion { get; set; } = 1;
    public string NonceBase64 { get; set; } = string.Empty;
    public string TagBase64 { get; set; } = string.Empty;
    public string CiphertextBase64 { get; set; } = string.Empty;
}

/// <summary>
/// Payload encrypted inside CiphertextBase64 for protected notes.
/// All user-visible, content-derived data is encapsulated and encrypted.
/// </summary>
public sealed class DraftJournalProtectedPayload
{
    public int Version { get; set; } = 1;
    public string? Title { get; set; }
    public string Text { get; set; } = string.Empty;
    public string? SourceProcessName { get; set; }
    public string? SourceWindowTitle { get; set; }
    public string? SourceUrl { get; set; }
    public DateTime? CapturedAt { get; set; }
    public List<DraftJournalTagDto>? Tags { get; set; }
    public List<DraftJournalAttachmentDto>? Attachments { get; set; }
}

/// <summary>
/// Versioned format of a draft journal file persisted on disk.
/// Separated from SQLite DB and revisions; never triggers sync.
/// When IsProtected is true, only routing, version and crypto metadata are stored in this envelope;
/// all user-visible and content data resides encrypted in Crypto.CiphertextBase64.
/// </summary>
public sealed class DraftJournalEnvelope
{
    public const int CurrentFormatVersion = 1;

    public int Version { get; set; } = CurrentFormatVersion;
    public string DraftId { get; set; } = string.Empty;
    public int? NoteId { get; set; }
    public Guid? SyncId { get; set; }
    public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;
    public long SequenceNumber { get; set; }

    // Source context (null when IsProtected is true)
    public string? SourceProcessName { get; set; }
    public string? SourceWindowTitle { get; set; }
    public string? SourceUrl { get; set; }
    public DateTime? CapturedAt { get; set; }

    // Protection state
    public bool IsProtected { get; set; }
    public DraftJournalCryptoEnvelope? Crypto { get; set; }

    // Plaintext fields (null when IsProtected is true)
    public string? Title { get; set; }
    public string? Text { get; set; }

    // Additional state (null when IsProtected is true)
    public List<DraftJournalTagDto>? Tags { get; set; }
    public List<DraftJournalAttachmentDto>? Attachments { get; set; }
}

/// <summary>
/// In-memory snapshot of editor state to be saved as a draft journal.
/// </summary>
public sealed class DraftJournalSnapshot
{
    public string DraftId { get; init; } = string.Empty;
    public int? NoteId { get; init; }
    public Guid? SyncId { get; init; }
    public long SequenceNumber { get; init; }
    public string? Title { get; init; }
    public string Text { get; init; } = string.Empty;
    public string? SourceProcessName { get; init; }
    public string? SourceWindowTitle { get; init; }
    public string? SourceUrl { get; init; }
    public DateTime? CapturedAt { get; init; }
    public bool IsProtected { get; init; }
    public List<DraftJournalTagDto>? Tags { get; init; }
    public List<DraftJournalAttachmentDto>? Attachments { get; init; }
}

/// <summary>
/// Result of persisting a draft journal.
/// </summary>
public sealed class DraftSaveResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public bool IsCryptoUnavailable { get; init; }
    public bool IsSuperseded { get; init; }
    public DateTime? SavedAtUtc { get; init; }
    public long SequenceNumber { get; init; }

    public static DraftSaveResult Ok(long sequenceNumber, DateTime savedAtUtc) =>
        new() { Success = true, SequenceNumber = sequenceNumber, SavedAtUtc = savedAtUtc };

    public static DraftSaveResult Superseded(long sequenceNumber) =>
        new() { Success = false, IsSuperseded = true, SequenceNumber = sequenceNumber };

    public static DraftSaveResult CryptoUnavailable(string message) =>
        new() { Success = false, IsCryptoUnavailable = true, ErrorMessage = message };

    public static DraftSaveResult Fail(string message) =>
        new() { Success = false, ErrorMessage = message };
}

/// <summary>
/// Result of reading and validating a draft journal.
/// User-facing fields do not expose local filesystem paths or technical exception details.
/// </summary>
public sealed class DraftJournalReadResult
{
    public DraftJournalReadStatus Status { get; init; }
    public DraftJournalEnvelope? Envelope { get; init; }
    public string? DecryptedTitle { get; init; }
    public string? DecryptedText { get; init; }
    public string? SourceProcessName { get; init; }
    public string? SourceWindowTitle { get; init; }
    public string? SourceUrl { get; init; }
    public DateTime? CapturedAt { get; init; }
    public List<DraftJournalTagDto>? Tags { get; init; }
    public List<DraftJournalAttachmentDto>? Attachments { get; init; }
    public string? ErrorMessage { get; init; }

    public static DraftJournalReadResult Ok(
        DraftJournalEnvelope envelope,
        string? title,
        string? text,
        string? sourceProcessName = null,
        string? sourceWindowTitle = null,
        string? sourceUrl = null,
        DateTime? capturedAt = null,
        List<DraftJournalTagDto>? tags = null,
        List<DraftJournalAttachmentDto>? attachments = null) =>
        new()
        {
            Status = DraftJournalReadStatus.Success,
            Envelope = envelope,
            DecryptedTitle = title,
            DecryptedText = text,
            SourceProcessName = sourceProcessName,
            SourceWindowTitle = sourceWindowTitle,
            SourceUrl = sourceUrl,
            CapturedAt = capturedAt,
            Tags = tags,
            Attachments = attachments
        };

    public static DraftJournalReadResult NotFound() =>
        new() { Status = DraftJournalReadStatus.NotFound };

    public static DraftJournalReadResult ProtectedSessionRequired(DraftJournalEnvelope envelope) =>
        new()
        {
            Status = DraftJournalReadStatus.ProtectedSessionRequired,
            Envelope = envelope,
            ErrorMessage = "Черновик защищён паролем. Требуется открыть заметку паролем для расшифровки черновика."
        };

    public static DraftJournalReadResult Corrupt(string error = "Файл черновика повреждён.") =>
        new()
        {
            Status = DraftJournalReadStatus.Corrupt,
            ErrorMessage = error
        };

    public static DraftJournalReadResult UnsupportedVersion(int version) =>
        new()
        {
            Status = DraftJournalReadStatus.UnsupportedVersion,
            ErrorMessage = $"Неподдерживаемая версия формата черновика: {version}."
        };
}

/// <summary>
/// Arguments passed when prompting the user to recover or discard an uncommitted draft journal.
/// Contains complete recovered draft state for full fidelity restoration.
/// </summary>
public sealed class DraftRecoveryPromptArgs
{
    public int? NoteId { get; init; }
    public string DraftId { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public DateTime JournalTimestamp { get; init; }
    public string CommittedTitle { get; init; } = string.Empty;
    public string CommittedText { get; init; } = string.Empty;
    public DateTime? CommittedTimestamp { get; init; }
    public string DraftTitle { get; init; } = string.Empty;
    public string DraftText { get; init; } = string.Empty;
    public string DiffSummary { get; init; } = string.Empty;
    public string? SourceProcessName { get; init; }
    public string? SourceWindowTitle { get; init; }
    public string? SourceUrl { get; init; }
    public DateTime? CapturedAt { get; init; }
    public List<DraftJournalTagDto>? Tags { get; init; }
    public List<DraftJournalAttachmentDto>? Attachments { get; init; }
}
