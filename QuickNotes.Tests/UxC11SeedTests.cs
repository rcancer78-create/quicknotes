using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Services;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public sealed class UxC11SeedTests
{
    [Fact]
    public void SeedCards_ExecutesSqliteQueryAndDoesNotDuplicateFixtures()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<QuickNotesDbContext>().UseSqlite(connection).Options;
        using var db = new QuickNotesDbContext(options);
        db.Database.EnsureCreated();

        UxC11UiSmokeRunner.SeedCards(db);
        var first = db.Notes.OrderBy(n => n.Id).Select(n => new { n.Id, n.Title, n.Text }).ToArray();
        Assert.Equal(5, first.Length);
        Assert.Contains(first, n => n.Title == UxC11UiSmokeRunner.LocalTitle);
        Assert.Contains(first, n => n.Title == UxC11UiSmokeRunner.PendingTitle);
        Assert.Contains(first, n => n.Title == UxC11UiSmokeRunner.CloudTitle);
        Assert.Contains(first, n => n.Title == UxC11UiSmokeRunner.ConflictTitle);
        Assert.Contains(first, n => n.Title == UxC11UiSmokeRunner.ErrorTitle);

        UxC11UiSmokeRunner.SeedCards(db);
        Assert.Equal(first, db.Notes.OrderBy(n => n.Id).Select(n => new { n.Id, n.Title, n.Text }).ToArray());
    }
}
