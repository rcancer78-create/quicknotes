using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Services.NoteProtection;

namespace QuickNotes.App.Services.EncryptedArchive;

public sealed class EncryptedArchiveService : IEncryptedArchiveService
{
    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly EncryptedArchiveKdfLimits _kdfLimits;
    private readonly INoteCryptoService? _noteCryptoMustNotBeUsed;

    public EncryptedArchiveService(
        EncryptedArchiveKdfLimits? kdfLimits = null,
        INoteCryptoService? noteCryptoMustNotBeUsed = null)
    {
        _kdfLimits = kdfLimits ?? EncryptedArchiveKdfLimits.Production;
        _noteCryptoMustNotBeUsed = noteCryptoMustNotBeUsed;
    }

    /// <summary>
    /// Test hook: invoked after the sibling temp file is created and before bytes are written.
    /// </summary>
    internal Action<string>? TestBeforeTempWrite { get; set; }

    /// <summary>
    /// Test hook: invoked after Flush(true) and before atomic <see cref="File.Move"/>.
    /// </summary>
    internal Action<string>? TestBeforePublishMove { get; set; }

    /// <summary>
    /// Test hook: invoked after dry-run validation and before restore staging is created.
    /// </summary>
    internal Action<string>? TestBeforeRestoreStaging { get; set; }

    /// <summary>
    /// Test hook: invoked after restore staging is flushed and integrity-checked, before publish.
    /// </summary>
    internal Action<string>? TestAfterRestoreStagingBeforePublish { get; set; }

    /// <summary>
    /// Optional note-crypto instance that this service must never invoke (AR-S2).
    /// </summary>
    internal INoteCryptoService? ForbiddenNoteCrypto => _noteCryptoMustNotBeUsed;

    public EncryptedArchiveCreateResult Create(EncryptedArchiveCreateRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(request.ArchivePassword))
        {
            throw new EncryptedArchiveValidationException("Пароль архива не задан.");
        }

        int pbkdf2Iterations = _kdfLimits.ResolveCreateIterations(request.Pbkdf2Iterations);

        if (string.IsNullOrWhiteSpace(request.DatabasePath) || !File.Exists(request.DatabasePath))
        {
            throw new EncryptedArchiveValidationException("Файл базы данных не найден.");
        }

        string archivePath = Path.GetFullPath(request.DestinationArchivePath);
        if (!archivePath.EndsWith(EncryptedArchiveConstants.FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new EncryptedArchiveValidationException("Архив должен иметь расширение .qnar.");
        }

        if (File.Exists(archivePath))
        {
            throw new EncryptedArchiveValidationException("Файл архива уже существует.");
        }

        string? archiveDir = Path.GetDirectoryName(archivePath);
        if (string.IsNullOrWhiteSpace(archiveDir))
        {
            throw new EncryptedArchiveValidationException("Некорректный путь архива.");
        }

        Directory.CreateDirectory(archiveDir);
        string stagingDir = Path.Combine(archiveDir, ".qnar-staging-" + Guid.NewGuid().ToString("N"));
        byte[]? recoveryKey = null;
        string? formattedRecovery = null;
        try
        {
            Directory.CreateDirectory(stagingDir);
            string backupPath = CreateSqliteOnlineBackup(request.DatabasePath, stagingDir);
            SnapshotStats stats = ReadSnapshotStats(backupPath);
            List<QnapEntry> packedFiles = PackAttachmentFiles(request.AttachmentsDirectory, request.DatabasePath);

            byte[] snapshotBytes = EncryptedArchiveInMemoryLimits.ReadAllBytesIfSourceFits(backupPath);
            var snapshotEntry = EncryptedArchivePayload.Create(EncryptedArchiveConstants.SnapshotEntryPath, snapshotBytes);
            var manifestFiles = new List<PayloadManifestFileDto>
            {
                ToManifestFile(snapshotEntry)
            };
            manifestFiles.AddRange(packedFiles.Select(ToManifestFile));

            var manifest = new PayloadManifestDto
            {
                UserVersion = stats.UserVersion,
                NoteCount = stats.NoteCount,
                ProtectedNoteCount = stats.ProtectedNoteCount,
                AttachmentFileCount = packedFiles.Count,
                Files = manifestFiles
            };

            byte[] manifestBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, ManifestJson));
            EncryptedArchiveInMemoryLimits.EnsureAllocatedSizeFits(manifestBytes.LongLength, formatException: false);
            var entries = new List<QnapEntry>(2 + packedFiles.Count)
            {
                snapshotEntry
            };
            entries.AddRange(packedFiles);
            entries.Add(EncryptedArchivePayload.Create(EncryptedArchiveConstants.ManifestEntryPath, manifestBytes));

