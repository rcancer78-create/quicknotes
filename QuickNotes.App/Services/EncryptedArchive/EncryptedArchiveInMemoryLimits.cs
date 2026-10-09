using System;
using System.IO;

namespace QuickNotes.App.Services.EncryptedArchive;

/// <summary>
/// Honest v1 ceiling for this in-memory implementation (payload and archive live in <c>byte[]</c>).
/// Declared u64 fields in the format may be larger; they are rejected before allocation.
/// </summary>
internal static class EncryptedArchiveInMemoryLimits
{
    internal const string OversizeMessage = "Размер записи архива превышает лимит.";

    /// <summary>
    /// Tests may report a hostile length for a small on-disk file so oversize paths
    /// are covered without allocating a buffer at the budget.
    /// </summary>
    internal static Func<string, long>? GetLengthOverrideForTests;

    internal static long GetLength(string path)
    {
        Func<string, long>? overrideLength = GetLengthOverrideForTests;
        return overrideLength != null ? overrideLength(path) : new FileInfo(path).Length;
    }

    internal static void EnsureArchiveFileFits(string path)
    {
        if (GetLength(path) > EncryptedArchiveConstants.MaxArchiveFileBytes)
        {
            throw new EncryptedArchiveFormatException(OversizeMessage);
        }
    }

    internal static void EnsureSourceFileFits(string path)
    {
        if (GetLength(path) > EncryptedArchiveConstants.MaxSingleFileBytes)
        {
            throw new EncryptedArchiveValidationException(OversizeMessage);
        }
    }

    internal static byte[] ReadAllBytesIfArchiveFits(string path)
    {
        EnsureArchiveFileFits(path);
        return File.ReadAllBytes(path);
    }

    internal static byte[] ReadAllBytesIfSourceFits(string path)
    {
        EnsureSourceFileFits(path);
        return File.ReadAllBytes(path);
    }

    internal static int CheckedDeclaredBytes(ulong declared, long maxInclusive, bool formatException)
    {
        if (declared > (ulong)maxInclusive)
        {
            throw formatException
                ? new EncryptedArchiveFormatException(OversizeMessage)
                : new EncryptedArchiveValidationException(OversizeMessage);
        }

        return (int)declared;
    }

    internal static void EnsureAllocatedSizeFits(long byteCount, bool formatException)
    {
        if (byteCount < 0 || byteCount > EncryptedArchiveConstants.InMemoryBudgetBytes)
        {
            throw formatException
                ? new EncryptedArchiveFormatException(OversizeMessage)
                : new EncryptedArchiveValidationException(OversizeMessage);
        }
    }
}
