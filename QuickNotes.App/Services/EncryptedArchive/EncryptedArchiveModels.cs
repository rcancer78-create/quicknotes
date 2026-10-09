using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace QuickNotes.App.Services.EncryptedArchive;

public sealed class EncryptedArchiveCreateRequest
{
    public required string DatabasePath { get; init; }
    public required string AttachmentsDirectory { get; init; }
    public required string DestinationArchivePath { get; init; }
    public required string ArchivePassword { get; init; }

    /// <summary>
    /// PBKDF2 iterations for this archive. Omit to use the production shipping default
    /// for new QNAR files. Readers never apply that default; they use the header N.
    /// </summary>
    public int? Pbkdf2Iterations { get; init; }
}

public sealed class EncryptedArchiveCreateResult
{
    public required string ArchivePath { get; init; }
    public required Guid ArchiveId { get; init; }
    public required string RecoveryKeyFormatted { get; init; }
    public required int Pbkdf2Iterations { get; init; }
    public required int SnapshotUserVersion { get; init; }
    public required int NoteCount { get; init; }
    public required int ProtectedNoteCount { get; init; }
    public required int AttachmentFileCount { get; init; }
}

public sealed class EncryptedArchiveDryRunRequest
{
    public required string ArchivePath { get; init; }
    public string? ArchivePassword { get; init; }
    public string? RecoveryKeyFormatted { get; init; }
    public string? RestoreDestinationDirectory { get; init; }
}

public sealed class EncryptedArchiveDryRunResult
{
    public required Guid ArchiveId { get; init; }
    public required int FormatVersion { get; init; }
    public required DateTime CreatedAtUtc { get; init; }
    public required int SnapshotUserVersion { get; init; }
    public required int NoteCount { get; init; }
    public required int ProtectedNoteCount { get; init; }
    public required int AttachmentFileCount { get; init; }
    public required int PayloadEntryCount { get; init; }
    public required IReadOnlyList<string> EntryPaths { get; init; }
    internal byte[] SnapshotSqliteBytes { get; init; } = Array.Empty<byte>();
    internal IReadOnlyList<QnapEntry> Entries { get; init; } = Array.Empty<QnapEntry>();
}

internal sealed class PayloadManifestFileDto
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;
}

internal sealed class PayloadManifestDto
{
    [JsonPropertyName("schema")]
    public string Schema { get; set; } = "payload-manifest-v1";

    [JsonPropertyName("userVersion")]
    public int UserVersion { get; set; }

    [JsonPropertyName("noteCount")]
    public int NoteCount { get; set; }

    [JsonPropertyName("protectedNoteCount")]
    public int ProtectedNoteCount { get; set; }

    [JsonPropertyName("attachmentFileCount")]
    public int AttachmentFileCount { get; set; }

    [JsonPropertyName("files")]
    public List<PayloadManifestFileDto> Files { get; set; } = new();
}

public sealed class EncryptedArchiveRestoreRequest
{
    public required string ArchivePath { get; init; }
    public string? ArchivePassword { get; init; }
    public string? RecoveryKeyFormatted { get; init; }
    public required string DestinationDirectory { get; init; }
}

public sealed class EncryptedArchiveRestoreResult
{
    public required string DestinationDirectory { get; init; }
    public required string DatabasePath { get; init; }
    public required string AttachmentsDirectory { get; init; }
    public required Guid ArchiveId { get; init; }
    public required int FormatVersion { get; init; }
    public required DateTime CreatedAtUtc { get; init; }
    public required int SnapshotUserVersion { get; init; }
    public required int NoteCount { get; init; }
    public required int ProtectedNoteCount { get; init; }
    public required int AttachmentFileCount { get; init; }
}

public sealed class EncryptedArchiveRotateRecoveryRequest
{
    public required string ArchivePath { get; init; }
    public string? ArchivePassword { get; init; }
    public string? CurrentRecoveryKeyFormatted { get; init; }
}

public sealed class EncryptedArchiveRotateRecoveryResult
{
    public required string ArchivePath { get; init; }
    public required Guid ArchiveId { get; init; }
    public required string RecoveryKeyFormatted { get; init; }
}

public interface IEncryptedArchiveService
{
    EncryptedArchiveCreateResult Create(EncryptedArchiveCreateRequest request, CancellationToken cancellationToken = default);
    EncryptedArchiveDryRunResult DryRun(EncryptedArchiveDryRunRequest request, CancellationToken cancellationToken = default);
    EncryptedArchiveRestoreResult Restore(EncryptedArchiveRestoreRequest request, CancellationToken cancellationToken = default);
    EncryptedArchiveRotateRecoveryResult RotateRecovery(EncryptedArchiveRotateRecoveryRequest request, CancellationToken cancellationToken = default);
}
