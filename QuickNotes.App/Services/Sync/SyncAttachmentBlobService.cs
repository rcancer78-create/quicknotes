using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Models;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.Services.Crypto;

namespace QuickNotes.App.Services.Sync;

/// <summary>
/// Implements encrypted content-addressed attachment blob synchronization.
///
/// CLOUD KEY FORMAT:  {versionedPrefix}blobs/{sha256hex}.bin
/// No filename, original name, or local path in the key or unencrypted header.
///
/// BINARY BLOB ENVELOPE FORMAT v2 (little-endian integers via BinaryWriter):
///   [4 bytes]  Magic: 0x514E4241 = "QNBA"
///   [1 byte]   Version: 0x02
///   [1 byte]   KDF algorithm id (0x01 = PBKDF2-HMAC-SHA256)
///   [2 bytes]  KDF descriptor version (uint16)
///   [4 bytes]  KDF work factor (int32)
///   [4 bytes]  Salt length (= SaltByteSize = 32)
///   [N bytes]  Salt (PBKDF2)
///   [12 bytes] Nonce (AES-GCM)
///   [16 bytes] Auth tag (AES-GCM)
///   [8 bytes]  Plaintext size (int64, little-endian)
///   [M bytes]  Ciphertext
///
/// v1 envelopes (version 0x01, no descriptor) remain readable: they use this service's
/// configured work factor, which is the exact historical default, and are flagged for a later
/// migration rather than rewritten while decrypting.
///
/// AAD (Associated Authenticated Data) = UTF-8("QNBA|1|{sha256hex}") - identical for v1 and v2
/// — binds the envelope header to the expected content hash, preventing
///   an attacker from re-using a blob ciphertext under a different key.
///
/// SECURITY:
/// - Password → key via PBKDF2-HMAC-SHA256 with per-blob random salt + iterations
/// - Authenticated encryption via AES-256-GCM (tag binds ciphertext to AAD)
/// - Wrong password → CryptographicException → SyncSecurityException → error result
/// - Ciphertext substitution → tag mismatch → SyncSecurityException
/// - Wrong SHA-256 hash after decryption → treated as corruption
///
/// DEDUPLICATION:
/// - One blob per SHA-256 hash within the user prefix; immutable once written
/// - Existing blob is reused only after GET + AEAD decrypt + size/SHA-256 verification
/// - CloudConflictException after Put is accepted only if the winning object passes the same checks
///
/// UPLOAD ORDER:
/// - Blobs must be uploaded BEFORE the package/pointer that references them.
///   SyncEngine calls UploadAttachmentBlobsAsync before PutImmutableObjectAsync(package).
///
/// DOWNLOAD ORDER:
/// - Inbound package blobs: envelope headers are parsed and KDF work is reserved before
///   local metadata apply. Files are materialized only after that reservation succeeds.
/// - Retry path: remaining missing blobs may still download after apply; already-valid
///   local files are skipped. Failed blob download does not corrupt an existing local file.
///
/// MEMORY BOUND:
/// - Reads file in streaming chunks up to MaxBlobReadBytes; rejects files larger than allowed limit.
/// </summary>
public class SyncAttachmentBlobService : ISyncAttachmentBlobService
{
    // Blob envelope constants
    private static readonly byte[] MagicBytes = { 0x51, 0x4E, 0x42, 0x41 }; // "QNBA"
    private const byte EnvelopeVersionV1 = 0x01;
    private const byte EnvelopeVersion = 0x02;
    private const int MagicSize = 4;
    private const int VersionSize = 1;
    private const int SaltLenFieldSize = 4;
    private const int PlaintextSizeFieldSize = 8;
    private const int MinEnvelopeOverhead = MagicSize + VersionSize + SaltLenFieldSize
        + SyncCryptoService.SaltByteSize + SyncCryptoService.NonceByteSize
        + SyncCryptoService.TagByteSize + PlaintextSizeFieldSize;

    /// <summary>Descriptor header appended by v2 envelopes (algorithm id + version + iterations).</summary>
    private const int DescriptorHeaderSize = 1 + 2 + 4;

    /// <summary>
    /// Returned when a content-addressed object exists but cannot be reused
    /// (corrupt, wrong password, AEAD failure, size/hash mismatch). Immutable objects are never overwritten.
    /// </summary>
    internal const string UnusableExistingBlobMessage =
        "Существующий облачный объект вложения повреждён, зашифрован другим паролем или не соответствует ожидаемому содержимому. Объект не перезаписан; пакет не должен публиковаться до устранения.";

    private enum ExistingBlobStatus
    {
        Missing,
        Valid,
        Unusable
    }

