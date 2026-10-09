using QuickNotes.App.Services.Crypto;

namespace QuickNotes.App.Models;

/// <summary>
/// Cryptographic envelope of a protected note stored on the Note row.
/// All sensitive content (text and source context) is encrypted; only these
/// technical parameters are stored in plaintext.
/// </summary>
public class NoteProtectionEnvelope
{
    /// <summary>
    /// Crypto payload format version (see INoteCryptoService.CurrentFormatVersion).
    /// </summary>
    public int FormatVersion { get; set; } = 1;

    public int KdfIterations { get; set; }

    /// <summary>Base64 of PBKDF2 salt (32 bytes).</summary>
    public string SaltBase64 { get; set; } = string.Empty;

    /// <summary>Base64 of AES-GCM nonce (12 bytes) for the note text envelope.</summary>
    public string NonceBase64 { get; set; } = string.Empty;

    /// <summary>Base64 of AES-GCM authentication tag (16 bytes).</summary>
    public string TagBase64 { get; set; } = string.Empty;

    /// <summary>Base64 of the ciphertext of JSON payload containing Text and source context.</summary>
    public string CiphertextBase64 { get; set; } = string.Empty;
}

/// <summary>
/// Encrypted payload stored in the note text envelope.
/// Sensitive fields only; technical fields stay on the Note row.
/// </summary>
public sealed class NoteProtectedPayload
{
    public string? Title { get; set; }
    public string Text { get; set; } = string.Empty;

    public string? SourceProcessName { get; set; }
    public string? SourceWindowTitle { get; set; }
    public string? SourceUrl { get; set; }
}

/// <summary>
/// Type identifiers for the AAD binding (links ciphertext to the object type).
/// </summary>
public static class NoteProtectedObjectType
{
    public const string NoteEnvelope = "note";
    public const string Revision = "revision";
    public const string AttachmentMeta = "attachment-meta";
    public const string AttachmentContent = "attachment-content";
    public const string ConflictSnapshot = "conflict-snapshot";
    public const string DraftJournal = "draft-journal";
}

/// <summary>
/// Encrypted revision body stored in the NoteRevisions table for protected notes.
/// Sensible revision text is fully encrypted with a unique nonce per revision.
/// </summary>
public class NoteRevisionProtectionEnvelope
{
    public int FormatVersion { get; set; } = 1;
    public int KdfIterations { get; set; }
    public string SaltBase64 { get; set; } = string.Empty;
    public string NonceBase64 { get; set; } = string.Empty;
    public string TagBase64 { get; set; } = string.Empty;
    public string CiphertextBase64 { get; set; } = string.Empty;
}

/// <summary>
/// Encrypted payload stored inside a protected revision's ciphertext.
/// Contains both Title and Text, ensuring Title is never leaked to SQLite.
/// </summary>
public sealed class NoteRevisionProtectedPayload
{
    public int Version { get; set; } = 1;
    public string? Title { get; set; }
    public string Text { get; set; } = string.Empty;

    public void Deconstruct(out string? title, out string? text)
    {
        title = Title;
        text = Text;
    }
}

/// <summary>
/// Encrypted attachment metadata (original file name) stored on the attachment row.
/// Attachment file content is encrypted at rest inside the managed Attachments directory.
/// </summary>
public class NoteAttachmentProtectionEnvelope
{
    public int FormatVersion { get; set; } = 1;
    public int KdfIterations { get; set; }
    public string SaltBase64 { get; set; } = string.Empty;
    public string NonceBase64 { get; set; } = string.Empty;
    public string TagBase64 { get; set; } = string.Empty;
    public string CiphertextBase64 { get; set; } = string.Empty;
}

/// <summary>
/// In-memory unlocked state of a protected note in the current process.
/// Contains the derived key (zeroed on Lock/Remove/Exit) and the decrypted payload.
/// Never persisted.
/// </summary>
public sealed class ProtectedNoteSession
{
    public int NoteId { get; init; }
    public Guid SyncId { get; set; }
    public byte[] Key { get; }
    public NoteProtectedPayload Payload { get; }
    public DateTime UnlockedAt { get; } = DateTime.Now;

    /// <summary>
    /// Explicit KDF descriptor text of the stored envelope this session was unlocked from,
    /// or null when that envelope is legacy (no descriptor written yet).
    /// Non-secret metadata; used to decide whether a migration is still pending.
    /// </summary>
    public string? KdfDescriptorText { get; set; }

    /// <summary>Compatibility state of the stored envelope this session came from.</summary>
    public KdfEnvelopeState KdfState { get; set; } = KdfEnvelopeState.Current;

    public ProtectedNoteSession(int noteId, Guid syncId, byte[] key, NoteProtectedPayload payload)
    {
        NoteId = noteId;
        SyncId = syncId;
        Key = key;
        Payload = payload;
    }

    public void Wipe()
    {
        if (Key != null)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(Key);
        }
    }
}
