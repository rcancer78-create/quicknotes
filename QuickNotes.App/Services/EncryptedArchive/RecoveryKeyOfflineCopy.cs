using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace QuickNotes.App.Services.EncryptedArchive;

/// <summary>
/// Printable/file payload for a recovery key. The secret is never written next to a .qnar by default
/// and is only persisted when the caller supplies a new destination path.
/// </summary>
public static class RecoveryKeyOfflineCopy
{
    public const string FileTitle = "QuickNotes recovery key — offline copy";
    public const string DefaultFileName = "QuickNotes-recovery-key.txt";

    public static string SuggestedNewFilePath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            DefaultFileName);

    public static string BuildDocument(string recoveryKeyFormatted, Guid? archiveId = null)
    {
        byte[] material = RecoveryKeyEncoding.Parse(recoveryKeyFormatted);
        try
        {
            string checksumGroups = RecoveryKeyEncoding.ChecksumGroups(recoveryKeyFormatted);
            var sb = new StringBuilder();
            sb.AppendLine(FileTitle);
            sb.AppendLine("================================");
            sb.AppendLine();
            sb.AppendLine("This file is a secret. Keep it offline, separate from the .qnar archive and from the archive password.");
            sb.AppendLine("It does not replace a protected note password. QuickNotes does not copy it to the clipboard.");
            sb.AppendLine("Encoding: Crockford Base32, groups of 4. The last two groups are a SHA-256 checksum of the key material.");
            if (archiveId.HasValue && archiveId.Value != Guid.Empty)
            {
                sb.AppendLine("Archive id (not a secret): " + archiveId.Value.ToString("D"));
            }

            sb.AppendLine();
            sb.AppendLine("Recovery key:");
            sb.AppendLine(recoveryKeyFormatted);
            sb.AppendLine();
            sb.AppendLine("Checksum groups (last two groups of the key above):");
            sb.AppendLine(checksumGroups);
            sb.AppendLine();
            sb.AppendLine("To restore: enter the full recovery key (including checksum groups) into QuickNotes or --recovery-key-stdin.");
            return sb.ToString();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    public static void WriteNewFile(string destinationPath, string recoveryKeyFormatted, string? archivePathToKeepApart, Guid? archiveId = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new EncryptedArchiveValidationException("Путь офлайн-копии не задан.");
        }

        string destFull = Path.GetFullPath(destinationPath);
        if (Directory.Exists(destFull))
        {
            throw new EncryptedArchiveValidationException("Офлайн-копия должна быть новым файлом, не каталогом.");
        }

        if (File.Exists(destFull))
        {
            throw new EncryptedArchiveValidationException("Файл офлайн-копии уже существует.");
        }

        EnsureNotBesideArchive(destFull, archivePathToKeepApart);

        string? dir = Path.GetDirectoryName(destFull);
        if (string.IsNullOrWhiteSpace(dir))
        {
            throw new EncryptedArchiveValidationException("Некорректный путь офлайн-копии.");
        }

        Directory.CreateDirectory(dir);
        string document = BuildDocument(recoveryKeyFormatted, archiveId);
        byte[] utf8 = Encoding.UTF8.GetBytes(document);
        string tempPath = Path.Combine(dir, "." + Path.GetFileName(destFull) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(utf8, 0, utf8.Length);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, destFull);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }
    }

    public static void EnsureNotBesideArchive(string destinationFilePath, string? archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            return;
        }

        string destDir = Path.GetFullPath(Path.GetDirectoryName(Path.GetFullPath(destinationFilePath)) ?? string.Empty);
        string archiveDir = Path.GetFullPath(Path.GetDirectoryName(Path.GetFullPath(archivePath)) ?? string.Empty);
        if (destDir.Length == 0 || archiveDir.Length == 0)
        {
            return;
        }

        if (string.Equals(destDir, archiveDir, StringComparison.OrdinalIgnoreCase))
        {
            throw new EncryptedArchiveValidationException(
                "Офлайн-копию recovery key нельзя сохранять в тот же каталог, что и файл .qnar.");
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
            // Best-effort temp cleanup after a failed write.
        }
    }
}
