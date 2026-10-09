using System;
using System.Collections.Generic;
using System.IO;

namespace QuickNotes.App.Services;

/// <summary>
/// Best-effort removal of the leftover local vector-index SQLite file from ADR-005
/// direction B. Only the historically known filename in the given profile root is
/// eligible. Notes database, backups, settings, and other files are never targeted.
/// </summary>
public static class LegacyUnsupportedIndexCleanup
{
    public const string KnownIndexFileName = "semantic_index.db";

    public static IReadOnlyList<string> GetKnownCandidatePaths(string profileDirectory)
    {
        if (string.IsNullOrWhiteSpace(profileDirectory))
        {
            return Array.Empty<string>();
        }

        string root = Path.GetFullPath(profileDirectory);
        return new[]
        {
            Path.Combine(root, KnownIndexFileName),
            Path.Combine(root, KnownIndexFileName + "-wal"),
            Path.Combine(root, KnownIndexFileName + "-shm")
        };
    }

    public static void TryDeleteKnownIndexFiles(string profileDirectory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(profileDirectory))
            {
                return;
            }

            string root = Path.GetFullPath(profileDirectory);
            foreach (string candidate in GetKnownCandidatePaths(root))
            {
                TryDeleteExactKnownFile(root, candidate);
            }
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("LegacyUnsupportedIndexCleanup", ex);
        }
    }

    private static void TryDeleteExactKnownFile(string profileRoot, string candidatePath)
    {
        try
        {
            string full = Path.GetFullPath(candidatePath);
            string parent = Path.GetDirectoryName(full) ?? string.Empty;
            if (!string.Equals(parent, profileRoot, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string name = Path.GetFileName(full);
            if (!IsKnownIndexFileName(name))
            {
                return;
            }

            var info = new FileInfo(full);
            if (!info.Exists)
            {
                return;
            }

            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return;
            }

            info.Delete();
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("LegacyUnsupportedIndexCleanup.Delete", ex);
        }
    }

    private static bool IsKnownIndexFileName(string name)
        => name.Equals(KnownIndexFileName, StringComparison.OrdinalIgnoreCase)
           || name.Equals(KnownIndexFileName + "-wal", StringComparison.OrdinalIgnoreCase)
           || name.Equals(KnownIndexFileName + "-shm", StringComparison.OrdinalIgnoreCase);
}
