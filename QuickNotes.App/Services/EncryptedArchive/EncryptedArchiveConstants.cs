using System.Text;

namespace QuickNotes.App.Services.EncryptedArchive;

public static class EncryptedArchiveConstants
{
    public const string FormatName = "quicknotes-encrypted-archive";
    public const string FileExtension = ".qnar";
    public const int FormatVersionV1 = 1;
    /// <summary>
    /// Archive KDF algorithm id (ADR-002 / ADR-004). Equal by value to
    /// <see cref="QuickNotes.App.Services.Crypto.KdfAlgorithmIds.Pbkdf2HmacSha256"/>; the archive
    /// header format (and its AAD binding) is unchanged by the shared descriptor contract.
    /// </summary>
    public const string KdfAlgorithmV1 = QuickNotes.App.Services.Crypto.KdfAlgorithmIds.Pbkdf2HmacSha256;

    /// <summary>Archive KDF descriptor version; equal by value to the shared descriptor version.</summary>
    public const int KdfVersionV1 = QuickNotes.App.Services.Crypto.KdfDescriptorConstants.CurrentDescriptorVersion;
    public const string PayloadContentTypeV1 = "snapshot-v1";
    public const int WrapCountV1 = 2;
    public const string WrapSlotPassword = "password";
    public const string WrapSlotRecovery = "recovery";
    public const string HkdfInfoRecoveryV1 = "QNAR-wrap-recovery-v1";

    public const string SnapshotEntryPath = "snapshot/quicknotes.db";
    public const string ManifestEntryPath = "payload-manifest.json";
    public const string AttachmentsPrefix = "attachments/";
    public const string RestoredDatabaseFileName = "quicknotes.db";
    public const string RestoredAttachmentsDirectoryName = "Attachments";

    public const int MagicSize = 4;
    public const int HeaderLengthSize = 2;
    public const int SaltByteSize = 32;
    public const int NonceByteSize = 12;
    public const int TagByteSize = 16;
    public const int KeyByteSize = 32;
    public const int WrapSlotByteSize = SaltByteSize + NonceByteSize + KeyByteSize + TagByteSize; // 92
    public const int PayloadLengthSize = 8;
    public const int Sha256ByteSize = 32;

    public const int MinKdfIterationsUntrusted = 10_000;
    public const int MaxKdfIterations = 5_000_000;

    /// <summary>
    /// Shipping default for *new* QNAR create only (ADR-002 B4 / ADR-004).
    /// Measured locally 2026-09-10 on this repo's Windows/.NET 8 host (~250 ms PBKDF2).
    /// Not a universal cost and not a fallback when reading an explicit header N.
    /// </summary>
    public const int DefaultPbkdf2IterationsForNewArchives = 2_490_000;

    /// <summary>
    /// Conservative v1 in-memory budget. Below <see cref="int.MaxValue"/> so every accepted
    /// length can be a <c>byte[]</c>. Format fields stay u64; values above this are rejected
    /// before allocation. Not an on-disk 8 GiB capability.
    /// </summary>
    public const int InMemoryBudgetBytes = 512 * 1024 * 1024;

    public const long MaxArchiveFileBytes = InMemoryBudgetBytes;
    public const int MaxHeaderBytes = 4 * 1024;
    public const long MaxPayloadBytes = InMemoryBudgetBytes;
    public const int MaxEntryCount = 100_000;
    public const int MaxPathUtf8Bytes = 1024;
    public const long MaxSingleFileBytes = InMemoryBudgetBytes;
    public const int MaxPathDepth = 32;

    /// <summary>
    /// Max UTF-8 bytes of a CLI stdin secret after stripping trailing CR/LF only.
    /// </summary>
    public const int MaxStdinSecretUtf8Bytes = 4096;

    public static readonly byte[] MagicQnar = Encoding.ASCII.GetBytes("QNAR");
    public static readonly byte[] MagicQnap = Encoding.ASCII.GetBytes("QNAP");
}