    private readonly struct ExistingBlobInspection
    {
        public ExistingBlobStatus Status { get; init; }
        public string? ErrorMessage { get; init; }

        public static ExistingBlobInspection Missing()
            => new() { Status = ExistingBlobStatus.Missing };

        public static ExistingBlobInspection Valid()
            => new() { Status = ExistingBlobStatus.Valid };

        public static ExistingBlobInspection Unusable(string? message = null)
            => new() { Status = ExistingBlobStatus.Unusable, ErrorMessage = message ?? UnusableExistingBlobMessage };
    }

    internal const string NewBlobBlockedByQuotaMessage = CloudQuotaPolicy.NewBlobBlockedByQuotaMessage;

    private readonly ICloudObjectStoreTransport _transport;
    private readonly ISyncCryptoService _cryptoService;
    private readonly IAttachmentStorageService _attachmentStorage;
    private readonly SyncObjectKeyHelper _keyHelper;
    private readonly SyncCloudSettings _settings;
    private readonly ICloudUsageService? _usageService;

    public SyncAttachmentBlobService(
        ICloudObjectStoreTransport transport,
        ISyncCryptoService cryptoService,
        IAttachmentStorageService attachmentStorage,
        SyncObjectKeyHelper keyHelper,
        SyncCloudSettings settings,
        ICloudUsageService? usageService = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _cryptoService = cryptoService ?? throw new ArgumentNullException(nameof(cryptoService));
        _attachmentStorage = attachmentStorage ?? throw new ArgumentNullException(nameof(attachmentStorage));
        _keyHelper = keyHelper ?? throw new ArgumentNullException(nameof(keyHelper));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _usageService = usageService;
    }

    // -------------------------------------------------
    // UPLOAD
    // -------------------------------------------------

