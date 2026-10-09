using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QuickNotes.App.Services.EncryptedArchive;

namespace QuickNotes.App.Services;

public static class ImportPathSafety
{
    public static bool TryGetSafeFullPath(string path, out string fullPath, out string? error)
    {
        fullPath = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Путь пуст.";
            return false;
        }

        if (path.IndexOf('\0') >= 0 || path.Contains("://", StringComparison.Ordinal))
        {
            error = "Недопустимый путь.";
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            error = "Не удалось нормализовать путь.";
            return false;
        }

        if (EncryptedArchivePathRules.IsReparsePoint(fullPath))
        {
            error = "Путь содержит точку повторной обработки (symlink/reparse) и отклонён.";
            return false;
        }

        return true;
    }

    public static bool TryResolveLocalRelative(
        string sourceFilePath,
        string relativeReference,
        string importRoot,
        out string resolvedFullPath,
        out string? error)
    {
        resolvedFullPath = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(relativeReference))
        {
            error = "Пустая ссылка на вложение.";
            return false;
        }

        var trimmed = relativeReference.Trim().Replace('\\', '/');
        if (trimmed.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("://", StringComparison.Ordinal)
            || trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.Contains('\0')
            || trimmed.Contains(':'))
        {
            error = "Внешняя или небезопасная ссылка на файл отклонена.";
            return false;
        }

        var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Length > ImportLimits.MaxPathDepth
            || segments.Any(s => s == "." || s == ".."))
        {
            error = "Относительный путь отклонён (обход каталога).";
            return false;
        }

        string baseDir = Path.GetDirectoryName(Path.GetFullPath(sourceFilePath)) ?? string.Empty;
        string combined;
        try
        {
            combined = Path.GetFullPath(Path.Combine(baseDir, string.Join(Path.DirectorySeparatorChar, segments)));
        }
        catch (Exception)
        {
            error = "Не удалось разрешить относительный путь.";
            return false;
        }

        if (!TryEnsureInsideRoot(combined, importRoot, out error))
        {
            return false;
        }

        if (EncryptedArchivePathRules.IsReparsePoint(combined))
        {
            error = "Вложение указывает на reparse-точку и отклонено.";
            return false;
        }

        resolvedFullPath = combined;
        return true;
    }

    public static bool TryEnsureInsideRoot(string candidateFullPath, string rootPath, out string? error)
    {
        error = null;
        string root;
        string candidate;
        try
        {
            root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
            candidate = Path.GetFullPath(candidateFullPath);
        }
        catch (Exception)
        {
            error = "Не удалось проверить корень импорта.";
            return false;
        }

        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(candidate.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            error = "Путь выходит за пределы выбранного каталога импорта.";
            return false;
        }

        return true;
    }

    public static bool IsRejectedExternalOrUnsafeUri(string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return true;
        }

        var value = href.Trim();
        if (value.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("http:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("ftp:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("//", StringComparison.Ordinal)
            || value.StartsWith('='))
        {
            return true;
        }

        return false;
    }

    public static IEnumerable<string> EnumerateExistingAncestors(string fullPath)
    {
        string current = Path.GetFullPath(fullPath);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (seen.Add(current))
        {
            yield return current;
            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                yield break;
            }

            current = parent;
        }
    }

    public static bool TreeContainsReparse(string root, out string? error)
    {
        error = null;
        foreach (var ancestor in EnumerateExistingAncestors(root))
        {
            if (EncryptedArchivePathRules.IsReparsePoint(ancestor))
            {
                error = "Каталог импорта или его предки содержат reparse-точку.";
                return true;
            }
        }

        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            {
                if (EncryptedArchivePathRules.IsReparsePoint(entry))
                {
                    error = "В дереве импорта обнаружена reparse-точка.";
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            error = $"Не удалось проверить дерево импорта: {ex.Message}";
            return true;
        }

        return false;
    }
}
