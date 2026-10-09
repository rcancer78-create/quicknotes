using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace QuickNotes.App.Data;

/// <summary>
/// Isolated-profile paths are compared both lexically and after Windows reparse resolution.
/// A junction/symlink whose final target is the live profile (or a relative of it) is rejected
/// before any database or settings write.
/// </summary>
internal static class IsolatedProfilePath
{
    private const uint FileReadAttributes = 0x0080;
    private const uint FileShareReadWriteDelete = 0x0001 | 0x0002 | 0x0004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileNameNormalized = 0;

    internal static string Normalize(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    internal static bool IsSameOrRelated(string candidate, string other)
    {
        string left = Normalize(candidate);
        string right = Normalize(other);
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string leftPrefix = left + Path.DirectorySeparatorChar;
        string rightPrefix = right + Path.DirectorySeparatorChar;
        return left.StartsWith(rightPrefix, StringComparison.OrdinalIgnoreCase)
            || right.StartsWith(leftPrefix, StringComparison.OrdinalIgnoreCase);
    }

    internal static string ResolveExistingChain(string path)
    {
        string full = Normalize(path);
        var missing = new Stack<string>();
        string current = full;

        while (true)
        {
            if (TryGetExistingEntry(current, out bool isReparse))
            {
                string? finalPath = TryGetFinalPath(current);
                if (string.IsNullOrWhiteSpace(finalPath))
                {
                    if (isReparse)
                    {
                        throw new InvalidOperationException(
                            "Isolated profile path contains a reparse point that could not be resolved to a final directory.");
                    }

                    finalPath = current;
                }

                string resolved = Normalize(finalPath);
                while (missing.Count > 0)
                {
                    resolved = Path.Combine(resolved, missing.Pop());
                }

                return Normalize(resolved);
            }

            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                return full;
            }

            string leaf = Path.GetFileName(current);
            if (!string.IsNullOrEmpty(leaf))
            {
                missing.Push(leaf);
            }

            current = parent;
        }
    }

    private static bool TryGetExistingEntry(string path, out bool isReparse)
    {
        isReparse = false;
        try
        {
            if (!Directory.Exists(path) && !File.Exists(path))
            {
                return false;
            }

            isReparse = (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static string? TryGetFinalPath(string existingPath)
    {
        using SafeFileHandle handle = CreateFileW(
            existingPath,
            FileReadAttributes,
            FileShareReadWriteDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return null;
        }

        var buffer = new StringBuilder(512);
        int chars = GetFinalPathNameByHandle(handle, buffer, buffer.Capacity, FileNameNormalized);
        if (chars <= 0)
        {
            return null;
        }

        if (chars >= buffer.Capacity)
        {
            buffer.EnsureCapacity(chars + 2);
            chars = GetFinalPathNameByHandle(handle, buffer, buffer.Capacity, FileNameNormalized);
            if (chars <= 0)
            {
                return null;
            }
        }

        return StripExtendedPrefix(buffer.ToString());
    }

    internal static string StripExtendedPrefix(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string dosPrefix = @"\\?\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path.Substring(uncPrefix.Length);
        }

        if (path.StartsWith(dosPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return path.Substring(dosPrefix.Length);
        }

        return path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetFinalPathNameByHandle(
        SafeFileHandle hFile,
        StringBuilder lpszFilePath,
        int cchFilePath,
        uint dwFlags);
}
