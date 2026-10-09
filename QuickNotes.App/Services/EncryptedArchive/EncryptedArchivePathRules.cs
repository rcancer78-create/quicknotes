using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QuickNotes.App.Data;

namespace QuickNotes.App.Services.EncryptedArchive;

internal static class EncryptedArchivePathRules
{
    internal static string NormalizeRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new EncryptedArchiveValidationException("Некорректный путь внутри архива.");
        }

        if (path.IndexOf(':') >= 0
            || path.Contains('\0', StringComparison.Ordinal)
            || path.StartsWith("\\\\?\\", StringComparison.Ordinal)
            || path.StartsWith("//?/", StringComparison.Ordinal)
            || Path.IsPathRooted(path))
        {
            throw new EncryptedArchiveValidationException("Некорректный путь внутри архива.");
        }

        string normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/')
            || normalized.StartsWith("//", StringComparison.Ordinal)
            || normalized.Contains("://", StringComparison.Ordinal))
        {
            throw new EncryptedArchiveValidationException("Некорректный путь внутри архива.");
        }

        string[] segments = normalized.Split('/', StringSplitOptions.None);
        if (segments.Length == 0 || segments.Length > EncryptedArchiveConstants.MaxPathDepth)
        {
            throw new EncryptedArchiveValidationException("Некорректный путь внутри архива.");
        }

        foreach (string segment in segments)
        {
            if (string.IsNullOrEmpty(segment)
                || segment == "."
                || segment == ".."
                || segment.EndsWith(' ')
                || segment.EndsWith('.')
                || segment.Contains(':', StringComparison.Ordinal))
            {
                throw new EncryptedArchiveValidationException("Некорректный путь внутри архива.");
            }
        }

        return string.Join('/', segments);
    }

    internal static void ValidateAllowedPayloadPath(string normalized)
    {
        if (normalized == EncryptedArchiveConstants.SnapshotEntryPath
            || normalized == EncryptedArchiveConstants.ManifestEntryPath)
        {
            return;
        }

        if (!normalized.StartsWith(EncryptedArchiveConstants.AttachmentsPrefix, StringComparison.Ordinal)
            || normalized.Length <= EncryptedArchiveConstants.AttachmentsPrefix.Length)
        {
            throw new EncryptedArchiveValidationException("Некорректный путь внутри архива.");
        }
    }

    internal static void EnsureResolvedInsideRoot(string destinationRoot, string relativeNormalized)
    {
        string root = Path.GetFullPath(destinationRoot);
        string rootWithSep = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string combined = Path.GetFullPath(Path.Combine(root, relativeNormalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!combined.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            throw new EncryptedArchiveValidationException("Некорректный путь внутри архива.");
        }
    }

    internal const string SourceReparseMessage = "Источник архива содержит reparse-точку.";
    internal const string RestoreReparseMessage = "Путь восстановления содержит reparse-точку.";

    internal static bool IsReparsePoint(string fullPath)
    {
        try
        {
            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                return false;
            }

            return (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    internal static void ThrowIfReparse(string fullPath, string? message = null)
    {
        bool exists;
        try
        {
            exists = File.Exists(fullPath) || Directory.Exists(fullPath);
        }
        catch
        {
            throw new EncryptedArchiveValidationException("Не удалось проверить путь на reparse-точку.");
        }

        if (!exists)
        {
            return;
        }

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(fullPath);
        }
        catch
        {
            throw new EncryptedArchiveValidationException("Не удалось проверить путь на reparse-точку.");
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new EncryptedArchiveValidationException(message ?? SourceReparseMessage);
        }
    }

    internal static void ThrowIfExistingAncestorChainHasReparse(string fullPath, string? message = null)
    {
        string current = Path.GetFullPath(fullPath);
        string text = message ?? RestoreReparseMessage;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (seen.Add(current))
        {
            ThrowIfReparse(current, text);
            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent))
            {
                break;
            }

            current = parent;
        }
    }

    internal static void EnsureRestoreDestinationAllowed(string? destinationDirectory)
    {
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            return;
        }

        string full = Path.GetFullPath(destinationDirectory);
        EnsureNotLiveProfile(full);
        ThrowIfExistingAncestorChainHasReparse(full);

        if (File.Exists(full))
        {
            throw new EncryptedArchiveValidationException("Восстановление возможно только в новый пустой каталог.");
        }

        if (!Directory.Exists(full))
        {
            return;
        }

        if (Directory.EnumerateFileSystemEntries(full).Any())
        {
            throw new EncryptedArchiveValidationException("Восстановление возможно только в новый пустой каталог.");
        }
    }

    internal static void EnsureNotLiveProfile(string destinationFullPath)
    {
        // An isolated-profile override changes the active profile, but must never remove
        // protection from the real user profile (including before its first launch).
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string activeRoot = QuickNotesDbContext.GetDefaultProfileDirectory();
        if (IsSameOrNestedPath(destinationFullPath, liveRoot)
            || IsSameOrNestedPath(destinationFullPath, activeRoot))
        {
            throw new EncryptedArchiveValidationException("Восстановление в живой профиль не поддерживается.");
        }
    }

    internal static bool IsSameOrNestedPath(string candidate, string root)
    {
        string candidateFull = Path.GetFullPath(candidate)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string rootFull = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(candidateFull, rootFull, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string rootPrefix = rootFull + Path.DirectorySeparatorChar;
        return candidateFull.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
    }

    internal static string? MapPayloadPathToRestoreRelative(string payloadPath)
    {
        if (payloadPath == EncryptedArchiveConstants.SnapshotEntryPath)
        {
            return EncryptedArchiveConstants.RestoredDatabaseFileName;
        }

        if (payloadPath == EncryptedArchiveConstants.ManifestEntryPath)
        {
            return null;
        }

        if (payloadPath.StartsWith(EncryptedArchiveConstants.AttachmentsPrefix, StringComparison.Ordinal)
            && payloadPath.Length > EncryptedArchiveConstants.AttachmentsPrefix.Length)
        {
            return EncryptedArchiveConstants.RestoredAttachmentsDirectoryName
                   + "/"
                   + payloadPath[EncryptedArchiveConstants.AttachmentsPrefix.Length..];
        }

        throw new EncryptedArchiveValidationException("Некорректный путь внутри архива.");
    }
}
