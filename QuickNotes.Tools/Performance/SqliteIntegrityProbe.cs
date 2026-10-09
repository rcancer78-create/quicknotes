using System;
using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;
using QuickNotes.App.Data;

namespace QuickNotes.Tools.Performance;

public static class SqliteIntegrityProbe
{
    public static string Check(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("Database path cannot be empty.", nameof(databasePath));
        }

        string full = Path.GetFullPath(databasePath);
        QuickNotesDbContext.ValidateIsolatedProfilePath(Path.GetDirectoryName(full)!);

        if (!File.Exists(full))
        {
            throw new FileNotFoundException("Synthetic database file was not found.", full);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = full,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        };

        using var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        using var reader = command.ExecuteReader();
        var sb = new StringBuilder();
        while (reader.Read())
        {
            if (sb.Length > 0)
            {
                sb.AppendLine();
            }

            sb.Append(reader.GetValue(0)?.ToString() ?? string.Empty);
        }

        return sb.ToString();
    }

    public static void RequireOk(string databasePath)
    {
        string result = Check(databasePath);
        if (!IsOk(result))
        {
            throw new InvalidOperationException(
                "PRAGMA integrity_check did not return ok (" + result + "). Measurement stopped; no repair was attempted and this tool does not classify the file as a corrupted user database.");
        }
    }

    public static bool IsOk(string pragmaResult)
    {
        if (string.IsNullOrWhiteSpace(pragmaResult))
        {
            return false;
        }

        string[] lines = pragmaResult.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            return false;
        }

        foreach (string line in lines)
        {
            if (!string.Equals(line.Trim(), "ok", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    public static void CheckpointWal(string databasePath)
    {
        string full = Path.GetFullPath(databasePath);
        QuickNotesDbContext.ValidateIsolatedProfilePath(Path.GetDirectoryName(full)!);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = full,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        };

        using var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        command.ExecuteNonQuery();
    }
}
