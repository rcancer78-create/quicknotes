using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using QuickNotes.App.Data;

namespace QuickNotes.App.Services;

public class BackupService
{
    public const int DefaultMaxCopies = 7;

    public string DbPath { get; }
    public string BackupDirectory { get; }

    public BackupService(string? dbPath = null, string? backupDirectory = null)
    {
        DbPath = dbPath ?? QuickNotesDbContext.GetDefaultDbPath();

        if (!string.IsNullOrWhiteSpace(backupDirectory))
        {
            BackupDirectory = backupDirectory;
        }
        else
        {
            BackupDirectory = Path.Combine(QuickNotes.App.Data.QuickNotesDbContext.GetDefaultProfileDirectory(), "Backups");
        }
    }

    /// <summary>
    /// Creates a consistent backup copy of the SQLite database using SQLite Online Backup API.
    /// Retains up to maxCopies most recent backup files.
    /// </summary>
    /// <param name="maxCopies">Maximum number of backup copies to keep (default 7).</param>
    /// <returns>Absolute path to the created backup file.</returns>
    public virtual string CreateBackup(int maxCopies = DefaultMaxCopies, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(DbPath))
        {
            throw new FileNotFoundException($"Файл базы данных не найден: {DbPath}");
        }

        Directory.CreateDirectory(BackupDirectory);

        string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
        string backupFileName = $"quicknotes_backup_{timestamp}_{Guid.NewGuid():N}.db";
        string backupFilePath = Path.Combine(BackupDirectory, backupFileName);

        // SQLite Online Backup API ensures consistent point-in-time snapshot
        var sourceConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        var destinationConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = backupFilePath,
            Pooling = false
        }.ToString();

        try
        {
            using (var sourceConnection = new SqliteConnection(sourceConnectionString))
            using (var destinationConnection = new SqliteConnection(destinationConnectionString))
            {
                sourceConnection.Open();
                destinationConnection.Open();
                sourceConnection.BackupDatabase(destinationConnection);
            }

            cancellationToken.ThrowIfCancellationRequested();
            CleanupOldBackups(maxCopies);

            return backupFilePath;
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (File.Exists(backupFilePath))
                {
                    File.Delete(backupFilePath);
                }
            }
            catch
            {
            }

            throw;
        }
    }

    public List<FileInfo> GetExistingBackups()
    {
        if (!Directory.Exists(BackupDirectory))
        {
            return new List<FileInfo>();
        }

        var dirInfo = new DirectoryInfo(BackupDirectory);
        return dirInfo.GetFiles("quicknotes_backup_*.db")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();
    }

    private void CleanupOldBackups(int maxCopies)
    {
        if (maxCopies <= 0) return;

        var backups = GetExistingBackups();
        if (backups.Count > maxCopies)
        {
            foreach (var oldBackup in backups.Skip(maxCopies))
            {
                try
                {
                    oldBackup.Delete();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Не удалось удалить старую резервную копию '{oldBackup.FullName}': {ex.Message}");
                }
            }
        }
    }
}
