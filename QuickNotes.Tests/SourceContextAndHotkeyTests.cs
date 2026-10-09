using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.ViewModels;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.UiSensitive)]
public class SourceContextAndHotkeyTests
{
    private static void RunInSta(Action action) => StaTestHarness.Run(action, TimeSpan.FromSeconds(15));

    [Fact]
    public void DatabaseInitialization_Version2Upgrade_AddsSourceMetadataAndPreservesData()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"quicknotes_v2_migration_{Guid.NewGuid():N}.db");
        try
        {
            // 1. Create legacy database with v1 schema and existing note
            using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE Notes (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Text TEXT NOT NULL,
                        CreatedAt TEXT NOT NULL,
                        UpdatedAt TEXT NOT NULL,
                        IsPinned INTEGER NOT NULL DEFAULT 0,
                        IsFavorite INTEGER NOT NULL DEFAULT 0,
                        IsInbox INTEGER NOT NULL DEFAULT 0,
                        DeletedAt TEXT NULL
                    );
                    INSERT INTO Notes (Text, CreatedAt, UpdatedAt, IsPinned, IsFavorite, IsInbox, DeletedAt)
                    VALUES ('pre-v2 existing note', '2026-02-01 10:00:00', '2026-02-01 10:00:00', 1, 0, 0, NULL);
                    PRAGMA user_version = 1;
                    """;
                command.ExecuteNonQuery();
            }

            // 2. Initialize with DbInitializer
            using (var context = new QuickNotesDbContext(dbPath))
            {
                DbInitializer.Initialize(context);
            }

            // 3. Verify columns, user_version, and existing data
            using (var verify = new SqliteConnection($"Data Source={dbPath}"))
            {
                verify.Open();

                long version = 0;
                using (var versionCmd = verify.CreateCommand())
                {
                    versionCmd.CommandText = "PRAGMA user_version;";
                    version = Convert.ToInt64(versionCmd.ExecuteScalar());
                }
                Assert.Equal(DbInitializer.CurrentSchemaVersion, version);

                var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var colCmd = verify.CreateCommand())
                {
                    colCmd.CommandText = "PRAGMA table_info(Notes);";
                    using var reader = colCmd.ExecuteReader();
                    while (reader.Read())
                    {
                        columns.Add(reader.GetString(1));
                    }
                }

                Assert.Contains("SourceProcessName", columns);
                Assert.Contains("SourceWindowTitle", columns);
                Assert.Contains("SourceUrl", columns);
                Assert.Contains("CapturedAt", columns);

                // Verify existing row was not lost
                using (var dataCmd = verify.CreateCommand())
                {
                    dataCmd.CommandText = "SELECT Text, IsPinned, SourceProcessName, SourceWindowTitle, SourceUrl, CapturedAt FROM Notes WHERE Id = 1;";
                    using var data = dataCmd.ExecuteReader();
                    Assert.True(data.Read());
                    Assert.Equal("pre-v2 existing note", data.GetString(0));
                    Assert.Equal(1L, data.GetInt64(1));
                    Assert.True(data.IsDBNull(2)); // SourceProcessName is null
                    Assert.True(data.IsDBNull(3)); // SourceWindowTitle is null
                    Assert.True(data.IsDBNull(4)); // SourceUrl is null
                    Assert.True(data.IsDBNull(5)); // CapturedAt is null
                }
            }

            // 4. Insert note with new fields via DbContext and query back
            using (var context = new QuickNotesDbContext(dbPath))
            {
                var now = DateTime.Now;
                var capturedNote = new Note
                {
                    Text = "captured note with source",
                    CreatedAt = now,
                    UpdatedAt = now,
                    CapturedAt = now.AddSeconds(-5),
                    SourceProcessName = "chrome",
                    SourceWindowTitle = "GitHub - PR #42",
                    SourceUrl = "https://github.com/dotnet/wpf/pull/42",
                    IsInbox = true
                };
                context.Notes.Add(capturedNote);
                context.SaveChanges();

                var loaded = context.Notes.Single(n => n.Id == capturedNote.Id);
                Assert.Equal("chrome", loaded.SourceProcessName);
                Assert.Equal("chrome", loaded.SourceAppName); // Alias check
                Assert.Equal("GitHub - PR #42", loaded.SourceWindowTitle);
                Assert.Equal("https://github.com/dotnet/wpf/pull/42", loaded.SourceUrl);
                Assert.Equal("https://github.com/dotnet/wpf/pull/42", loaded.Url); // Alias check
                Assert.NotNull(loaded.CapturedAt);
                Assert.True(loaded.IsInbox);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void HotkeyValidation_ValidatesModifiersKeysAndDetectsConflicts()
    {
        // 1. Valid settings (defaults)
        var valid = new AppSettings
        {
            HotkeyCtrl = true,
            HotkeyShift = true,
            HotkeyAlt = false,
            HotkeyWin = false,
            HotkeyKey = "Space",

            InstantHotkeyCtrl = true,
            InstantHotkeyShift = false,
            InstantHotkeyAlt = true,
            InstantHotkeyWin = false,
            InstantHotkeyKey = "Space"
        };
        Assert.True(GlobalHotkeyService.ValidateSettings(valid, out var errValid));
        Assert.Null(errValid);

        // 2. Editor hotkey missing all modifiers
        var noModEditor = new AppSettings
        {
            HotkeyCtrl = false,
            HotkeyShift = false,
            HotkeyAlt = false,
            HotkeyWin = false,
            HotkeyKey = "Space",
            InstantHotkeyCtrl = true,
            InstantHotkeyKey = "Space"
        };
        Assert.False(GlobalHotkeyService.ValidateSettings(noModEditor, out var errNoMod));
        Assert.NotNull(errNoMod);
        Assert.Contains("модификатор", errNoMod, StringComparison.OrdinalIgnoreCase);

        // 3. Instant hotkey missing all modifiers
        var noModInstant = new AppSettings
        {
            HotkeyCtrl = true,
            HotkeyKey = "Space",
            InstantHotkeyCtrl = false,
            InstantHotkeyShift = false,
            InstantHotkeyAlt = false,
            InstantHotkeyWin = false,
            InstantHotkeyKey = "Space"
        };
        Assert.False(GlobalHotkeyService.ValidateSettings(noModInstant, out var errNoModInstant));
        Assert.NotNull(errNoModInstant);
        Assert.Contains("модификатор", errNoModInstant, StringComparison.OrdinalIgnoreCase);

        // 4. Conflicting identical hotkeys
        var conflict = new AppSettings
        {
            HotkeyCtrl = true,
            HotkeyShift = false,
            HotkeyAlt = true,
            HotkeyWin = false,
            HotkeyKey = "F11",
            InstantHotkeyCtrl = true,
            InstantHotkeyShift = false,
            InstantHotkeyAlt = true,
            InstantHotkeyWin = false,
            InstantHotkeyKey = "F11"
        };
        Assert.False(GlobalHotkeyService.ValidateSettings(conflict, out var errConflict));
        Assert.NotNull(errConflict);
        Assert.Contains("не должны совпадать", errConflict, StringComparison.OrdinalIgnoreCase);

        // 5. Invalid key name
        var invalidKey = new AppSettings
        {
            HotkeyCtrl = true,
            HotkeyKey = "InvalidKeyNameXYZ",
            InstantHotkeyCtrl = true,
            InstantHotkeyKey = "Space"
        };
        Assert.False(GlobalHotkeyService.ValidateSettings(invalidKey, out var errKey));
        Assert.NotNull(errKey);
    }

    [Fact]
    public void GlobalHotkeyService_RegistrationAndRollback_PreservesWorkingHotkeysOnFailure()
    {
        RunInSta(() =>
        {
            var registeredHotkeys = new HashSet<int>();
            int callCount = 0;
            bool failInstantSave = false;

            GlobalHotkeyService.RegisterHotKeyDelegate mockRegister = (hWnd, id, mod, vk) =>
            {
                callCount++;
                if (failInstantSave && vk == (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(System.Windows.Input.Key.B))
                {
                    return false; // Simulate another app holding this key
                }
                registeredHotkeys.Add(id);
                return true;
            };

            GlobalHotkeyService.UnregisterHotKeyDelegate mockUnregister = (hWnd, id) =>
            {
                registeredHotkeys.Remove(id);
                return true;
            };

            using var service = new GlobalHotkeyService(mockRegister, mockUnregister, testHwnd: (IntPtr)100);

            var settingsA = new AppSettings
            {
                HotkeyCtrl = true,
                HotkeyShift = true,
                HotkeyKey = "Space",
                InstantHotkeyCtrl = true,
                InstantHotkeyAlt = true,
                InstantHotkeyKey = "Space"
            };

            // 1. Register working hotkeys
            bool success = service.Register(settingsA, out var error);
            Assert.True(success);
            Assert.Null(error);
            Assert.True(service.IsEditorRegistered);
            Assert.True(service.IsInstantSaveRegistered);

            // 2. Try registering a new configuration where instant save fails
            failInstantSave = true;
            var settingsB = new AppSettings
            {
                HotkeyCtrl = true,
                HotkeyShift = true,
                HotkeyKey = "A",
                InstantHotkeyCtrl = true,
                InstantHotkeyAlt = true,
                InstantHotkeyKey = "B" // will fail in mock
            };

            bool successB = service.Register(settingsB, out var errorB);
            Assert.False(successB);
            Assert.NotNull(errorB);
            Assert.Contains("мгновенного сохранения", errorB, StringComparison.OrdinalIgnoreCase);

            // 3. Verify rollback restored working hotkeys
            Assert.True(service.IsEditorRegistered);
            Assert.True(service.IsInstantSaveRegistered);
        });
    }

    [Theory]
    [InlineData("chrome", "GitHub - PR", "https://github.com", "chrome • GitHub - PR • https://github.com")]
    [InlineData("notepad", "document.txt", null, "notepad • document.txt")]
    [InlineData("notepad", "notepad", null, "notepad")]
    [InlineData("notepad", "Notepad", "", "notepad")]
    [InlineData(null, "document.txt", null, "document.txt")]
    [InlineData("chrome", null, "https://google.com", "chrome • https://google.com")]
    [InlineData(null, null, "https://github.com", "https://github.com")]
    [InlineData(null, null, null, "")]
    [InlineData("   ", "  ", "", "")]
    public void NoteSourceContext_FormatCompactSource_FormatsCleanlyWithoutEmptyParts(
        string? process, string? title, string? url, string expected)
    {
        string formatted = NoteSourceContext.FormatCompactSource(process, title, url);
        Assert.Equal(expected, formatted);
    }

    [Theory]
    [InlineData("https://github.com/dotnet/wpf", "https://github.com/dotnet/wpf")]
    [InlineData("http://localhost:5000/api", "http://localhost:5000/api")]
    [InlineData("github.com/dotnet/wpf", "https://github.com/dotnet/wpf")]
    [InlineData("google.com", "https://google.com/")]
    [InlineData("https://ru.wikipedia.org/wiki/WPF", "https://ru.wikipedia.org/wiki/WPF")]
    [InlineData("hello world", null)]
    [InlineData("var x = 1;", null)]
    [InlineData("line1\nline2", null)]
    [InlineData("with space.com", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NoteSourceContext_SanitizeUrl_FiltersAndNormalizesUrls(string? raw, string? expected)
    {
        string? sanitized = NoteSourceContext.SanitizeUrl(raw);
        Assert.Equal(expected, sanitized);
    }

    [Fact]
    public void NoteCardViewModel_ExposesSourceTextAndCapturedDate()
    {
        var now = DateTime.Now;
        var note = new Note
        {
            Id = 42,
            Text = "Sample note",
            CreatedAt = now,
            UpdatedAt = now,
            CapturedAt = now.AddMinutes(-1),
            SourceProcessName = "msedge",
            SourceWindowTitle = "Microsoft Edge",
            SourceUrl = "https://learn.microsoft.com"
        };

        var card = new NoteCardViewModel(note);

        Assert.True(card.HasSourceText);
        Assert.Equal("msedge • Microsoft Edge • https://learn.microsoft.com", card.SourceText);
        Assert.NotNull(card.CapturedAtText);
        Assert.Contains(now.AddMinutes(-1).ToString("dd.MM.yyyy"), card.CapturedAtText);

        // Note without source
        var emptyNote = new Note
        {
            Id = 43,
            Text = "Manual note",
            CreatedAt = now,
            UpdatedAt = now
        };
        var emptyCard = new NoteCardViewModel(emptyNote);
        Assert.False(emptyCard.HasSourceText);
        Assert.Equal(string.Empty, emptyCard.SourceText);
        Assert.Null(emptyCard.CapturedAtText);
    }

    [Fact]
    public void NoteEditorViewModel_PreservesAndAppliesSourceMetadata()
    {
        var now = DateTime.Now;
        var tagService = new TagDetectionService();
        var allTags = new List<Tag>();

        // 1. Initialized with captured metadata
        using var vmNew = new NoteEditorViewModel(
            tagService,
            allTags,
            existingNote: null,
            initialText: "Captured text",
            initialAutoTags: null,
            sourceProcessName: "devenv",
            sourceWindowTitle: "QuickNotes - Visual Studio",
            sourceUrl: null,
            capturedAt: now,
            draftJournalService: NoOpDraftJournalService.Instance);

        Assert.True(vmNew.HasSourceText);
        Assert.Equal("devenv • QuickNotes - Visual Studio", vmNew.SourceText);
        Assert.NotNull(vmNew.CapturedAtText);

        var targetNote = new Note();
        vmNew.ApplyToNote(targetNote);
        Assert.Equal("devenv", targetNote.SourceProcessName);
        Assert.Equal("QuickNotes - Visual Studio", targetNote.SourceWindowTitle);
        Assert.Null(targetNote.SourceUrl);
        Assert.Equal(now, targetNote.CapturedAt);

        // 2. Initialized from existing note
        var existing = new Note
        {
            Id = 10,
            Text = "Existing text",
            SourceProcessName = "chrome",
            SourceWindowTitle = "W3Schools",
            SourceUrl = "https://w3schools.com",
            CapturedAt = now
        };
        using var vmExisting = new NoteEditorViewModel(tagService, allTags, existingNote: existing, draftJournalService: NoOpDraftJournalService.Instance);
        Assert.Equal("chrome", vmExisting.SourceProcessName);
        Assert.Equal("W3Schools", vmExisting.SourceWindowTitle);
        Assert.Equal("https://w3schools.com", vmExisting.SourceUrl);
        Assert.Equal(now, vmExisting.CapturedAt);
        Assert.Equal("chrome • W3Schools • https://w3schools.com", vmExisting.SourceText);
    }

    [Fact]
    public void SaveInstantNote_CreatesInboxNoteWithAutoTagsAndSourceMetadata()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<QuickNotesDbContext>().UseSqlite(connection).Options;

        using (var initDb = new QuickNotesDbContext(options))
        {
            DbInitializer.Initialize(initDb);
            initDb.Tags.Add(new Tag { Name = "CSharp" });
            initDb.SaveChanges();
        }

        var settingsPath = Path.Combine(Path.GetTempPath(), $"quicknotes_test_settings_{Guid.NewGuid():N}.json");
        try
        {
            var settingsService = new SettingsService(settingsPath, _ => { });
            var tagDetectionService = new TagDetectionService();
            var searchService = new SearchService();
            using var hotkeyService = new GlobalHotkeyService((_1, _2, _3, _4) => true, (_1, _2) => true, (IntPtr)1);
            var clipboardService = new ClipboardCaptureService();
            using var trayService = new TrayIconService();

            using var vm = MainViewModelTestComposition.Create(
                () => new QuickNotesDbContext(options),
                tagDetectionService,
                searchService,
                settingsService,
                hotkeyService,
                clipboardService,
                trayService,
                draftJournalService: NoOpDraftJournalService.Instance);

            var captureTime = DateTime.Now.AddSeconds(-2);
            var capture = new CapturedNoteContext
            {
                Text = "Learning CSharp generics and LINQ",
                ProcessName = "code",
                WindowTitle = "Main.cs - Visual Studio Code",
                Url = "https://learn.microsoft.com/dotnet/csharp",
                CapturedAt = captureTime
            };

            var note = vm.SaveInstantNote(capture);

            Assert.True(note.IsInbox);
            Assert.False(note.IsPinned);
            Assert.False(note.IsFavorite);
            Assert.Null(note.DeletedAt);
            Assert.Equal("code", note.SourceProcessName);
            Assert.Equal("Main.cs - Visual Studio Code", note.SourceWindowTitle);
            Assert.Equal("https://learn.microsoft.com/dotnet/csharp", note.SourceUrl);
            Assert.Equal(captureTime, note.CapturedAt);
            Assert.Contains(note.NoteTags, nt => nt.Tag?.Name == "CSharp" || nt.TagId > 0);

            using var verifyDb = new QuickNotesDbContext(options);
            var loaded = verifyDb.Notes.Include(n => n.NoteTags).ThenInclude(nt => nt.Tag).Single(n => n.Id == note.Id);
            Assert.True(loaded.IsInbox);
            Assert.Equal("code", loaded.SourceProcessName);
            Assert.Equal("Main.cs - Visual Studio Code", loaded.SourceWindowTitle);
            Assert.Equal("https://learn.microsoft.com/dotnet/csharp", loaded.SourceUrl);
            Assert.Equal("CSharp", loaded.NoteTags.Single().Tag.Name);
        }
        finally
        {
            if (File.Exists(settingsPath)) File.Delete(settingsPath);
        }
    }
}