            byte[] qnap = EncryptedArchivePayload.Write(entries);
            recoveryKey = RandomNumberGenerator.GetBytes(EncryptedArchiveConstants.KeyByteSize);
            Guid archiveId = Guid.NewGuid();
            byte[] sealedArchive = SealPayload(
                qnap,
                request.ArchivePassword,
                recoveryKey,
                pbkdf2Iterations,
                archiveId,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            PublishNewArchive(archivePath, sealedArchive, cancellationToken);
            formattedRecovery = RecoveryKeyEncoding.Format(recoveryKey);

            return new EncryptedArchiveCreateResult
            {
                ArchivePath = archivePath,
                ArchiveId = archiveId,
                RecoveryKeyFormatted = formattedRecovery,
                Pbkdf2Iterations = pbkdf2Iterations,
                SnapshotUserVersion = stats.UserVersion,
                NoteCount = stats.NoteCount,
                ProtectedNoteCount = stats.ProtectedNoteCount,
                AttachmentFileCount = packedFiles.Count
            };
        }
        finally
        {
            if (recoveryKey != null)
            {
                CryptographicOperations.ZeroMemory(recoveryKey);
            }

            TryDeleteDirectory(stagingDir);
        }
    }

    public EncryptedArchiveDryRunResult DryRun(EncryptedArchiveDryRunRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.ArchivePath) || !File.Exists(request.ArchivePath))
        {
            throw new EncryptedArchiveFormatException("Файл не является зашифрованным архивом QuickNotes.");
        }

        byte[] fileBytes = EncryptedArchiveInMemoryLimits.ReadAllBytesIfArchiveFits(request.ArchivePath);
        return DryRunBytes(fileBytes, request.ArchivePassword, request.RecoveryKeyFormatted, request.RestoreDestinationDirectory);
    }

    internal EncryptedArchiveDryRunResult DryRunBytes(
        byte[] fileBytes,
        string? archivePassword,
        string? recoveryKeyFormatted,
        string? restoreDestinationDirectory)
    {
        EncryptedArchiveFramedFile framed = EncryptedArchiveFraming.ReadBounded(fileBytes, _kdfLimits);
        byte[] dek = UnwrapDek(framed, archivePassword, recoveryKeyFormatted);
        byte[]? plaintext = null;
        try
        {
            byte[] payloadAad = EncryptedArchiveCrypto.BuildPayloadAad(
                framed.CanonicalHeaderText,
                framed.Header.PayloadContentType,
                framed.PayloadCiphertext.Length);
            plaintext = EncryptedArchiveCrypto.DecryptPayload(
                dek,
                framed.PayloadCiphertext,
                framed.PayloadNonce,
                framed.PayloadTag,
                payloadAad);

            EncryptedArchivePathRules.EnsureRestoreDestinationAllowed(restoreDestinationDirectory);

            List<QnapEntry> entries = EncryptedArchivePayload.ReadBounded(plaintext);
            ValidatePayloadShape(entries, restoreDestinationDirectory);
            PayloadManifestDto manifest = ParseAndValidateManifest(entries);

            QnapEntry snapshot = entries.First(e => e.Path == EncryptedArchiveConstants.SnapshotEntryPath);
            return new EncryptedArchiveDryRunResult
            {
                ArchiveId = framed.Header.ArchiveId,
                FormatVersion = framed.Header.FormatVersion,
                CreatedAtUtc = DateTimeOffset.FromUnixTimeSeconds(framed.Header.CreatedAtUnixUtc).UtcDateTime,
                SnapshotUserVersion = manifest.UserVersion,
                NoteCount = manifest.NoteCount,
                ProtectedNoteCount = manifest.ProtectedNoteCount,
                AttachmentFileCount = manifest.AttachmentFileCount,
                PayloadEntryCount = entries.Count,
                EntryPaths = entries.Select(e => e.Path).ToArray(),
                SnapshotSqliteBytes = snapshot.Content,
                Entries = entries
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
            if (plaintext != null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    public EncryptedArchiveRotateRecoveryResult RotateRecovery(EncryptedArchiveRotateRecoveryRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        cancellationToken.ThrowIfCancellationRequested();

        bool hasPassword = !string.IsNullOrEmpty(request.ArchivePassword);
        bool hasCurrentRecovery = !string.IsNullOrWhiteSpace(request.CurrentRecoveryKeyFormatted);
        if (hasPassword == hasCurrentRecovery)
        {
            throw new EncryptedArchiveValidationException("Нужен ровно один секрет: пароль архива или текущий recovery key.");
        }

        if (string.IsNullOrWhiteSpace(request.ArchivePath) || !File.Exists(request.ArchivePath))
        {
            throw new EncryptedArchiveFormatException("Файл не является зашифрованным архивом QuickNotes.");
        }

        string archivePath = Path.GetFullPath(request.ArchivePath);
        if (!archivePath.EndsWith(EncryptedArchiveConstants.FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new EncryptedArchiveValidationException("Архив должен иметь расширение .qnar.");
        }

        byte[] originalBytes = EncryptedArchiveInMemoryLimits.ReadAllBytesIfArchiveFits(archivePath);
        EncryptedArchiveFramedFile framed = EncryptedArchiveFraming.ReadBounded(originalBytes, _kdfLimits);
        byte[] dek = UnwrapDek(framed, request.ArchivePassword, request.CurrentRecoveryKeyFormatted);
        byte[]? newRecoveryMaterial = null;
        string? formattedNew = null;
        bool published = false;
        try
        {
            newRecoveryMaterial = RandomNumberGenerator.GetBytes(EncryptedArchiveConstants.KeyByteSize);
            byte[] recoverySalt = RandomNumberGenerator.GetBytes(EncryptedArchiveConstants.SaltByteSize);
            byte[] recoveryKek = EncryptedArchiveCrypto.DeriveRecoveryKek(newRecoveryMaterial, recoverySalt);
            byte[] recoveryWrap;
            try
            {
                byte[] aad = EncryptedArchiveCrypto.BuildWrapAad(
                    framed.CanonicalHeaderText,
                    EncryptedArchiveConstants.WrapSlotRecovery);
                byte[] ct = EncryptedArchiveCrypto.WrapDek(recoveryKek, dek, aad, out byte[] nonce, out byte[] tag);
                recoveryWrap = EncryptedArchiveCrypto.EncodeWrapSlot(recoverySalt, nonce, ct, tag);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(recoveryKek);
            }

            if (recoveryWrap.AsSpan().SequenceEqual(framed.RecoveryWrap))
            {
                throw new EncryptedArchiveValidationException("Не удалось выпустить новый recovery wrap.");
            }

            var rotated = new EncryptedArchiveFramedFile
            {
                Header = framed.Header,
                CanonicalHeaderText = framed.CanonicalHeaderText,
                PasswordWrap = framed.PasswordWrap,
                RecoveryWrap = recoveryWrap,
                PayloadNonce = framed.PayloadNonce,
                PayloadCiphertext = framed.PayloadCiphertext,
                PayloadTag = framed.PayloadTag
            };

            if (!rotated.PasswordWrap.AsSpan().SequenceEqual(framed.PasswordWrap)
                || !rotated.PayloadNonce.AsSpan().SequenceEqual(framed.PayloadNonce)
                || !rotated.PayloadCiphertext.AsSpan().SequenceEqual(framed.PayloadCiphertext)
                || !rotated.PayloadTag.AsSpan().SequenceEqual(framed.PayloadTag)
                || !string.Equals(rotated.CanonicalHeaderText, framed.CanonicalHeaderText, StringComparison.Ordinal))
            {
                throw new EncryptedArchiveValidationException("Ротация не должна менять парольный wrap или нагрузку.");
            }

            byte[] rotatedBytes = EncryptedArchiveFraming.Write(rotated);
            formattedNew = RecoveryKeyEncoding.Format(newRecoveryMaterial);

            _ = DryRunBytes(rotatedBytes, archivePassword: null, formattedNew, restoreDestinationDirectory: null);

            if (hasCurrentRecovery)
            {
                AssertOldRecoveryRejected(rotatedBytes, request.CurrentRecoveryKeyFormatted!);
            }
            else
            {
                _ = DryRunBytes(rotatedBytes, request.ArchivePassword, recoveryKeyFormatted: null, restoreDestinationDirectory: null);
            }

            PublishReplacingExistingArchive(archivePath, rotatedBytes, cancellationToken);
            published = true;

            return new EncryptedArchiveRotateRecoveryResult
            {
                ArchivePath = archivePath,
                ArchiveId = framed.Header.ArchiveId,
                RecoveryKeyFormatted = formattedNew
            };
        }
        catch
        {
            if (!published)
            {
                RestoreOriginalArchiveBytes(archivePath, originalBytes);
            }

            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
            if (newRecoveryMaterial != null)
            {
                CryptographicOperations.ZeroMemory(newRecoveryMaterial);
            }
        }
    }

    private void AssertOldRecoveryRejected(byte[] rotatedBytes, string oldRecoveryKeyFormatted)
    {
        try
        {
            _ = DryRunBytes(rotatedBytes, archivePassword: null, oldRecoveryKeyFormatted, restoreDestinationDirectory: null);
        }
        catch (EncryptedArchiveSecurityException)
        {
            return;
        }

        throw new EncryptedArchiveValidationException("Новый recovery wrap не отозвал прежний ключ.");
    }

    public EncryptedArchiveRestoreResult Restore(EncryptedArchiveRestoreRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.DestinationDirectory))
        {
            throw new EncryptedArchiveValidationException("Каталог восстановления не задан.");
        }

        string destFull = Path.GetFullPath(request.DestinationDirectory);
        string? parent = Path.GetDirectoryName(destFull);
        if (string.IsNullOrWhiteSpace(parent))
        {
            throw new EncryptedArchiveValidationException("Некорректный путь восстановления.");
        }

        string stagingDir = Path.Combine(
            parent,
            "." + Path.GetFileName(destFull) + "." + Guid.NewGuid().ToString("N") + ".restore-staging");
        EncryptedArchivePathRules.ThrowIfExistingAncestorChainHasReparse(destFull);
        EncryptedArchivePathRules.ThrowIfExistingAncestorChainHasReparse(stagingDir);

        EncryptedArchiveDryRunResult dry = DryRun(new EncryptedArchiveDryRunRequest
        {
            ArchivePath = request.ArchivePath,
            ArchivePassword = request.ArchivePassword,
            RecoveryKeyFormatted = request.RecoveryKeyFormatted,
            RestoreDestinationDirectory = destFull
        });

        EncryptedArchivePathRules.ThrowIfExistingAncestorChainHasReparse(destFull);
        EncryptedArchivePathRules.ThrowIfExistingAncestorChainHasReparse(stagingDir);
        Directory.CreateDirectory(parent);
        EncryptedArchivePathRules.ThrowIfExistingAncestorChainHasReparse(destFull);
        EncryptedArchivePathRules.ThrowIfExistingAncestorChainHasReparse(stagingDir);

        TestBeforeRestoreStaging?.Invoke(destFull);

        string? emptyAside = null;
        try
        {
            Directory.CreateDirectory(stagingDir);
            EncryptedArchivePathRules.ThrowIfReparse(stagingDir, EncryptedArchivePathRules.RestoreReparseMessage);
            EncryptedArchivePathRules.ThrowIfExistingAncestorChainHasReparse(stagingDir);
            WriteRestoreTree(stagingDir, dry.Entries);
            string restoredDb = Path.Combine(stagingDir, EncryptedArchiveConstants.RestoredDatabaseFileName);
            EnsureSqliteIntegrity(restoredDb);
            ApplySchemaOnRestoredDatabase(restoredDb);
            ReleaseSqliteHandles();
            TestAfterRestoreStagingBeforePublish?.Invoke(stagingDir);
            cancellationToken.ThrowIfCancellationRequested();
            PublishRestoredDirectory(stagingDir, destFull, ref emptyAside);
            stagingDir = string.Empty;

            string publishedDb = Path.Combine(destFull, EncryptedArchiveConstants.RestoredDatabaseFileName);
            return new EncryptedArchiveRestoreResult
            {
                DestinationDirectory = destFull,
                DatabasePath = publishedDb,
                AttachmentsDirectory = Path.Combine(destFull, EncryptedArchiveConstants.RestoredAttachmentsDirectoryName),
                ArchiveId = dry.ArchiveId,
                FormatVersion = dry.FormatVersion,
                CreatedAtUtc = dry.CreatedAtUtc,
                SnapshotUserVersion = dry.SnapshotUserVersion,
                NoteCount = dry.NoteCount,
                ProtectedNoteCount = dry.ProtectedNoteCount,
                AttachmentFileCount = dry.AttachmentFileCount
            };
        }
        catch
        {
            TryDeleteDirectory(stagingDir);
            if (emptyAside != null && !Directory.Exists(destFull) && Directory.Exists(emptyAside))
            {
                try
                {
                    Directory.Move(emptyAside, destFull);
                }
                catch
                {
                    // Best-effort restore of the previously empty destination.
                }
            }

            throw;
        }
        finally
        {
            if (!string.IsNullOrEmpty(stagingDir))
            {
                TryDeleteDirectory(stagingDir);
            }

            if (!string.IsNullOrEmpty(emptyAside) && Directory.Exists(emptyAside) && Directory.Exists(destFull))
            {
                TryDeleteDirectory(emptyAside);
            }
        }
    }

    internal byte[] SealPayload(
        byte[] qnapPlaintext,
        string archivePassword,
        byte[] recoveryKeyMaterial,
        int pbkdf2Iterations,
        Guid archiveId,
        long createdAtUnixUtc)
    {
        EncryptedArchiveInMemoryLimits.EnsureAllocatedSizeFits(qnapPlaintext.LongLength, formatException: false);
        _kdfLimits.ValidateIterations(pbkdf2Iterations);
        var header = new EncryptedArchiveCanonicalHeader
        {
            FormatVersion = EncryptedArchiveConstants.FormatVersionV1,
            ArchiveId = archiveId,
            CreatedAtUnixUtc = createdAtUnixUtc,
            KdfAlg = EncryptedArchiveConstants.KdfAlgorithmV1,
            KdfVersion = EncryptedArchiveConstants.KdfVersionV1,
            KdfIterations = pbkdf2Iterations,
            WrapCount = EncryptedArchiveConstants.WrapCountV1,
            PayloadContentType = EncryptedArchiveConstants.PayloadContentTypeV1
        };

        string canonical = header.ToCanonicalString();
        byte[] dek = RandomNumberGenerator.GetBytes(EncryptedArchiveConstants.KeyByteSize);
        try
        {
            byte[] passwordSalt = RandomNumberGenerator.GetBytes(EncryptedArchiveConstants.SaltByteSize);
            byte[] recoverySalt = RandomNumberGenerator.GetBytes(EncryptedArchiveConstants.SaltByteSize);
            byte[] passwordKek = EncryptedArchiveCrypto.DerivePasswordKek(archivePassword, passwordSalt, pbkdf2Iterations);
            byte[] passwordWrap;
            try
            {
                byte[] aad = EncryptedArchiveCrypto.BuildWrapAad(canonical, EncryptedArchiveConstants.WrapSlotPassword);
                byte[] ct = EncryptedArchiveCrypto.WrapDek(passwordKek, dek, aad, out byte[] nonce, out byte[] tag);
                passwordWrap = EncryptedArchiveCrypto.EncodeWrapSlot(passwordSalt, nonce, ct, tag);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passwordKek);
            }

            byte[] recoveryKek = EncryptedArchiveCrypto.DeriveRecoveryKek(recoveryKeyMaterial, recoverySalt);
            byte[] recoveryWrap;
            try
            {
                byte[] aad = EncryptedArchiveCrypto.BuildWrapAad(canonical, EncryptedArchiveConstants.WrapSlotRecovery);
                byte[] ct = EncryptedArchiveCrypto.WrapDek(recoveryKek, dek, aad, out byte[] nonce, out byte[] tag);
                recoveryWrap = EncryptedArchiveCrypto.EncodeWrapSlot(recoverySalt, nonce, ct, tag);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(recoveryKek);
            }

            byte[] payloadAad = EncryptedArchiveCrypto.BuildPayloadAad(
                canonical,
                EncryptedArchiveConstants.PayloadContentTypeV1,
                qnapPlaintext.Length);
            EncryptedArchiveCrypto.EncryptPayload(dek, qnapPlaintext, payloadAad, out byte[] payloadNonce, out byte[] payloadCt, out byte[] payloadTag);

            return EncryptedArchiveFraming.Write(new EncryptedArchiveFramedFile
            {
                Header = header,
                CanonicalHeaderText = canonical,
                PasswordWrap = passwordWrap,
                RecoveryWrap = recoveryWrap,
                PayloadNonce = payloadNonce,
                PayloadCiphertext = payloadCt,
                PayloadTag = payloadTag
            });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    private byte[] UnwrapDek(EncryptedArchiveFramedFile framed, string? archivePassword, string? recoveryKeyFormatted)
    {
        bool hasPassword = !string.IsNullOrEmpty(archivePassword);
        bool hasRecovery = !string.IsNullOrWhiteSpace(recoveryKeyFormatted);
        if (hasPassword == hasRecovery)
        {
            throw new EncryptedArchiveValidationException("Нужен ровно один секрет: пароль архива или recovery key.");
        }

        if (hasPassword)
        {
            EncryptedArchiveCrypto.DecodeWrapSlot(framed.PasswordWrap, out byte[] salt, out byte[] nonce, out byte[] ct, out byte[] tag);
            byte[] kek = EncryptedArchiveCrypto.DerivePasswordKek(archivePassword!, salt, framed.Header.KdfIterations);
            try
            {
                byte[] aad = EncryptedArchiveCrypto.BuildWrapAad(framed.CanonicalHeaderText, EncryptedArchiveConstants.WrapSlotPassword);
                return EncryptedArchiveCrypto.UnwrapDek(kek, ct, nonce, tag, aad);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(kek);
            }
        }

        byte[] recoveryKey = RecoveryKeyEncoding.Parse(recoveryKeyFormatted!);
        try
        {
            EncryptedArchiveCrypto.DecodeWrapSlot(framed.RecoveryWrap, out byte[] salt, out byte[] nonce, out byte[] ct, out byte[] tag);
            byte[] kek = EncryptedArchiveCrypto.DeriveRecoveryKek(recoveryKey, salt);
            try
            {
                byte[] aad = EncryptedArchiveCrypto.BuildWrapAad(framed.CanonicalHeaderText, EncryptedArchiveConstants.WrapSlotRecovery);
                return EncryptedArchiveCrypto.UnwrapDek(kek, ct, nonce, tag, aad);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(kek);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(recoveryKey);
        }
    }

    private static void ValidatePayloadShape(IReadOnlyList<QnapEntry> entries, string? restoreDestinationDirectory)
    {
        int snapshots = entries.Count(e => e.Path == EncryptedArchiveConstants.SnapshotEntryPath);
        int manifests = entries.Count(e => e.Path == EncryptedArchiveConstants.ManifestEntryPath);
        if (snapshots != 1 || manifests != 1)
        {
            throw new EncryptedArchiveValidationException("Нагрузка архива повреждена.");
        }

        foreach (QnapEntry entry in entries)
        {
            if (!string.IsNullOrWhiteSpace(restoreDestinationDirectory))
            {
                EncryptedArchivePathRules.EnsureResolvedInsideRoot(restoreDestinationDirectory, entry.Path);
            }
        }
    }

    private static PayloadManifestDto ParseAndValidateManifest(IReadOnlyList<QnapEntry> entries)
    {
        QnapEntry manifestEntry = entries.First(e => e.Path == EncryptedArchiveConstants.ManifestEntryPath);
        PayloadManifestDto? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<PayloadManifestDto>(manifestEntry.Content, ManifestJson);
        }
        catch (JsonException)
        {
            throw new EncryptedArchiveValidationException("Нагрузка архива повреждена.");
        }

        if (manifest == null || manifest.Schema != "payload-manifest-v1" || manifest.Files == null)
        {
            throw new EncryptedArchiveValidationException("Нагрузка архива повреждена.");
        }

        var payloadFiles = entries.Where(e => e.Path != EncryptedArchiveConstants.ManifestEntryPath).ToList();
        if (manifest.Files.Count != payloadFiles.Count)
        {
            throw new EncryptedArchiveValidationException("Нагрузка архива повреждена.");
        }

        int attachmentCount = payloadFiles.Count(e => e.Path.StartsWith(EncryptedArchiveConstants.AttachmentsPrefix, StringComparison.Ordinal));
        if (manifest.AttachmentFileCount != attachmentCount
            || manifest.NoteCount < 0
            || manifest.ProtectedNoteCount < 0
            || manifest.ProtectedNoteCount > manifest.NoteCount)
        {
            throw new EncryptedArchiveValidationException("Нагрузка архива повреждена.");
        }

        var byPath = payloadFiles.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PayloadManifestFileDto file in manifest.Files)
        {
            string path = EncryptedArchivePathRules.NormalizeRelative(file.Path);
            EncryptedArchivePathRules.ValidateAllowedPayloadPath(path);
            if (!seen.Add(path) || !byPath.TryGetValue(path, out QnapEntry? entry))
            {
                throw new EncryptedArchiveValidationException("Нагрузка архива повреждена.");
            }

            string actualSha = Convert.ToHexString(entry.Sha256).ToLowerInvariant();
            if (file.Size != entry.Content.Length
                || !string.Equals(file.Sha256, actualSha, StringComparison.OrdinalIgnoreCase))
            {
                throw new EncryptedArchiveValidationException("Контрольная сумма записи архива не совпала.");
            }
        }

        return manifest;
    }

    private static PayloadManifestFileDto ToManifestFile(QnapEntry entry)
        => new()
        {
            Path = entry.Path,
            Size = entry.Content.Length,
            Sha256 = Convert.ToHexString(entry.Sha256).ToLowerInvariant()
        };

    private static string CreateSqliteOnlineBackup(string dbPath, string stagingDir)
    {
        var backup = new BackupService(dbPath, stagingDir);
        return backup.CreateBackup(maxCopies: int.MaxValue);
    }

    private static List<QnapEntry> PackAttachmentFiles(string attachmentsDirectory, string databasePath)
    {
        var packed = new List<QnapEntry>();
        if (!string.IsNullOrWhiteSpace(attachmentsDirectory) && Directory.Exists(attachmentsDirectory))
        {
            string root = Path.GetFullPath(attachmentsDirectory);
            EncryptedArchivePathRules.ThrowIfReparse(root);
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string dir = pending.Pop();
                EncryptedArchivePathRules.ThrowIfReparse(dir);
                foreach (string file in Directory.EnumerateFiles(dir))
                {
                    EncryptedArchivePathRules.ThrowIfReparse(file);
                    var attrs = File.GetAttributes(file);
                    if ((attrs & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new EncryptedArchiveValidationException("Источник архива содержит reparse-точку.");
                    }

                    string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                    string qnapPath = EncryptedArchiveConstants.AttachmentsPrefix + relative;
                    packed.Add(EncryptedArchivePayload.Create(qnapPath, EncryptedArchiveInMemoryLimits.ReadAllBytesIfSourceFits(file)));
                }

                foreach (string sub in Directory.EnumerateDirectories(dir))
                {
                    EncryptedArchivePathRules.ThrowIfReparse(sub);
                    pending.Push(sub);
                }
            }
        }

        foreach (string required in ListRequiredAttachmentFiles(databasePath, attachmentsDirectory))
        {
            if (!File.Exists(required) || EncryptedArchivePathRules.IsReparsePoint(required))
            {
                throw new EncryptedArchiveValidationException("Обязательное вложение недоступно для архива.");
            }
        }

        return packed;
    }

    private static IEnumerable<string> ListRequiredAttachmentFiles(string databasePath, string attachmentsDirectory)
    {
        if (string.IsNullOrWhiteSpace(attachmentsDirectory) || !File.Exists(databasePath))
        {
            yield break;
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        };

        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        if (!TableExists(connection, "NoteAttachments"))
        {
            yield break;
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT StoredFileName FROM NoteAttachments;";
        using var reader = cmd.ExecuteReader();
        string root = Path.GetFullPath(attachmentsDirectory);
        string rootWithSep = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        while (reader.Read())
        {
            string stored = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            if (string.IsNullOrWhiteSpace(stored))
            {
                throw new EncryptedArchiveValidationException("Обязательное вложение недоступно для архива.");
            }

            string candidate = Path.GetFullPath(Path.Combine(root, Path.GetFileName(stored)));
            if (!candidate.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
            {
                throw new EncryptedArchiveValidationException("Обязательное вложение недоступно для архива.");
            }

            yield return candidate;
        }
    }

    private static SnapshotStats ReadSnapshotStats(string sqlitePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = sqlitePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        };

        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        int userVersion;
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA user_version;";
            userVersion = Convert.ToInt32(pragma.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        int noteCount = 0;
        int protectedCount = 0;
        if (TableExists(connection, "Notes"))
        {
            using var countCmd = connection.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM Notes;";
            noteCount = Convert.ToInt32(countCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (ColumnExists(connection, "Notes", "IsProtected"))
            {
                using var prot = connection.CreateCommand();
                prot.CommandText = "SELECT COUNT(*) FROM Notes WHERE IsProtected = 1;";
                protectedCount = Convert.ToInt32(prot.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }

        return new SnapshotStats(userVersion, noteCount, protectedCount);
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
        cmd.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static bool ColumnExists(SqliteConnection connection, string tableName, string columnName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({QuoteIdent(tableName)});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string QuoteIdent(string name)
        => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static void WriteRestoreTree(string stagingDir, IReadOnlyList<QnapEntry> entries)
    {
        foreach (QnapEntry entry in entries)
        {
            string? relative = EncryptedArchivePathRules.MapPayloadPathToRestoreRelative(entry.Path);
            if (relative == null)
            {
                continue;
            }

            EncryptedArchivePathRules.EnsureResolvedInsideRoot(stagingDir, relative);
            string fullPath = Path.GetFullPath(Path.Combine(stagingDir, relative.Replace('/', Path.DirectorySeparatorChar)));
            WriteFlushedNewFile(fullPath, entry.Content);
        }
    }

    private static void WriteFlushedNewFile(string path, byte[] content)
    {
        string? dir = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(dir))
        {
            throw new EncryptedArchiveValidationException("Некорректный путь восстановления.");
        }

        if (Directory.Exists(dir))
        {
            EncryptedArchivePathRules.ThrowIfReparse(dir, EncryptedArchivePathRules.RestoreReparseMessage);
        }
        else
        {
            Directory.CreateDirectory(dir);
        }

        if (File.Exists(path) || Directory.Exists(path) || EncryptedArchivePathRules.IsReparsePoint(path))
        {
            throw new EncryptedArchiveValidationException("Восстановление не перезаписывает существующие файлы.");
        }

        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(content, 0, content.Length);
        stream.Flush(flushToDisk: true);
    }

    private static void EnsureSqliteIntegrity(string databasePath)
    {
        if (!File.Exists(databasePath) || new FileInfo(databasePath).Length == 0)
        {
            throw new EncryptedArchiveValidationException("Снимок базы в архиве повреждён.");
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        };

        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        object? result = cmd.ExecuteScalar();
        string text = result?.ToString() ?? string.Empty;
        if (!string.Equals(text, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new EncryptedArchiveValidationException("Снимок базы в архиве повреждён.");
        }
    }

    private static void ApplySchemaOnRestoredDatabase(string databasePath)
    {
        var options = new DbContextOptionsBuilder<QuickNotesDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Pooling = false
            }.ToString())
            .Options;

        using var context = new QuickNotesDbContext(options);
        DbInitializer.Initialize(context);
    }

    private static void ReleaseSqliteHandles()
    {
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        SqliteConnection.ClearAllPools();
    }

    private static void PublishRestoredDirectory(string stagingDir, string destFull, ref string? emptyAside)
    {
        string? parent = Path.GetDirectoryName(destFull);
        if (string.IsNullOrWhiteSpace(parent))
        {
            throw new EncryptedArchiveValidationException("Некорректный путь восстановления.");
        }

        if (Directory.Exists(destFull))
        {
            EncryptedArchivePathRules.EnsureRestoreDestinationAllowed(destFull);
            emptyAside = Path.Combine(
                parent,
                "." + Path.GetFileName(destFull) + "." + Guid.NewGuid().ToString("N") + ".empty-aside");
            Directory.Move(destFull, emptyAside);
        }
        else if (File.Exists(destFull))
        {
            throw new EncryptedArchiveValidationException("Восстановление возможно только в новый пустой каталог.");
        }

        Directory.Move(stagingDir, destFull);
    }

    private void PublishNewArchive(string archivePath, byte[] sealedArchive, CancellationToken cancellationToken = default)
    {
        EncryptedArchiveInMemoryLimits.EnsureAllocatedSizeFits(sealedArchive.LongLength, formatException: true);
        string? archiveDir = Path.GetDirectoryName(archivePath);
        if (string.IsNullOrWhiteSpace(archiveDir))
        {
            throw new EncryptedArchiveValidationException("Некорректный путь архива.");
        }

        string tempPath = Path.Combine(
            archiveDir,
            "." + Path.GetFileName(archivePath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                TestBeforeTempWrite?.Invoke(tempPath);
                stream.Write(sealedArchive, 0, sealedArchive.Length);
                stream.Flush(flushToDisk: true);
            }

            TestBeforePublishMove?.Invoke(tempPath);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, archivePath);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }
    }

    private void PublishReplacingExistingArchive(string archivePath, byte[] sealedArchive, CancellationToken cancellationToken = default)
    {
        EncryptedArchiveInMemoryLimits.EnsureAllocatedSizeFits(sealedArchive.LongLength, formatException: true);
        if (!File.Exists(archivePath))
        {
            throw new EncryptedArchiveValidationException("Файл архива не найден.");
        }

        string? archiveDir = Path.GetDirectoryName(archivePath);
        if (string.IsNullOrWhiteSpace(archiveDir))
        {
            throw new EncryptedArchiveValidationException("Некорректный путь архива.");
        }

        string tempPath = Path.Combine(
            archiveDir,
            "." + Path.GetFileName(archivePath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        string backupPath = Path.Combine(
            archiveDir,
            "." + Path.GetFileName(archivePath) + "." + Guid.NewGuid().ToString("N") + ".bak");
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                TestBeforeTempWrite?.Invoke(tempPath);
                stream.Write(sealedArchive, 0, sealedArchive.Length);
                stream.Flush(flushToDisk: true);
            }

            TestBeforePublishMove?.Invoke(tempPath);
            cancellationToken.ThrowIfCancellationRequested();
            File.Replace(tempPath, archivePath, backupPath);
            TryDeleteFile(backupPath);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }
    }

    private static void RestoreOriginalArchiveBytes(string archivePath, byte[] originalBytes)
    {
        try
        {
            if (File.Exists(archivePath))
            {
                byte[] current = File.ReadAllBytes(archivePath);
                if (current.AsSpan().SequenceEqual(originalBytes))
                {
                    return;
                }
            }

            string? archiveDir = Path.GetDirectoryName(archivePath);
            if (string.IsNullOrWhiteSpace(archiveDir))
            {
                return;
            }

            string tempPath = Path.Combine(
                archiveDir,
                "." + Path.GetFileName(archivePath) + "." + Guid.NewGuid().ToString("N") + ".orig.tmp");
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(originalBytes, 0, originalBytes.Length);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(archivePath))
            {
                File.Replace(tempPath, archivePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, archivePath);
            }
        }
        catch
        {
            // Best-effort: the original bytes were captured before mutation.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort temp cleanup after a failed publish.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort staging cleanup after create.
        }
    }

    private readonly record struct SnapshotStats(int UserVersion, int NoteCount, int ProtectedNoteCount);
}