    public async Task<BlobUploadResult> UploadBlobAsync(
        NoteAttachment attachment,
        string encryptionPassword,
        CancellationToken ct = default)
    {
        if (attachment == null) throw new ArgumentNullException(nameof(attachment));
        if (string.IsNullOrEmpty(encryptionPassword))
            return BlobError(attachment.Sha256, string.Empty, "Пароль шифрования не задан.");

        // Validate SHA-256 field
        string sha256 = (attachment.Sha256 ?? string.Empty).Trim().ToLowerInvariant();
        if (sha256.Length != 64 || !IsHexString(sha256))
            return BlobError(sha256, string.Empty, $"Неверный SHA-256 вложения {attachment.SyncId}.");

        // Respect sync-attachments toggle
        if (!_settings.SyncAttachments)
            return BlobSkip(sha256, string.Empty, "Синхронизация вложений отключена в настройках.");

        string cloudKey;
        try
        {
            cloudKey = _keyHelper.GetBlobKey(sha256);
        }
        catch (Exception ex)
        {
            return BlobError(sha256, string.Empty, $"Недопустимый ключ blob: {ex.Message}");
        }

        // Check per-file size limit
        long maxBytes = _settings.MaxAttachmentSyncBytes;
        if (attachment.Size > maxBytes)
            return BlobSkip(sha256, cloudKey, $"Размер вложения {attachment.Size} байт превышает лимит {maxBytes} байт.");

        // Deduplication: an object at the content-addressed key is reusable only after
        // AEAD decrypt, size and SHA-256 verification with the current password.
        ExistingBlobInspection existing = await InspectExistingCloudBlobAsync(
            sha256, cloudKey, encryptionPassword, attachment.Size, ct).ConfigureAwait(false);
        if (existing.Status == ExistingBlobStatus.Valid)
        {
            return new BlobUploadResult
            {
                Sha256 = sha256,
                CloudKey = cloudKey,
                AlreadyExisted = true
            };
        }
        if (existing.Status == ExistingBlobStatus.Unusable)
        {
            return BlobError(sha256, cloudKey, existing.ErrorMessage ?? UnusableExistingBlobMessage);
        }

        // Get local file path
        string? localPath = null;
        if (string.IsNullOrEmpty(attachment.RelativePath) && !string.IsNullOrEmpty(attachment.StoredFileName))
        {
            string safeName = Path.GetFileName(attachment.StoredFileName);
            if (!string.IsNullOrEmpty(safeName) && safeName == attachment.StoredFileName)
                attachment.RelativePath = Path.Combine("Attachments", safeName);
        }

        if (!string.IsNullOrEmpty(attachment.RelativePath))
        {
            try
            {
                localPath = _attachmentStorage.GetFullPath(attachment.RelativePath);
            }
            catch (Exception ex)
            {
                return BlobError(sha256, cloudKey, $"Недопустимый путь вложения {attachment.SyncId}: {ex.Message}");
            }
        }

        if (string.IsNullOrEmpty(localPath) || !File.Exists(localPath))
            return BlobError(sha256, cloudKey, $"Локальный файл вложения {attachment.SyncId} не найден, blob отсутствует в облаке.");

        // Read file with size bound
        byte[] plaintext;
        try
        {
            plaintext = ReadFileBounded(localPath, maxBytes, sha256);
        }
        catch (Exception ex)
        {
            return BlobError(sha256, cloudKey, $"Ошибка чтения файла вложения {attachment.SyncId}: {ex.Message}");
        }

        // Encrypt
        byte[] envelope;
        try
        {
            envelope = EncryptBlobEnvelope(plaintext, sha256, encryptionPassword);
        }
        finally
        {
            // Zeroize plaintext from memory
            CryptographicOperations.ZeroMemory(plaintext);
        }

        if (_usageService != null)
        {
            CloudUsageResult usage;
            try
            {
                usage = await _usageService.CalculateUsageAsync(forceRefresh: true, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                ErrorLogService.Write("BlobService.QuotaCheck", ex.GetType().Name);
                return BlobError(sha256, cloudKey, NewBlobBlockedByQuotaMessage);
            }

            if (!CloudQuotaPolicy.CanUploadNewBlob(usage, envelope.Length))
                return BlobError(sha256, cloudKey, NewBlobBlockedByQuotaMessage);
        }

        // Upload (immutable). A concurrent winner is accepted only after the same verification.
        try
        {
            await _transport.PutImmutableObjectAsync(cloudKey, envelope, "application/octet-stream", ct).ConfigureAwait(false);
        }
        catch (CloudConflictException)
        {
            ExistingBlobInspection winner = await InspectExistingCloudBlobAsync(
                sha256, cloudKey, encryptionPassword, attachment.Size, ct).ConfigureAwait(false);
            if (winner.Status == ExistingBlobStatus.Valid)
            {
                return new BlobUploadResult { Sha256 = sha256, CloudKey = cloudKey, AlreadyExisted = true };
            }

            return BlobError(sha256, cloudKey, winner.ErrorMessage ?? UnusableExistingBlobMessage);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ErrorLogService.Write("BlobService.Upload", ex.GetType().Name);
            return BlobError(sha256, cloudKey, "Ошибка выгрузки blob в облачное хранилище.");
        }

        return new BlobUploadResult { Sha256 = sha256, CloudKey = cloudKey };
    }

    // -------------------------------------------------
    // DOWNLOAD
    // -------------------------------------------------

    public async Task<BlobDownloadResult> DownloadBlobAsync(
        NoteAttachment attachment,
        string encryptionPassword,
        CancellationToken ct = default)
    {
        if (attachment == null) throw new ArgumentNullException(nameof(attachment));
        if (string.IsNullOrEmpty(encryptionPassword))
            return BlobDownloadError(attachment.Sha256, string.Empty, "Пароль шифрования не задан.");

        string sha256 = (attachment.Sha256 ?? string.Empty).Trim().ToLowerInvariant();
        if (sha256.Length != 64 || !IsHexString(sha256))
            return BlobDownloadError(sha256, string.Empty, $"Неверный SHA-256 вложения {attachment.SyncId}.");

        if (!_settings.SyncAttachments)
            return BlobDownloadSkip(sha256, string.Empty, "Синхронизация вложений отключена в настройках.");

        string cloudKey;
        try
        {
            cloudKey = _keyHelper.GetBlobKey(sha256);
        }
        catch (Exception ex)
        {
            return BlobDownloadError(sha256, string.Empty, $"Недопустимый ключ blob: {ex.Message}");
        }

        // Determine local stored filename.
        // Use attachment.StoredFileName if available (already validated when attachment was stored locally).
        // Fall back to computing from SHA-256 + extension if empty (receiving device case).
        string storedFileName;
        if (!string.IsNullOrEmpty(attachment.StoredFileName))
        {
            // Safety: validate there is no path traversal in the stored filename
            storedFileName = Path.GetFileName(attachment.StoredFileName);
            if (storedFileName != attachment.StoredFileName ||
                storedFileName.Contains("..") ||
                storedFileName.Contains('/') ||
                storedFileName.Contains('\\'))
            {
                storedFileName = GetStoredFileName(sha256, attachment.OriginalFileName);
            }
        }
        else
        {
            storedFileName = GetStoredFileName(sha256, attachment.OriginalFileName);
        }

        string relativePath = Path.Combine("Attachments", storedFileName);

        if (_attachmentStorage.FileExists(relativePath))
        {
            // Verify local file hash matches to guard against local corruption
            try
            {
                string localFullPath = _attachmentStorage.GetFullPath(relativePath);
                string localHash = ComputeFileSha256(localFullPath);
                if (string.Equals(localHash, sha256, StringComparison.OrdinalIgnoreCase))
                {
                    attachment.StoredFileName = storedFileName;
                    attachment.RelativePath = Path.Combine("Attachments", storedFileName);
                    return new BlobDownloadResult { Sha256 = sha256, CloudKey = cloudKey, AlreadyLocal = true };
                }
                // Hash mismatch - local file is corrupt; proceed to re-download
            }
            catch { /* Proceed to download */ }
        }

        // Download from cloud
        StorageObjectResult? blobObj;
        try
        {
            blobObj = await _transport.GetObjectAsync(cloudKey, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return BlobDownloadError(sha256, cloudKey, $"Ошибка скачивания blob {sha256}: {ex.Message}");
        }

        if (blobObj == null || blobObj.Content == null || blobObj.Content.Length == 0)
            return BlobDownloadError(sha256, cloudKey, $"Blob {sha256} отсутствует в облачном хранилище.");

        // Check size bound (envelope can be larger than original due to encryption overhead, add generous margin)
        long maxBytes = _settings.MaxAttachmentSyncBytes;
        if (blobObj.Content.Length > maxBytes + MinEnvelopeOverhead + 1024)
            return BlobDownloadError(sha256, cloudKey, $"Размер скачанного blob превышает допустимый лимит.");

        // Decrypt and verify
        try
        {
            return await MaterializeDownloadedBlobAsync(attachment, sha256, cloudKey, storedFileName, blobObj.Content, encryptionPassword, inboundKdfBudget: null, ct)
                .ConfigureAwait(false);
        }
        catch (SyncKdfWorkBudgetExceededException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SyncSecurityException ex)
        {
            return BlobDownloadError(sha256, cloudKey, $"Ошибка расшифровки blob {sha256}: {ex.Message}");
        }
        catch (CryptographicException ex)
        {
            return BlobDownloadError(sha256, cloudKey, $"Ошибка расшифровки blob {sha256}: {ex.Message}");
        }
        catch (Exception ex)
        {
            return BlobDownloadError(sha256, cloudKey, $"Повреждение blob {sha256}: {ex.Message}");
        }
    }

    // -------------------------------------------------
    // BATCH OPERATIONS
    // -------------------------------------------------

    public async Task<(int uploaded, int skipped, int alreadyExisted, int errors, List<string> diagnostics)> UploadAttachmentBlobsAsync(
        IReadOnlyList<NoteAttachment> attachments,
        string encryptionPassword,
        CancellationToken ct = default)
    {
        int uploaded = 0, skipped = 0, alreadyExisted = 0, errors = 0;
        var diagnostics = new List<string>();

        if (!_settings.SyncAttachments)
        {
            diagnostics.Add("Синхронизация вложений отключена — blob-выгрузка пропущена.");
            skipped = attachments?.Count ?? 0;
            return (uploaded, skipped, alreadyExisted, errors, diagnostics);
        }

        if (attachments == null || attachments.Count == 0)
            return (uploaded, skipped, alreadyExisted, errors, diagnostics);

        foreach (var att in attachments)
        {
            ct.ThrowIfCancellationRequested();
            var r = await UploadBlobAsync(att, encryptionPassword, ct).ConfigureAwait(false);
            if (!r.Success)
            {
                errors++;
                diagnostics.Add($"[Upload Error] SHA-256={r.Sha256}: {r.ErrorMessage}");
            }
            else if (r.AlreadyExisted)
            {
                alreadyExisted++;
            }
            else if (r.Skipped)
            {
                skipped++;
                if (!string.IsNullOrEmpty(r.SkipReason))
                    diagnostics.Add($"[Upload Skip] SHA-256={r.Sha256}: {r.SkipReason}");
            }
            else
            {
                uploaded++;
            }
        }

        return (uploaded, skipped, alreadyExisted, errors, diagnostics);
    }

    public async Task<(int downloaded, int skipped, int alreadyLocal, int errors, List<string> diagnostics)> DownloadMissingBlobsAsync(
        IReadOnlyList<NoteAttachment> attachments,
        string encryptionPassword,
        CancellationToken ct = default)
    {
        int downloaded = 0, skipped = 0, alreadyLocal = 0, errors = 0;
        var diagnostics = new List<string>();

        if (!_settings.SyncAttachments)
        {
            diagnostics.Add("Синхронизация вложений отключена — blob-скачивание пропущено.");
            skipped = attachments?.Count ?? 0;
            return (downloaded, skipped, alreadyLocal, errors, diagnostics);
        }

        if (attachments == null || attachments.Count == 0)
            return (downloaded, skipped, alreadyLocal, errors, diagnostics);

        foreach (var att in attachments)
        {
            ct.ThrowIfCancellationRequested();
            var r = await DownloadBlobAsync(att, encryptionPassword, ct).ConfigureAwait(false);
            if (!r.Success)
            {
                errors++;
                diagnostics.Add($"[Download Error] SHA-256={r.Sha256}: {r.ErrorMessage}");
            }
            else if (r.AlreadyLocal)
            {
                alreadyLocal++;
            }
            else if (r.Skipped)
            {
                skipped++;
                if (!string.IsNullOrEmpty(r.SkipReason))
                    diagnostics.Add($"[Download Skip] SHA-256={r.Sha256}: {r.SkipReason}");
            }
            else
            {
                downloaded++;
            }
        }

        return (downloaded, skipped, alreadyLocal, errors, diagnostics);
    }

    public async Task<(int downloaded, int errors)> PrefetchUntrustedInboundBlobsBeforeApplyAsync(
        IReadOnlyList<SyncAttachmentDto> attachments,
        string encryptionPassword,
        UntrustedInboundKdfWorkBudget? inboundKdfBudget,
        CancellationToken ct = default)
    {
        if (!_settings.SyncAttachments || attachments == null || attachments.Count == 0)
        {
            return (0, 0);
        }

        var pending = new List<(NoteAttachment Attachment, string Sha256, string CloudKey, string StoredFileName, byte[] Envelope, int Iterations)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (SyncAttachmentDto dto in attachments)
        {
            ct.ThrowIfCancellationRequested();
            if (dto == null || dto.Operation == SyncOperationType.Delete)
            {
                continue;
            }

            string sha256 = (dto.Sha256 ?? string.Empty).Trim().ToLowerInvariant();
            if (sha256.Length != 64 || !IsHexString(sha256) || !seen.Add(sha256))
            {
                continue;
            }

            var attachment = new NoteAttachment
            {
                SyncId = dto.SyncId,
                OriginalFileName = dto.OriginalFileName,
                Size = dto.Size,
                Sha256 = sha256
            };

            string storedFileName = GetStoredFileName(sha256, dto.OriginalFileName);
            string relativePath = Path.Combine("Attachments", storedFileName);
            if (_attachmentStorage.FileExists(relativePath))
            {
                try
                {
                    string localHash = ComputeFileSha256(_attachmentStorage.GetFullPath(relativePath));
                    if (string.Equals(localHash, sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }
                catch
                {
                    // Re-download corrupt local file.
                }
            }

            string cloudKey;
            try
            {
                cloudKey = _keyHelper.GetBlobKey(sha256);
            }
            catch
            {
                continue;
            }

            StorageObjectResult? blobObj;
            try
            {
                blobObj = await _transport.GetObjectAsync(cloudKey, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                continue;
            }

            if (blobObj?.Content == null || blobObj.Content.Length == 0)
            {
                continue;
            }

            int iterations;
            try
            {
                iterations = ReadUntrustedKdfIterations(blobObj.Content);
            }
            catch (SyncKdfWorkBudgetExceededException)
            {
                throw;
            }
            catch
            {
                continue;
            }

            pending.Add((attachment, sha256, cloudKey, storedFileName, blobObj.Content, iterations));
        }

        long additional = 0;
        try
        {
            foreach (var item in pending)
            {
                additional = checked(additional + item.Iterations);
            }
        }
        catch (OverflowException)
        {
            throw new SyncKdfWorkBudgetExceededException(UntrustedInboundKdfWorkBudget.UserFacingMessage);
        }

        inboundKdfBudget?.ThrowIfWouldExceed(additional);

        int downloaded = 0;
        int errors = 0;
        foreach (var item in pending)
        {
            ct.ThrowIfCancellationRequested();
            BlobDownloadResult materialized = await MaterializeDownloadedBlobAsync(
                    item.Attachment,
                    item.Sha256,
                    item.CloudKey,
                    item.StoredFileName,
                    item.Envelope,
                    encryptionPassword,
                    inboundKdfBudget,
                    ct)
                .ConfigureAwait(false);
            if (!materialized.Success)
            {
                errors++;
            }
            else
            {
                downloaded++;
            }
        }

        return (downloaded, errors);
    }

    private async Task<BlobDownloadResult> MaterializeDownloadedBlobAsync(
        NoteAttachment attachment,
        string sha256,
        string cloudKey,
        string storedFileName,
        byte[] envelope,
        string encryptionPassword,
        UntrustedInboundKdfWorkBudget? inboundKdfBudget,
        CancellationToken ct)
    {
        byte[] plaintext;
        try
        {
            plaintext = DecryptBlobEnvelope(envelope, sha256, encryptionPassword, inboundKdfBudget);
        }
        catch (SyncKdfWorkBudgetExceededException)
        {
            throw;
        }
        catch (SyncSecurityException ex)
        {
            return BlobDownloadError(sha256, cloudKey, $"Ошибка расшифровки blob {sha256}: {ex.Message}");
        }
        catch (CryptographicException ex)
        {
            return BlobDownloadError(sha256, cloudKey, $"Ошибка расшифровки blob {sha256}: {ex.Message}");
        }
        catch (Exception ex)
        {
            return BlobDownloadError(sha256, cloudKey, $"Повреждение blob {sha256}: {ex.Message}");
        }

        try
        {
            VerifyDecryptedPlaintext(plaintext, sha256, attachment.Size);
        }
        catch (SyncSecurityException ex)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            return BlobDownloadError(sha256, cloudKey, ex.Message);
        }

        string attachmentsDir = _attachmentStorage.AttachmentsDirectory;
        string destFullPath = Path.Combine(attachmentsDir, storedFileName);
        string tempPath = Path.Combine(attachmentsDir, $"{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllBytesAsync(tempPath, plaintext, ct).ConfigureAwait(false);
            File.Move(tempPath, destFullPath, overwrite: true);
            attachment.StoredFileName = storedFileName;
            attachment.RelativePath = Path.Combine("Attachments", storedFileName);
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(tempPath);
            throw;
        }
        catch (Exception ex)
        {
            TryDeleteFile(tempPath);
            CryptographicOperations.ZeroMemory(plaintext);
            return BlobDownloadError(sha256, cloudKey, $"Ошибка записи blob {sha256} в локальное хранилище: {ex.Message}");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        return new BlobDownloadResult { Sha256 = sha256, CloudKey = cloudKey };
    }

    /// <summary>
    /// Reads the claimed PBKDF2 work factor from a QNBA header without deriving a key.
    /// Legacy v1 uses this service's configured iterations (the historical default).
    /// </summary>
    internal int ReadUntrustedKdfIterations(byte[] envelope)
    {
        if (envelope == null || envelope.Length < MinEnvelopeOverhead)
            throw new SyncSecurityException("Blob envelope слишком короткий или повреждён.");

        using var ms = new MemoryStream(envelope);
        using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        byte[] magic = br.ReadBytes(MagicSize);
        if (magic.Length != MagicSize ||
            magic[0] != MagicBytes[0] || magic[1] != MagicBytes[1] ||
            magic[2] != MagicBytes[2] || magic[3] != MagicBytes[3])
        {
            throw new SyncSecurityException("Неверная сигнатура blob envelope (ожидалась QNBA).");
        }

        byte version = br.ReadByte();
        if (version != EnvelopeVersionV1 && version != EnvelopeVersion)
            throw new SyncSecurityException($"Неподдерживаемая версия blob envelope ({version}).");

        if (version == EnvelopeVersionV1)
        {
            KdfDescriptor.ValidateIterations(_cryptoService.Iterations, KdfDescriptorLimits.CloudEnvelope);
            return _cryptoService.Iterations;
        }

        if (envelope.Length < MinEnvelopeOverhead + DescriptorHeaderSize)
            throw new SyncSecurityException("Blob envelope усечён (заголовок KDF).");

        byte algorithmId = br.ReadByte();
        if (algorithmId != KdfAlgorithmIds.Pbkdf2HmacSha256Id)
            throw new SyncSecurityException("Неподдерживаемый алгоритм KDF blob envelope.");

        int descriptorVersion = br.ReadUInt16();
        int blobIterations = br.ReadInt32();
        var descriptor = new KdfDescriptor(
            KdfAlgorithmIds.Pbkdf2HmacSha256,
            descriptorVersion,
            blobIterations,
            SyncCryptoService.SaltByteSize);
        try
        {
            descriptor.Validate(KdfDescriptorLimits.CloudEnvelope);
        }
        catch (KdfDescriptorValidationException ex)
        {
            throw new SyncSecurityException("Недопустимые параметры KDF blob envelope.", ex);
        }

        return blobIterations;
    }

    // -------------------------------------------------
    // CRYPTO: ENVELOPE ENCODE/DECODE
    // -------------------------------------------------

    /// <summary>
    /// Encrypts plaintext bytes into the QNBA binary envelope format.
    /// AAD = UTF-8("QNBA|1|{sha256hex}") — binds ciphertext to the expected content hash.
    /// </summary>
    internal byte[] EncryptBlobEnvelope(byte[] plaintext, string sha256Hex, string password)
    {
        byte[] aad = GetAad(sha256Hex);
        var enc = _cryptoService.EncryptPayload(plaintext, password, aad);

        // Build envelope
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        // Magic
        bw.Write(MagicBytes);
        // Version
        bw.Write(EnvelopeVersion);
        // KDF descriptor: algorithm id + descriptor version + work factor
        bw.Write(KdfAlgorithmIds.Pbkdf2HmacSha256Id);
        bw.Write((ushort)KdfDescriptorConstants.CurrentDescriptorVersion);
        bw.Write(enc.KdfIterations);
        // Salt length + salt
        bw.Write((int)enc.Salt.Length);
        bw.Write(enc.Salt);
        // Nonce (always NonceByteSize)
        bw.Write(enc.Nonce);
        // Tag (always TagByteSize)
        bw.Write(enc.Tag);
        // Plaintext size (int64 little-endian, BinaryWriter default)
        bw.Write((long)plaintext.Length);
        // Ciphertext
        bw.Write(enc.Ciphertext);

        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// Decrypts a QNBA binary envelope. Throws SyncSecurityException on auth failure or corruption.
    /// </summary>
    internal byte[] DecryptBlobEnvelope(byte[] envelope, string sha256Hex, string password, UntrustedInboundKdfWorkBudget? inboundKdfBudget = null)
    {
        if (envelope == null || envelope.Length < MinEnvelopeOverhead)
            throw new SyncSecurityException("Blob envelope слишком короткий или повреждён.");

        using var ms = new MemoryStream(envelope);
        using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        // Magic
        byte[] magic = br.ReadBytes(MagicSize);
        if (magic.Length != MagicSize ||
            magic[0] != MagicBytes[0] || magic[1] != MagicBytes[1] ||
            magic[2] != MagicBytes[2] || magic[3] != MagicBytes[3])
        {
            throw new SyncSecurityException("Неверная сигнатура blob envelope (ожидалась QNBA).");
        }

        // Version
        byte version = br.ReadByte();
        if (version != EnvelopeVersionV1 && version != EnvelopeVersion)
            throw new SyncSecurityException($"Неподдерживаемая версия blob envelope ({version}).");

        if (version == EnvelopeVersion && envelope.Length < MinEnvelopeOverhead + DescriptorHeaderSize)
            throw new SyncSecurityException("Blob envelope усечён (заголовок KDF).");

        int blobIterations = _cryptoService.Iterations;
        if (version == EnvelopeVersion)
        {
            // KDF descriptor (v2): algorithm id + descriptor version + work factor
            byte algorithmId = br.ReadByte();
            if (algorithmId != KdfAlgorithmIds.Pbkdf2HmacSha256Id)
                throw new SyncSecurityException("Неподдерживаемый алгоритм KDF blob envelope.");

            int descriptorVersion = br.ReadUInt16();
            blobIterations = br.ReadInt32();
            var descriptor = new KdfDescriptor(
                KdfAlgorithmIds.Pbkdf2HmacSha256,
                descriptorVersion,
                blobIterations,
                SyncCryptoService.SaltByteSize);
            try
            {
                descriptor.Validate(KdfDescriptorLimits.CloudEnvelope);
            }
            catch (KdfDescriptorValidationException ex)
            {
                throw new SyncSecurityException("Недопустимые параметры KDF blob envelope.", ex);
            }
        }
        // Salt
        int saltLen = br.ReadInt32();
        if (saltLen <= 0 || saltLen > 256)
            throw new SyncSecurityException($"Неверная длина соли в blob envelope ({saltLen}).");
        byte[] salt = br.ReadBytes(saltLen);
        if (salt.Length != saltLen)
            throw new SyncSecurityException("Blob envelope усечён (соль).");

        // Nonce
        byte[] nonce = br.ReadBytes(SyncCryptoService.NonceByteSize);
        if (nonce.Length != SyncCryptoService.NonceByteSize)
            throw new SyncSecurityException("Blob envelope усечён (nonce).");

        // Tag
        byte[] tag = br.ReadBytes(SyncCryptoService.TagByteSize);
        if (tag.Length != SyncCryptoService.TagByteSize)
            throw new SyncSecurityException("Blob envelope усечён (auth tag).");

        // Plaintext size
        long plaintextSize = br.ReadInt64();
        if (plaintextSize < 0 || plaintextSize > _settings.MaxAttachmentSyncBytes)
            throw new SyncSecurityException($"Недопустимый размер plaintext в blob envelope ({plaintextSize}).");

        // Ciphertext (remaining bytes)
        long remaining = envelope.Length - ms.Position;
        if (remaining < 0)
            throw new SyncSecurityException("Blob envelope усечён (ciphertext отсутствует).");

        byte[] ciphertext = br.ReadBytes((int)remaining);

        // AAD
        byte[] aad = GetAad(sha256Hex);

        try
        {
            // v1 blobs carry no descriptor: they were written with this service's configured
            // work factor, which is the exact historical default. v2 blobs use the stored factor.
            int effectiveIterations = version == EnvelopeVersion ? blobIterations : _cryptoService.Iterations;
            return _cryptoService.DecryptPayload(ciphertext, salt, nonce, tag, password, aad, effectiveIterations, inboundKdfBudget);
        }
        catch (CryptographicException ex)
        {
            throw new SyncSecurityException("Неверный пароль или нарушение целостности blob.", ex);
        }
    }

    /// <summary>
    /// Loads a content-addressed blob and accepts it for deduplication only after
    /// envelope parse, AEAD decrypt with the current password, size and SHA-256 checks.
    /// Never overwrites the immutable object.
    /// </summary>
    private async Task<ExistingBlobInspection> InspectExistingCloudBlobAsync(
        string sha256,
        string cloudKey,
        string encryptionPassword,
        long expectedPlaintextSize,
        CancellationToken ct)
    {
        StorageObjectResult? blobObj;
        try
        {
            blobObj = await _transport.GetObjectAsync(cloudKey, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("BlobService.InspectExisting", ex.GetType().Name);
            return ExistingBlobInspection.Unusable("Не удалось проверить существующий облачный объект вложения.");
        }

        if (blobObj == null || blobObj.Content == null || blobObj.Content.Length == 0)
            return ExistingBlobInspection.Missing();

        long maxBytes = _settings.MaxAttachmentSyncBytes;
        if (blobObj.Content.Length > maxBytes + MinEnvelopeOverhead + 1024)
            return ExistingBlobInspection.Unusable(UnusableExistingBlobMessage);

        byte[]? plaintext = null;
        try
        {
            plaintext = DecryptBlobEnvelope(blobObj.Content, sha256, encryptionPassword);
            VerifyDecryptedPlaintext(plaintext, sha256, expectedPlaintextSize);
            return ExistingBlobInspection.Valid();
        }
        catch (SyncSecurityException)
        {
            return ExistingBlobInspection.Unusable(UnusableExistingBlobMessage);
        }
        catch (CryptographicException)
        {
            return ExistingBlobInspection.Unusable(UnusableExistingBlobMessage);
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("BlobService.InspectExisting", ex.GetType().Name);
            return ExistingBlobInspection.Unusable(UnusableExistingBlobMessage);
        }
        finally
        {
            if (plaintext != null)
                CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static void VerifyDecryptedPlaintext(byte[] plaintext, string expectedSha256, long expectedSize)
    {
        if (expectedSize > 0 && plaintext.Length != expectedSize)
        {
            throw new SyncSecurityException(
                "Размер расшифрованного вложения не совпадает с метаданными.");
        }

        string decryptedHash = ComputeBytesSha256(plaintext);
        if (!string.Equals(decryptedHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new SyncSecurityException(
                "SHA-256 расшифрованного содержимого не совпадает с ожидаемым. Обнаружено повреждение.");
        }
    }

    // -------------------------------------------------
    // HELPERS
    // -------------------------------------------------

    private static byte[] GetAad(string sha256Hex)
        => Encoding.UTF8.GetBytes($"QNBA|1|{sha256Hex.ToLowerInvariant()}");

    private static string GetStoredFileName(string sha256Hex, string originalFileName)
    {
        // Derive extension safely from original name (mirrors AttachmentFileHelper.GetSafeStoredFileName)
        string rawExt = Path.GetExtension(originalFileName ?? string.Empty);
        var cleanChars = rawExt.TrimStart('.').Where(c => char.IsLetterOrDigit(c)).Take(12).ToArray();
        string safeExt = cleanChars.Length > 0 ? "." + new string(cleanChars).ToLowerInvariant() : string.Empty;
        return $"{sha256Hex.ToLowerInvariant()}{safeExt}";
    }

    private static string ComputeFileSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return AttachmentFileHelper.ComputeSha256(stream);
    }

    private static string ComputeBytesSha256(byte[] data)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(data)).ToLowerInvariant();
    }

    private static bool IsHexString(string s)
    {
        foreach (char c in s)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Reads a file into memory with a strict size bound.
    /// Verifies the read content has the expected SHA-256 hash.
    /// </summary>
    private static byte[] ReadFileBounded(string filePath, long maxBytes, string expectedSha256)
    {
        var fi = new FileInfo(filePath);
        if (fi.Length > maxBytes)
            throw new InvalidOperationException(
                $"Файл {fi.Length} байт превышает лимит {maxBytes} байт.");

        // Read in chunks to avoid reading entire file at once without bounds check
        using var fs = File.OpenRead(filePath);
        if (fs.Length > maxBytes)
            throw new InvalidOperationException(
                $"Файл {fs.Length} байт превышает лимит {maxBytes} байт.");

        byte[] data = new byte[fs.Length];
        int totalRead = 0;
        while (totalRead < data.Length)
        {
            int read = fs.Read(data, totalRead, data.Length - totalRead);
            if (read == 0) break;
            totalRead += read;
        }

        if (totalRead != data.Length)
            throw new IOException($"Прочитано {totalRead} байт вместо ожидаемых {data.Length}.");

        // Verify hash of read data
        string actualHash = ComputeBytesSha256(data);
        if (!string.Equals(actualHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            CryptographicOperations.ZeroMemory(data);
            throw new InvalidDataException(
                $"SHA-256 прочитанного файла ({actualHash}) не совпадает с метаданными ({expectedSha256}). Локальный файл повреждён.");
        }

        return data;
    }

    private static void TryDeleteFile(string path)
    {
        if (File.Exists(path))
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static BlobUploadResult BlobError(string sha256, string key, string msg)
        => new() { Sha256 = sha256, CloudKey = key, ErrorMessage = msg };

    private static BlobUploadResult BlobSkip(string sha256, string key, string reason)
        => new() { Sha256 = sha256, CloudKey = key, Skipped = true, SkipReason = reason };

    private static BlobDownloadResult BlobDownloadError(string sha256, string key, string msg)
        => new() { Sha256 = sha256, CloudKey = key, ErrorMessage = msg };

    private static BlobDownloadResult BlobDownloadSkip(string sha256, string key, string reason)
        => new() { Sha256 = sha256, CloudKey = key, Skipped = true, SkipReason = reason };
}
