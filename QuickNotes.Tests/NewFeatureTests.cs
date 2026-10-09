using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.IO;
using System.Threading;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Services;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public sealed class NewFeatureTests
{
    [Fact]
    public void DatabaseInitialization_UpgradesLegacyNotesWithoutLosingData()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"quicknotes_legacy_{Guid.NewGuid():N}.db");
        try
        {
            using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE Notes (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Text TEXT NOT NULL,
                        CreatedAt TEXT NOT NULL,
                        UpdatedAt TEXT NOT NULL
                    );
                    INSERT INTO Notes (Text, CreatedAt, UpdatedAt)
                    VALUES ('legacy note', '2026-01-02 03:04:05', '2026-01-02 03:04:05');
                    """;
                command.ExecuteNonQuery();
            }

            using (var context = new QuickNotesDbContext(dbPath))
            {
                DbInitializer.Initialize(context);
            }

            using (var verify = new SqliteConnection($"Data Source={dbPath}"))
            {
                verify.Open();
                using var columnsCommand = verify.CreateCommand();
                columnsCommand.CommandText = "PRAGMA table_info(Notes);";
                using var reader = columnsCommand.ExecuteReader();
                var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (reader.Read()) columns.Add(reader.GetString(1));

                Assert.Contains("IsPinned", columns);
                Assert.Contains("IsFavorite", columns);
                Assert.Contains("IsInbox", columns);
                Assert.Contains("DeletedAt", columns);
                reader.Close();

                using var dataCommand = verify.CreateCommand();
                dataCommand.CommandText = "SELECT Text, IsPinned, IsFavorite, IsInbox, DeletedAt FROM Notes WHERE Id = 1;";
                using var data = dataCommand.ExecuteReader();
                Assert.True(data.Read());
                Assert.Equal("legacy note", data.GetString(0));
                Assert.Equal(0L, data.GetInt64(1));
                Assert.Equal(0L, data.GetInt64(2));
                Assert.Equal(0L, data.GetInt64(3));
                Assert.True(data.IsDBNull(4));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void BackupService_CreatesUniqueConsistentCopiesAndEnforcesRetention()
    {
        var root = Path.Combine(Path.GetTempPath(), $"quicknotes_backup_test_{Guid.NewGuid():N}");
        var dbPath = Path.Combine(root, "quicknotes.db");
        var backupDirectory = Path.Combine(root, "backups");
        Directory.CreateDirectory(root);

        try
        {
            using (var context = new QuickNotesDbContext(dbPath))
            {
                DbInitializer.Initialize(context);
                context.Notes.Add(new Note
                {
                    Text = "backup payload",
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                });
                context.SaveChanges();
            }

            var service = new BackupService(dbPath, backupDirectory);
            var first = service.CreateBackup(2);
            var second = service.CreateBackup(2);
            var third = service.CreateBackup(2);

            Assert.NotEqual(first, second);
            Assert.NotEqual(second, third);
            Assert.Equal(2, service.GetExistingBackups().Count);
            Assert.True(File.Exists(third));

            using (var backup = new QuickNotesDbContext(third))
            {
                Assert.Equal("backup payload", backup.Notes.Single().Text);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void SearchSections_ExcludeTrashAndKeepPinnedNotesFirst()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<QuickNotesDbContext>().UseSqlite(connection).Options;
        using var context = new QuickNotesDbContext(options);
        DbInitializer.Initialize(context);

        var now = DateTime.Now;
        context.Notes.AddRange(
            new Note { Text = "ordinary", CreatedAt = now, UpdatedAt = now },
            new Note { Text = "pinned", CreatedAt = now.AddDays(-2), UpdatedAt = now.AddDays(-2), IsPinned = true },
            new Note { Text = "inbox", CreatedAt = now.AddMinutes(-1), UpdatedAt = now.AddMinutes(-1), IsInbox = true },
            new Note { Text = "favorite", CreatedAt = now.AddMinutes(-2), UpdatedAt = now.AddMinutes(-2), IsFavorite = true },
            new Note { Text = "deleted", CreatedAt = now, UpdatedAt = now, DeletedAt = now });
        context.SaveChanges();

        var service = new SearchService();
        var all = service.QueryNotes(context, null, null, [], NavigationSection.All);
        var inbox = service.QueryNotes(context, null, null, [], NavigationSection.Inbox);
        var favorites = service.QueryNotes(context, null, null, [], NavigationSection.Favorites);
        var trash = service.QueryNotes(context, null, null, [], NavigationSection.Trash);

        Assert.Equal("pinned", all[0].Text);
        Assert.DoesNotContain(all, note => note.Text == "deleted");
        Assert.Collection(inbox, note => Assert.Equal("inbox", note.Text));
        Assert.Collection(favorites, note => Assert.Equal("favorite", note.Text));
        Assert.Collection(trash, note => Assert.Equal("deleted", note.Text));
    }

    [Fact]
    public void SingleInstance_SecondarySignalsPrimary()
    {
        var id = $"test_{Guid.NewGuid():N}";
        using var activated = new ManualResetEventSlim(false);
        using var primary = new SingleInstanceService(id);
        Assert.True(primary.IsPrimary);
        primary.StartListening(() => activated.Set());

        using var secondary = new SingleInstanceService(id);
        Assert.False(secondary.IsPrimary);
        secondary.SignalExistingInstance();

        Assert.True(activated.Wait(TimeSpan.FromSeconds(3)));
    }
}
