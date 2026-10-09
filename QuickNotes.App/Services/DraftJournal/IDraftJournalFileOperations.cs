using System;
using System.IO;

namespace QuickNotes.App.Services.DraftJournal;

/// <summary>
/// Abstraction over journal file operations enabling Windows-safe atomic replacement
/// and deterministic fault testing.
/// </summary>
public interface IDraftJournalFileOperations
{
    bool FileExists(string path);
    void MoveFile(string source, string destination);
    void ReplaceFileSafely(string tempFile, string targetFile);
    void DeleteFile(string path);
}

/// <summary>
/// Windows-safe implementation that replaces the destination file atomically
/// without ever deleting the destination before the replacement is durable.
/// </summary>
public class DefaultDraftJournalFileOperations : IDraftJournalFileOperations
{
    public bool FileExists(string path) => File.Exists(path);

    public void MoveFile(string source, string destination) => File.Move(source, destination);

    public void ReplaceFileSafely(string tempFile, string targetFile)
    {
        if (!File.Exists(targetFile))
        {
            File.Move(tempFile, targetFile);
            return;
        }

        string backupFile = targetFile + ".bak." + Guid.NewGuid().ToString("N");
        try
        {
            // Windows ReplaceFile creates a backup of target and places temp in target's place.
            File.Replace(tempFile, targetFile, backupFile, ignoreMetadataErrors: true);
        }
        catch
        {
            // If backup was created but target was somehow displaced, restore backup
            if (!File.Exists(targetFile) && File.Exists(backupFile))
            {
                try { File.Move(backupFile, targetFile); } catch { }
            }

            // Fallback: MoveFileEx with MOVEFILE_REPLACE_EXISTING in .NET 8 (overwrite: true)
            // Never explicitly deletes targetFile first.
            File.Move(tempFile, targetFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(backupFile))
            {
                try { File.Delete(backupFile); } catch { }
            }
        }
    }

    public void DeleteFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
