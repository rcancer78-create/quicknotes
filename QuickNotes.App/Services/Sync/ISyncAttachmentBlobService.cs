using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Models;
using QuickNotes.App.Models.Sync;

namespace QuickNotes.App.Services.Sync;

/// <summary>
/// Result of uploading a single attachment blob to cloud.
/// </summary>
public class BlobUploadResult
{
    public string Sha256 { get; set; } = string.Empty;
    public string CloudKey { get; set; } = string.Empty;
    public bool AlreadyExisted { get; set; }
    public bool Skipped { get; set; }
    public string? SkipReason { get; set; }
    public string? ErrorMessage { get; set; }
    public bool Success => ErrorMessage == null;
}

/// <summary>
/// Result of downloading a single attachment blob from cloud.
/// </summary>
public class BlobDownloadResult
{
    public string Sha256 { get; set; } = string.Empty;
    public string CloudKey { get; set; } = string.Empty;
    public bool AlreadyLocal { get; set; }
    public bool Skipped { get; set; }
    public string? SkipReason { get; set; }
    public string? ErrorMessage { get; set; }
    public bool Success => ErrorMessage == null;
}

/// <summary>
/// Manages encrypted content-addressed attachment blob synchronization.
/// Blobs are stored in cloud as {prefix}blobs/{sha256}.bin, encrypted with AES-256-GCM.
/// Blob objects are immutable and deduplicated by SHA-256 content hash within the bucket prefix.
/// No filename, original name, local path, or plaintext content appears in the cloud key or unencrypted header.
/// </summary>
public interface ISyncAttachmentBlobService
{
    /// <summary>
    /// Uploads a local attachment file as an encrypted blob to cloud storage.
    /// Skips upload if blob with same SHA-256 already exists (idempotent).
    /// Verifies file size is within allowed limit.
    /// Does NOT read the entire file into memory if it exceeds a reasonable bound.
    /// </summary>
    Task<BlobUploadResult> UploadBlobAsync(
        NoteAttachment attachment,
        string encryptionPassword,
        CancellationToken ct = default);

    /// <summary>
    /// Downloads an encrypted attachment blob from cloud, verifies AEAD + SHA-256 + size,
    /// and atomically places it in local attachment storage.
    /// Does not overwrite or corrupt an already-valid local file.
    /// </summary>
    Task<BlobDownloadResult> DownloadBlobAsync(
        NoteAttachment attachment,
        string encryptionPassword,
        CancellationToken ct = default);

    /// <summary>
    /// Uploads all attachment blobs present in the given list that need to be synced.
    /// Returns aggregate counts. Network errors on individual blobs are recorded but do not abort others.
    /// </summary>
    Task<(int uploaded, int skipped, int alreadyExisted, int errors, List<string> diagnostics)> UploadAttachmentBlobsAsync(
        IReadOnlyList<NoteAttachment> attachments,
        string encryptionPassword,
        CancellationToken ct = default);

    /// <summary>
    /// Downloads all missing attachment blobs for the given attachment list.
    /// Returns aggregate counts. Missing or corrupt blobs are recorded but do not abort others.
    /// </summary>
    Task<(int downloaded, int skipped, int alreadyLocal, int errors, List<string> diagnostics)> DownloadMissingBlobsAsync(
        IReadOnlyList<NoteAttachment> attachments,
        string encryptionPassword,
        CancellationToken ct = default);

    /// <summary>
    /// Downloads and decrypts blobs referenced by an inbound package payload before local
    /// metadata is applied. Claims KDF work against the ambient cycle budget (if any)
    /// before PBKDF2. Missing blobs are skipped; budget exhaustion fails closed.
    /// </summary>
    Task<(int downloaded, int errors)> PrefetchUntrustedInboundBlobsBeforeApplyAsync(
        IReadOnlyList<SyncAttachmentDto> attachments,
        string encryptionPassword,
        UntrustedInboundKdfWorkBudget? inboundKdfBudget,
        CancellationToken ct = default);
}
