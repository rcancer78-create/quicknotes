using System;
using System.IO;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;

namespace QuickNotes.Tests;

/// <summary>
/// File-backed SQLite in tests must not use the process-wide connection pool.
/// Pooled handles to deleted temp DBs have aborted testhost later in the run
/// (observed mid-flight on SyncEngineTests.Scenario19).
/// </summary>
internal static class SqliteTestUtil
{
    public static string ConnectionString(string dbPath)
        => $"Data Source={dbPath};Pooling=False";

    public static QuickNotesDbContext CreateContext(string dbPath)
    {
        var options = new DbContextOptionsBuilder<QuickNotesDbContext>()
            .UseSqlite(ConnectionString(dbPath))
            .Options;
        return new QuickNotesDbContext(options, dbPath);
    }

    public static void ReleasePools()
    {
        // File-backed test DBs use Pooling=False. Do not call ClearAllPools():
        // it is process-wide and can checkpoint/touch the live profile database.
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    public static void TryDeleteFileAndSiblings(string dbPath)
    {
        foreach (var path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm", dbPath + "-journal" })
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
                // Best-effort: file may still be locked by a non-test handle.
            }
        }
    }

    public static void TryDeleteDirectory(string directory)
    {
        ReleasePools();
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup of temp test trees.
        }
    }
}
