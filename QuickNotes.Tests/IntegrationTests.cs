using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public class IntegrationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly QuickNotesDbContext _context;
    private readonly SearchService _searchService = new();

    public IntegrationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<QuickNotesDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new QuickNotesDbContext(options);
        DbInitializer.Initialize(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public void Scenario2_ParentTagFilter_ReturnsNotesWithChildTags()
    {
        // IT (1) -> Database (2) -> Oracle (3)
        var it = new Tag { Name = "IT", ParentTagId = null };
        _context.Tags.Add(it);
        _context.SaveChanges();

        var db = new Tag { Name = "Database", ParentTagId = it.Id };
        _context.Tags.Add(db);
        _context.SaveChanges();

        var oracle = new Tag { Name = "Oracle", ParentTagId = db.Id };
        _context.Tags.Add(oracle);
        _context.SaveChanges();

        // Note physically only has Oracle assigned
        var note = new Note
        {
            Text = "Database connection string config for Oracle",
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };
        note.NoteTags.Add(new NoteTag
        {
            TagId = oracle.Id,
            Origin = TagOrigin.Auto,
            IsSuppressed = false
        });
        _context.Notes.Add(note);
        _context.SaveChanges();

        var allTags = _context.Tags.ToList();

        // 1. Filter by Oracle
        var notesOracle = _searchService.QueryNotes(_context, null, oracle.Id, allTags);
        Assert.Single(notesOracle);
        Assert.Equal(note.Id, notesOracle[0].Id);

        // 2. Filter by Database (parent of Oracle)
        var notesDb = _searchService.QueryNotes(_context, null, db.Id, allTags);
        Assert.Single(notesDb);
        Assert.Equal(note.Id, notesDb[0].Id);

        // 3. Filter by IT (grandparent of Oracle)
        var notesIt = _searchService.QueryNotes(_context, null, it.Id, allTags);
        Assert.Single(notesIt);
        Assert.Equal(note.Id, notesIt[0].Id);
    }

    [Fact]
    public void Scenario13_CombinedSearchAndTagFilter()
    {
        // Tags: Work and Personal
        var workTag = new Tag { Name = "Work" };
        var personalTag = new Tag { Name = "Personal" };
        _context.Tags.AddRange(workTag, personalTag);
        _context.SaveChanges();

        // Note 1: In Work, contains "Oracle"
        var note1 = new Note
        {
            Text = "Work project using Oracle 19c database",
            CreatedAt = DateTime.Now.AddMinutes(-5),
            UpdatedAt = DateTime.Now.AddMinutes(-5)
        };
        note1.NoteTags.Add(new NoteTag { TagId = workTag.Id, Origin = TagOrigin.Manual });

        // Note 2: In Personal, also contains "Oracle"
        var note2 = new Note
        {
            Text = "Learning Oracle Cloud free tier at home",
            CreatedAt = DateTime.Now.AddMinutes(-2),
            UpdatedAt = DateTime.Now.AddMinutes(-2)
        };
        note2.NoteTags.Add(new NoteTag { TagId = personalTag.Id, Origin = TagOrigin.Manual });

        // Note 3: In Work, does NOT contain "Oracle"
        var note3 = new Note
        {
            Text = "Work meeting with customer about frontend",
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };
        note3.NoteTags.Add(new NoteTag { TagId = workTag.Id, Origin = TagOrigin.Manual });

        _context.Notes.AddRange(note1, note2, note3);
        _context.SaveChanges();

        var allTags = _context.Tags.ToList();

        // Query: Search "Oracle", Tag "Work"
        var results = _searchService.QueryNotes(_context, "Oracle", workTag.Id, allTags);

        // Only Note 1 matches both conditions!
        Assert.Single(results);
        Assert.Equal(note1.Id, results[0].Id);
    }

    [Fact]
    public void DatabaseInitialization_CreatesDbAndTablesSuccessfully()
    {
        string tempDbPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"quicknotes_test_{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new QuickNotesDbContext(tempDbPath))
            {
                DbInitializer.Initialize(db);

                // Verify tables exist and can insert/query
                var tag = new Tag { Name = "TestTag" };
                db.Tags.Add(tag);
                db.SaveChanges();

                var note = new Note { Text = "Test Note with TestTag", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
                note.NoteTags.Add(new NoteTag { TagId = tag.Id, Origin = TagOrigin.Auto });
                db.Notes.Add(note);
                db.SaveChanges();

                Assert.True(db.Notes.Any(n => n.Text.Contains("Test Note")));
            }
        }
        finally
        {
            if (System.IO.File.Exists(tempDbPath))
            {
                try { System.IO.File.Delete(tempDbPath); } catch { }
            }
        }
    }
}
