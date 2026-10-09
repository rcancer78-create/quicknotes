using System.Collections.Generic;
using QuickNotes.App.Data;

namespace QuickNotes.App.Services;

public class AttachmentSaveResult
{
    public string OriginalFileName { get; }
    public string StoredFileName { get; }
    public string RelativePath { get; }
    public string ContentType { get; }
    public long Size { get; }
    public string Sha256 { get; }
    public string FullPath { get; }

    /// <summary>
    /// True when this call created a new managed file. False when an existing
    /// content-addressed destination was reused and must not be deleted on rollback.
    /// </summary>
    public bool WasCreated { get; }

    public AttachmentSaveResult(
        string originalFileName,
        string storedFileName,
        string relativePath,
        string contentType,
        long size,
        string sha256,
        string fullPath,
        bool wasCreated = true)
    {
        OriginalFileName = originalFileName;
        StoredFileName = storedFileName;
        RelativePath = relativePath;
        ContentType = contentType;
        Size = size;
        Sha256 = sha256;
        FullPath = fullPath;
        WasCreated = wasCreated;
    }
}

public interface IAttachmentStorageService
{
    string BaseDirectory { get; }
    string AttachmentsDirectory { get; }

    AttachmentSaveResult SaveAttachment(string sourceFilePath, long maxSizeBytes);
    AttachmentSaveResult SaveFromBytes(byte[] content, string originalFileName, long maxSizeBytes);
    string GetFullPath(string relativePath);
    bool FileExists(string relativePath);
    bool DeleteManagedFileIfUnreferenced(QuickNotesDbContext db, string storedFileName);
    void CleanupUnreferencedFiles(QuickNotesDbContext db, IEnumerable<string> storedFileNames);
}
