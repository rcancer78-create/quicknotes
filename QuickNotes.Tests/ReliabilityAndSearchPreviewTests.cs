using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.ViewModels;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public class ReliabilityAndSearchPreviewTests
{
    [Fact]
    public void SearchPreview_SkipsShortWordsAndHighlightsTerm()
    {
        Assert.Equal("Oracle", SearchPreview.FirstTerm("tag:Work Oracle AND"));
        var snippet = SearchPreview.BuildSnippet(
            string.Join('\n', Enumerable.Range(1, 20).Select(i => $"line {i} Oracle problem {i}")),
            "Oracle",
            80,
            expanded: false);
        Assert.Contains("Oracle", snippet, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("…", snippet);

        var parts = SearchPreview.SplitHighlights("railway and Oracle DB", "oracle");
        Assert.Contains(parts, p => p.IsMatch && p.Text.Equals("Oracle", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(parts, p => !p.IsMatch && p.Text.Contains("railway"));
    }

    [Fact]
    public void ErrorLog_SanitizeTruncatesAndCollapsesWhitespace()
    {
        var longText = new string('x', 500);
        var cleaned = ErrorLogService.Sanitize("  a \n b  " + longText);
        Assert.DoesNotContain('\n', cleaned);
        Assert.True(cleaned.Length <= 401);
    }

    [Fact]
    public void IntegrityCheck_NewDatabaseIsOk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"qn-integrity-{Guid.NewGuid():N}.db");
        try
        {
            using var db = new QuickNotesDbContext(path);
            DbInitializer.Initialize(db);
            Assert.Equal("ok", DbInitializer.CheckIntegrity(db), ignoreCase: true);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void SchemaUpgrade_BacksUpExistingNotesBeforeChangingVersion()
    {
        var path = Path.Combine(Path.GetTempPath(), $"qn-upgrade-{Guid.NewGuid():N}.db");
        var backedUp = false;
        try
        {
            using (var connection = new SqliteConnection($"Data Source={path}"))
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
                    VALUES ('keep me', '2026-01-02 03:04:05', '2026-01-02 03:04:05');
                    """;
                command.ExecuteNonQuery();
            }

            using var context = new QuickNotesDbContext(path);
            DbInitializer.Initialize(context, () => backedUp = true);
            Assert.True(backedUp);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void PurgeOldTrash_RemovesOnlyExpiredDeletedNotes()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<QuickNotesDbContext>().UseSqlite(connection).Options;
        using var context = new QuickNotesDbContext(options);
        DbInitializer.Initialize(context);

        var now = DateTime.Now;
        context.Notes.AddRange(
            new Note { Text = "old trash", DeletedAt = now.AddDays(-40) },
            new Note { Text = "fresh trash", DeletedAt = now.AddDays(-5) },
            new Note { Text = "active" });
        context.SaveChanges();

        var removed = new SearchService().PurgeOldTrash(context, now.AddDays(-30));
        Assert.Equal(1, removed);
        Assert.Equal(2, context.Notes.Count());
        Assert.DoesNotContain(context.Notes, n => n.Text == "old trash");
    }

    [Fact]
    public void NoteEditor_HasUnsavedChangesAfterEdit()
    {
        using var vm = new NoteEditorViewModel(new TagDetectionService(), new List<Tag>(), initialText: "hello", draftJournalService: NoOpDraftJournalService.Instance);
        Assert.False(vm.HasUnsavedChanges);
        vm.Text = "hello world";
        Assert.True(vm.HasUnsavedChanges);
    }
}
