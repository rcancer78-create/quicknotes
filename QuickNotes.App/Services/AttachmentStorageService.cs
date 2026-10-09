using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QuickNotes.App.Data;

namespace QuickNotes.App.Services;

public class AttachmentStorageService : IAttachmentStorageService
{
    public string BaseDirectory { get; }
    public string AttachmentsDirectory { get; }

    public AttachmentStorageService(string? baseDirectory = null)
    {
        BaseDirectory = baseDirectory ?? QuickNotes.App.Data.QuickNotesDbContext.GetDefaultProfileDirectory();

        AttachmentsDirectory = Path.Combine(BaseDirectory, "Attachments");
        Directory.CreateDirectory(AttachmentsDirectory);
    }

    public AttachmentSaveResult SaveAttachment(string sourceFilePath, long maxSizeBytes)
    {
        if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
        {
            throw new FileNotFoundException("Исходный файл для вложения не найден.", sourceFilePath);
        }

        var fileInfo = new FileInfo(sourceFilePath);
        long fileSize = fileInfo.Length;

        if (maxSizeBytes > 0 && fileSize > maxSizeBytes)
        {
            throw new InvalidOperationException(
                $"Размер файла ({AttachmentFileHelper.FormatFileSize(fileSize)}) превышает установленный лимит ({AttachmentFileHelper.FormatFileSize(maxSizeBytes)}).");
        }

        string sha256 = AttachmentFileHelper.ComputeSha256(sourceFilePath);
        string originalFileName = fileInfo.Name;
        string storedFileName = AttachmentFileHelper.GetSafeStoredFileName(sha256, originalFileName);
        string relativePath = Path.Combine("Attachments", storedFileName);
        string destFullPath = Path.Combine(AttachmentsDirectory, storedFileName);
        string contentType = AttachmentFileHelper.GetMimeType(originalFileName);

        // If managed file with identical hash and size already exists, reuse it
        if (File.Exists(destFullPath))
        {
            var destInfo = new FileInfo(destFullPath);
            if (destInfo.Length == fileSize)
            {
                return new AttachmentSaveResult(
                    originalFileName,
                    storedFileName,
                    relativePath,
                    contentType,
                    fileSize,
                    sha256,
                    destFullPath,
                    wasCreated: false);
            }
        }

        // Atomic copy through temporary file in the managed directory with cleanup on failure
        Directory.CreateDirectory(AttachmentsDirectory);
        string tempFilePath = Path.Combine(AttachmentsDirectory, $"{Guid.NewGuid():N}.tmp");

        try
        {
            using (var src = File.OpenRead(sourceFilePath))
            using (var dst = File.Create(tempFilePath))
            {
                src.CopyTo(dst);
            }

            File.Move(tempFilePath, destFullPath, overwrite: true);
        }
        catch (Exception ex)
        {
            if (File.Exists(tempFilePath))
            {
                try
                {
                    File.Delete(tempFilePath);
                }
                catch
                {
                    // Ignore temp cleanup error on failure
                }
            }

            ErrorLogService.Write("SaveAttachment", ex);
            throw;
        }

        return new AttachmentSaveResult(
            originalFileName,
            storedFileName,
            relativePath,
            contentType,
            fileSize,
            sha256,
            destFullPath,
            wasCreated: true);
    }

    public AttachmentSaveResult SaveFromBytes(byte[] content, string originalFileName, long maxSizeBytes)
    {
        if (content == null || content.Length == 0)
        {
            throw new InvalidOperationException("Пустые данные нельзя сохранить как вложение.");
        }

        if (maxSizeBytes > 0 && content.Length > maxSizeBytes)
        {
            throw new InvalidOperationException(
                $"Размер файла ({AttachmentFileHelper.FormatFileSize(content.Length)}) превышает установленный лимит ({AttachmentFileHelper.FormatFileSize(maxSizeBytes)}).");
        }

        string sha256 = AttachmentFileHelper.ComputeSha256(content);
        string safeOriginal = string.IsNullOrWhiteSpace(originalFileName) ? "file.bin" : Path.GetFileName(originalFileName);
        string storedFileName = AttachmentFileHelper.GetSafeStoredFileName(sha256, safeOriginal);
        string relativePath = Path.Combine("Attachments", storedFileName);
        string destFullPath = Path.Combine(AttachmentsDirectory, storedFileName);
        string contentType = AttachmentFileHelper.GetMimeType(safeOriginal);

        if (File.Exists(destFullPath))
        {
            var destInfo = new FileInfo(destFullPath);
            if (destInfo.Length == content.Length)
            {
                return new AttachmentSaveResult(safeOriginal, storedFileName, relativePath, contentType, content.Length, sha256, destFullPath, wasCreated: false);
            }
        }

        Directory.CreateDirectory(AttachmentsDirectory);
        string tempFilePath = Path.Combine(AttachmentsDirectory, $"{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(tempFilePath, content);
            File.Move(tempFilePath, destFullPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempFilePath))
            {
                try { File.Delete(tempFilePath); } catch { /* ignore */ }
            }

            throw;
        }

        return new AttachmentSaveResult(safeOriginal, storedFileName, relativePath, contentType, content.Length, sha256, destFullPath, wasCreated: true);
    }

    public string GetFullPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("Относительный путь не может быть пустым.", nameof(relativePath));
        }

        string normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

        string resolved;
        if (normalized.StartsWith("Attachments" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            resolved = Path.Combine(BaseDirectory, normalized);
        }
        else
        {
            resolved = Path.Combine(AttachmentsDirectory, normalized);
        }

        string fullPath = Path.GetFullPath(resolved);
        string rootWithSep = AttachmentsDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Попытка выхода за пределы управляемого каталога вложений (directory traversal).");
        }

        return fullPath;
    }

    public bool FileExists(string relativePath)
    {
        try
        {
            var fullPath = GetFullPath(relativePath);
            return File.Exists(fullPath);
        }
        catch
        {
            return false;
        }
    }

    public bool DeleteManagedFileIfUnreferenced(QuickNotesDbContext db, string storedFileName)
    {
        if (string.IsNullOrWhiteSpace(storedFileName)) return false;

        // Ensure safe filename without directory traversal
        string safeFileName = Path.GetFileName(storedFileName);

        // Check if any other attachment record references this stored file
        bool isReferenced = db.NoteAttachments.Any(a => a.StoredFileName == safeFileName);
        if (isReferenced)
        {
            return false;
        }

        try
        {
            string fullPath = Path.Combine(AttachmentsDirectory, safeFileName);
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
                return true;
            }
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("DeleteManagedFileIfUnreferenced", ex);
        }

        return false;
    }

    public void CleanupUnreferencedFiles(QuickNotesDbContext db, IEnumerable<string> storedFileNames)
    {
        foreach (var name in storedFileNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            DeleteManagedFileIfUnreferenced(db, name);
        }
    }
}
