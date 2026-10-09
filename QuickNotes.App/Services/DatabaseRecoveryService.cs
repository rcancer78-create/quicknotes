using System;
using System.IO;
using Microsoft.Data.Sqlite;
using QuickNotes.App.Data;

namespace QuickNotes.App.Services;

public record DatabaseRecoveryResult(
    bool Success,
    string? RestoredBackupPath,
    string? PreservedCorruptPath,
    string? ErrorMessage);

public class DatabaseRecoveryService
{
    private readonly string _dbPath;
    private readonly BackupService _backupService;

    public DatabaseRecoveryService(string? dbPath = null, string? backupDirectory = null)
    {
        _dbPath = dbPath ?? QuickNotesDbContext.GetDefaultDbPath();
        _backupService = new BackupService(_dbPath, backupDirectory);
    }

    public DatabaseRecoveryService(string dbPath, BackupService backupService)
    {
        _dbPath = dbPath;
        _backupService = backupService;
    }

    /// <summary>
    /// Checks if the database file exists and is corrupted.
    /// Returns true if corrupt, with the technical integrity message.
    /// </summary>
    public bool IsDatabaseCorrupt(out string integrityMessage)
    {
        integrityMessage = string.Empty;
        if (!File.Exists(_dbPath))
            return false;

        var fileInfo = new FileInfo(_dbPath);
        if (fileInfo.Length == 0)
        {
            integrityMessage = "Файл базы данных имеет нулевой размер.";
            return true;
        }

        try
        {
            var csb = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            };

            using var connection = new SqliteConnection(csb.ToString());
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check;";
            var res = cmd.ExecuteScalar()?.ToString();
            if (string.Equals(res, "ok", StringComparison.OrdinalIgnoreCase))
                return false;

            integrityMessage = res ?? "integrity_check returned empty result";
            return true;
        }
        catch (Exception ex)
        {
            integrityMessage = $"{ex.GetType().Name}: {ex.Message}";
            return true;
        }
    }

    /// <summary>
    /// Checks integrity of a specific backup file using PRAGMA integrity_check.
    /// </summary>
    public bool CheckBackupIntegrity(string backupPath, out string integrityMessage)
    {
        integrityMessage = string.Empty;
        if (!File.Exists(backupPath))
        {
            integrityMessage = "Файл резервной копии не найден.";
            return false;
        }

        var fileInfo = new FileInfo(backupPath);
        if (fileInfo.Length == 0)
        {
            integrityMessage = "Файл резервной копии имеет нулевой размер.";
            return false;
        }

        try
        {
            var csb = new SqliteConnectionStringBuilder
            {
                DataSource = backupPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            };

            using var connection = new SqliteConnection(csb.ToString());
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check;";
            var res = cmd.ExecuteScalar()?.ToString();
            if (string.Equals(res, "ok", StringComparison.OrdinalIgnoreCase))
                return true;

            integrityMessage = res ?? "integrity_check returned empty result";
            return false;
        }
        catch (Exception ex)
        {
            integrityMessage = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Finds the most recent healthy backup copy.
    /// </summary>
    public FileInfo? FindLatestHealthyBackup(out string? diagnostic)
    {
        diagnostic = null;
        var existing = _backupService.GetExistingBackups();
        if (existing.Count == 0)
        {
            diagnostic = "Резервные копии не найдены.";
            return null;
        }

        foreach (var backup in existing)
        {
            if (CheckBackupIntegrity(backup.FullName, out _))
            {
                return backup;
            }
        }

        diagnostic = "Все найденные резервные копии повреждены.";
        return null;
    }

    /// <summary>
    /// Preserves the corrupted database file under a separate unique name without deleting it.
    /// </summary>
    public string PreserveCorruptedDatabase()
    {
        if (!File.Exists(_dbPath))
            return string.Empty;

        var dir = Path.GetDirectoryName(_dbPath) ?? "";
        string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        string baseName = Path.GetFileNameWithoutExtension(_dbPath);
        string preservedPath = Path.Combine(dir, $"{baseName}_corrupted_{timestamp}_{Guid.NewGuid():N}.db");

        File.Copy(_dbPath, preservedPath, overwrite: true);
        return preservedPath;
    }

    /// <summary>
    /// Performs safe recovery from a validated backup copy.
    /// </summary>
    public DatabaseRecoveryResult PerformRecovery(string backupPath)
    {
        if (!File.Exists(backupPath))
        {
            return new DatabaseRecoveryResult(false, null, null, "Файл резервной копии не существует.");
        }

        // 1. Verify backup integrity before doing anything
        if (!CheckBackupIntegrity(backupPath, out var backupError))
        {
            return new DatabaseRecoveryResult(false, backupPath, null, $"Выбранная резервная копия повреждена: {backupError}");
        }

        // 2. Preserve corrupt file under a separate name
        string preservedPath;
        try
        {
            preservedPath = PreserveCorruptedDatabase();
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("Recovery.Preserve", ex);
            return new DatabaseRecoveryResult(false, backupPath, null, $"Не удалось сохранить повреждённый файл: {ex.Message}");
        }

        // 3. Staging and replacement
        var tempStaging = _dbPath + ".recovery_staging.tmp";
        try
        {
            File.Copy(backupPath, tempStaging, overwrite: true);

            // Double check staged copy integrity
            if (!CheckBackupIntegrity(tempStaging, out var stagingError))
            {
                try { if (File.Exists(tempStaging)) File.Delete(tempStaging); }
                catch (Exception cleanupEx) { System.Diagnostics.Debug.WriteLine($"Cleanup staging failed: {cleanupEx.Message}"); }

                return new DatabaseRecoveryResult(false, backupPath, preservedPath, $"Копия для восстановления повреждена: {stagingError}");
            }

            // The staging file is in the same directory, so Windows can replace the
            // destination atomically. If replacement fails, the corrupt original
            // remains at its path and its preserved copy remains available as well.
            File.Replace(tempStaging, _dbPath, destinationBackupFileName: null);

            ErrorLogService.Write("Recovery.Success", $"Restored from {Path.GetFileName(backupPath)}, preserved corrupt db as {Path.GetFileName(preservedPath)}");
            return new DatabaseRecoveryResult(true, backupPath, preservedPath, null);
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("Recovery.Perform", ex);
            try
            {
                if (File.Exists(tempStaging))
                    File.Delete(tempStaging);
            }
            catch (Exception cleanupEx)
            {
                System.Diagnostics.Debug.WriteLine($"Cleanup staging on failure: {cleanupEx.Message}");
            }

            return new DatabaseRecoveryResult(false, backupPath, preservedPath, $"Ошибка при замене файла базы данных: {ex.Message}");
        }
    }
}
