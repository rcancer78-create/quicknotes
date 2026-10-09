using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.ViewModels;
using QuickNotes.App.Helpers;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public sealed class UiSettingsAndSortingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly QuickNotesDbContext _context;
    private readonly SearchService _searchService = new();

    public UiSettingsAndSortingTests()
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
    public void AppSettings_Defaults_IncludeSystemThemeAndPinnedSortMode()
    {
        var settings = new AppSettings();

        Assert.Equal(AppTheme.System, settings.Theme);
        Assert.Equal(NoteSortMode.Pinned, settings.SortMode);
    }

    [Fact]
    public void AppSettings_JsonSerialization_RoundtripsAndUsesStringEnums()
    {
        var original = new AppSettings
        {
            Theme = AppTheme.Dark,
            SortMode = NoteSortMode.UpdatedAt,
            CompactCards = true,
            TagPanelWidth = 320
        };

        var json = JsonSerializer.Serialize(original, new JsonSerializerOptions { WriteIndented = true });

        Assert.Contains("\"Theme\": \"Dark\"", json);
        Assert.Contains("\"SortMode\": \"UpdatedAt\"", json);

        var restored = JsonSerializer.Deserialize<AppSettings>(json);
        Assert.NotNull(restored);
        Assert.Equal(AppTheme.Dark, restored.Theme);
        Assert.Equal(NoteSortMode.UpdatedAt, restored.SortMode);
        Assert.True(restored.CompactCards);
        Assert.Equal(320, restored.TagPanelWidth);
    }

    [Fact]
    public void AppSettings_BackwardsCompatibility_MissingThemeAndSortModeDefaultCorrectly()
    {
        // JSON payload from earlier versions without Theme or SortMode fields
        const string oldJson = """
        {
            "HotkeyCtrl": true,
            "HotkeyShift": true,
            "HotkeyKey": "Q",
            "CompactCards": false,
            "TagPanelWidth": 260
        }
        """;

        var deserialized = JsonSerializer.Deserialize<AppSettings>(oldJson);
        Assert.NotNull(deserialized);
        Assert.Equal(AppTheme.System, deserialized.Theme);
        Assert.Equal(NoteSortMode.Pinned, deserialized.SortMode);
    }

    [Fact]
    public void ThemeService_ResolveIsDark_MatchesSpecifiedThemes()
    {
        Assert.False(ThemeService.ResolveIsDark(AppTheme.Light));
        Assert.True(ThemeService.ResolveIsDark(AppTheme.Dark));
        Assert.True(ThemeService.ResolveIsDark(AppTheme.Green));

        bool expectedSystemDark = ThemeService.IsWindowsInDarkTheme();
        Assert.Equal(expectedSystemDark, ThemeService.ResolveIsDark(AppTheme.System));
    }

    [Fact]
    public void ThemeService_ApplyTheme_UpdatesPropertiesAndFiresEvent()
    {
        bool? eventArg = null;
        Action<bool> handler = val => eventArg = val;

        ThemeService.ThemeChanged += handler;
        try
        {
            ThemeService.ApplyTheme(AppTheme.Dark);
            Assert.Equal(AppTheme.Dark, ThemeService.CurrentTheme);
            Assert.True(ThemeService.IsDark);
            Assert.True(eventArg);

            ThemeService.ApplyTheme(AppTheme.Light);
            Assert.Equal(AppTheme.Light, ThemeService.CurrentTheme);
            Assert.False(ThemeService.IsDark);
            Assert.False(eventArg);
        }
        finally
        {
            ThemeService.ThemeChanged -= handler;
            ThemeService.ApplyTheme(AppTheme.System);
        }
    }

    [Fact]
    public void SearchService_Sorting_Pinned_PlacesPinnedFirstThenCreatedAtDesc()
    {
        var noteOldUnpinned = new Note
        {
            Text = "Old Unpinned",
            CreatedAt = new DateTime(2026, 1, 1),
            UpdatedAt = new DateTime(2026, 1, 1),
            IsPinned = false
        };
        var noteOldPinned = new Note
        {
            Text = "Old Pinned",
            CreatedAt = new DateTime(2026, 1, 2),
            UpdatedAt = new DateTime(2026, 1, 2),
            IsPinned = true
        };
        var noteNewUnpinned = new Note
        {
            Text = "New Unpinned",
            CreatedAt = new DateTime(2026, 1, 3),
            UpdatedAt = new DateTime(2026, 1, 3),
            IsPinned = false
        };
        var noteNewPinned = new Note
        {
            Text = "New Pinned",
            CreatedAt = new DateTime(2026, 1, 4),
            UpdatedAt = new DateTime(2026, 1, 4),
            IsPinned = true
        };

        _context.Notes.AddRange(noteOldUnpinned, noteOldPinned, noteNewUnpinned, noteNewPinned);
        _context.SaveChanges();

        var result = _searchService.QueryNotes(
            _context,
            searchQuery: null,
            selectedTagId: null,
            allTags: Enumerable.Empty<Tag>(),
            section: NavigationSection.All,
            sortMode: NoteSortMode.Pinned);

        Assert.Equal(4, result.Count);
        Assert.Equal("New Pinned", result[0].Text);
        Assert.Equal("Old Pinned", result[1].Text);
        Assert.Equal("New Unpinned", result[2].Text);
        Assert.Equal("Old Unpinned", result[3].Text);
    }

    [Fact]
    public void SearchService_Sorting_CreatedAt_SortsStrictlyByCreationDateDescending()
    {
        var note1 = new Note
        {
            Text = "Date 1 (Pinned)",
            CreatedAt = new DateTime(2026, 2, 1),
            UpdatedAt = new DateTime(2026, 2, 1),
            IsPinned = true
        };
        var note2 = new Note
        {
            Text = "Date 2 (Unpinned)",
            CreatedAt = new DateTime(2026, 2, 5),
            UpdatedAt = new DateTime(2026, 2, 5),
            IsPinned = false
        };
        var note3 = new Note
        {
            Text = "Date 3 (Pinned)",
            CreatedAt = new DateTime(2026, 2, 10),
            UpdatedAt = new DateTime(2026, 2, 10),
            IsPinned = true
        };

        _context.Notes.AddRange(note1, note2, note3);
        _context.SaveChanges();

        var result = _searchService.QueryNotes(
            _context,
            searchQuery: null,
            selectedTagId: null,
            allTags: Enumerable.Empty<Tag>(),
            section: NavigationSection.All,
            sortMode: NoteSortMode.CreatedAt);

        Assert.Equal(3, result.Count);
        Assert.Equal("Date 3 (Pinned)", result[0].Text);
        Assert.Equal("Date 2 (Unpinned)", result[1].Text);
        Assert.Equal("Date 1 (Pinned)", result[2].Text);
    }

    [Fact]
    public void SearchService_Sorting_UpdatedAt_SortsByModificationDateDescending()
    {
        var note1 = new Note
        {
            Text = "Created early, modified latest",
            CreatedAt = new DateTime(2026, 1, 1),
            UpdatedAt = new DateTime(2026, 3, 20),
            IsPinned = true
        };
        var note2 = new Note
        {
            Text = "Created mid, modified middle",
            CreatedAt = new DateTime(2026, 2, 1),
            UpdatedAt = new DateTime(2026, 3, 10),
            IsPinned = false
        };
        var note3 = new Note
        {
            Text = "Created late, never modified",
            CreatedAt = new DateTime(2026, 3, 1),
            UpdatedAt = new DateTime(2026, 3, 1),
            IsPinned = true
        };

        _context.Notes.AddRange(note1, note2, note3);
        _context.SaveChanges();

        var result = _searchService.QueryNotes(
            _context,
            searchQuery: null,
            selectedTagId: null,
            allTags: Enumerable.Empty<Tag>(),
            section: NavigationSection.All,
            sortMode: NoteSortMode.UpdatedAt);

        Assert.Equal(3, result.Count);
        Assert.Equal("Created early, modified latest", result[0].Text);
        Assert.Equal("Created mid, modified middle", result[1].Text);
        Assert.Equal("Created late, never modified", result[2].Text);
    }

    [Fact]
    public void SearchService_Sorting_CombinesWithVirtualSectionsAndSearch()
    {
        var fav1 = new Note
        {
            Text = "Searchable report alpha",
            IsFavorite = true,
            CreatedAt = new DateTime(2026, 1, 1),
            UpdatedAt = new DateTime(2026, 2, 15)
        };
        var fav2 = new Note
        {
            Text = "Searchable report beta",
            IsFavorite = true,
            CreatedAt = new DateTime(2026, 1, 10),
            UpdatedAt = new DateTime(2026, 2, 1)
        };
        var notFav = new Note
        {
            Text = "Searchable report gamma",
            IsFavorite = false,
            CreatedAt = new DateTime(2026, 1, 20),
            UpdatedAt = new DateTime(2026, 2, 20)
        };

        _context.Notes.AddRange(fav1, fav2, notFav);
        _context.SaveChanges();

        // 1. Favorites section with CreatedAt desc => fav2, fav1
        var favResultCreated = _searchService.QueryNotes(
            _context,
            searchQuery: null,
            selectedTagId: null,
            allTags: Enumerable.Empty<Tag>(),
            section: NavigationSection.Favorites,
            sortMode: NoteSortMode.CreatedAt);
        Assert.Equal(2, favResultCreated.Count);
        Assert.Equal("Searchable report beta", favResultCreated[0].Text);
        Assert.Equal("Searchable report alpha", favResultCreated[1].Text);

        // 2. Favorites section with UpdatedAt desc => fav1, fav2
        var favResultUpdated = _searchService.QueryNotes(
            _context,
            searchQuery: null,
            selectedTagId: null,
            allTags: Enumerable.Empty<Tag>(),
            section: NavigationSection.Favorites,
            sortMode: NoteSortMode.UpdatedAt);
        Assert.Equal(2, favResultUpdated.Count);
        Assert.Equal("Searchable report alpha", favResultUpdated[0].Text);
        Assert.Equal("Searchable report beta", favResultUpdated[1].Text);

        // 3. Search query with sorting
        var searchResult = _searchService.QueryNotes(
            _context,
            searchQuery: "Searchable",
            selectedTagId: null,
            allTags: Enumerable.Empty<Tag>(),
            section: NavigationSection.All,
            sortMode: NoteSortMode.UpdatedAt);
        Assert.Equal(3, searchResult.Count);
        Assert.Equal("Searchable report gamma", searchResult[0].Text); // Updated Feb 20
        Assert.Equal("Searchable report alpha", searchResult[1].Text); // Updated Feb 15
        Assert.Equal("Searchable report beta", searchResult[2].Text);  // Updated Feb 1
    }

    [Fact]
    public async Task SettingsViewModel_ThemeOption_ContainsGreenAndSaves()
    {
        string path = Path.Combine(Path.GetTempPath(), $"quicknotes-theme-test-{Guid.NewGuid():N}.json");
        try
        {
            var settingsService = new SettingsService(path, _ => { });
            using var hotkeyService = new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero);
            var vm = SettingsViewModelTestComposition.Create(settingsService, hotkeyService);

            Assert.Equal(4, vm.AvailableThemes.Count);
            Assert.Contains(vm.AvailableThemes, t => t.Theme == AppTheme.System);
            Assert.Contains(vm.AvailableThemes, t => t.Theme == AppTheme.Light);
            Assert.Contains(vm.AvailableThemes, t => t.Theme == AppTheme.Dark);
            Assert.Contains(vm.AvailableThemes, t => t.Theme == AppTheme.Green);

            var greenOption = vm.AvailableThemes.First(t => t.Theme == AppTheme.Green);
            Assert.Equal("Зелёная", greenOption.Title);
            vm.SelectedThemeItem = greenOption;

            // Save settings via command
            await vm.SaveAsync();

            // Re-read settings from file
            var reloaded = settingsService.LoadSettings();
            Assert.Equal(AppTheme.Green, reloaded.Theme);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void MainViewModel_SortMode_InitializesAndReordersNotes()
    {
        string settingsPath = Path.Combine(Path.GetTempPath(), $"quicknotes-sort-vm-test-{Guid.NewGuid():N}.json");
        try
        {
            // Seed database
            var noteOldPinned = new Note
            {
                Text = "Note A (Old Pinned)",
                CreatedAt = new DateTime(2026, 1, 1),
                UpdatedAt = new DateTime(2026, 1, 1),
                IsPinned = true
            };
            var noteNewUnpinned = new Note
            {
                Text = "Note B (New Unpinned)",
                CreatedAt = new DateTime(2026, 2, 1),
                UpdatedAt = new DateTime(2026, 2, 1),
                IsPinned = false
            };
            _context.Notes.AddRange(noteOldPinned, noteNewUnpinned);
            _context.SaveChanges();

            using var hotkey = new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero);
            using var tray = new TrayIconService();
            var debouncer = new SearchDebouncer(delayMs: 0);
            var settingsService = new SettingsService(settingsPath, _ => { });

            using var vm = MainViewModelTestComposition.Create(
                () => new QuickNotesDbContext(new DbContextOptionsBuilder<QuickNotesDbContext>().UseSqlite(_connection).Options),
                new TagDetectionService(),
                _searchService,
                settingsService,
                hotkey,
                new ClipboardCaptureService(),
                tray,
                searchDebouncer: debouncer,
                draftJournalService: NoOpDraftJournalService.Instance);

            // Verify available sort modes
            Assert.Equal(3, vm.AvailableSortModes.Count);
            Assert.Contains(vm.AvailableSortModes, s => s.Mode == NoteSortMode.Pinned);
            Assert.Contains(vm.AvailableSortModes, s => s.Mode == NoteSortMode.CreatedAt);
            Assert.Contains(vm.AvailableSortModes, s => s.Mode == NoteSortMode.UpdatedAt);

            // Default sort mode is Pinned
            Assert.Equal(NoteSortMode.Pinned, vm.SortMode);
            Assert.Equal(NoteSortMode.Pinned, vm.SelectedSortModeItem.Mode);

            // In Pinned mode: Note A (pinned) comes first
            Assert.Equal(2, vm.Notes.Count);
            Assert.Equal("Note A (Old Pinned)", vm.Notes[0].Text);
            Assert.Equal("Note B (New Unpinned)", vm.Notes[1].Text);

            // Switch sort mode to CreatedAt
            var createdOption = vm.AvailableSortModes.First(s => s.Mode == NoteSortMode.CreatedAt);
            vm.SelectedSortModeItem = createdOption;

            Assert.Equal(NoteSortMode.CreatedAt, vm.SortMode);

            // In CreatedAt mode: Note B (Feb 1) comes first, regardless of pin
            Assert.Equal("Note B (New Unpinned)", vm.Notes[0].Text);
            Assert.Equal("Note A (Old Pinned)", vm.Notes[1].Text);

            // Verify settings persistence
            var reloaded = settingsService.LoadSettings();
            Assert.Equal(NoteSortMode.CreatedAt, reloaded.SortMode);
        }
        finally
        {
            if (File.Exists(settingsPath)) File.Delete(settingsPath);
        }
    }

    [Fact]
    public void MainViewModel_NotesHeaderTitle_ReflectsSectionAndTag()
    {
        string settingsPath = Path.Combine(Path.GetTempPath(), $"quicknotes-title-vm-test-{Guid.NewGuid():N}.json");
        try
        {
            using var hotkey = new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero);
            using var tray = new TrayIconService();
            var debouncer = new SearchDebouncer(delayMs: 0);
            var settingsService = new SettingsService(settingsPath, _ => { });

            using var vm = MainViewModelTestComposition.Create(
                () => new QuickNotesDbContext(new DbContextOptionsBuilder<QuickNotesDbContext>().UseSqlite(_connection).Options),
                new TagDetectionService(),
                _searchService,
                settingsService,
                hotkey,
                new ClipboardCaptureService(),
                tray,
                searchDebouncer: debouncer,
                draftJournalService: NoOpDraftJournalService.Instance);

            // Default is All notes
            Assert.Equal("Все заметки", vm.NotesHeaderTitle);

            // Select Favorites section
            var favSection = vm.VirtualSections.First(s => s.Section == NavigationSection.Favorites);
            vm.SelectedSection = favSection;
            Assert.Equal("Избранное", vm.NotesHeaderTitle);

            // Select Trash section
            var trashSection = vm.VirtualSections.First(s => s.Section == NavigationSection.Trash);
            vm.SelectedSection = trashSection;
            Assert.Equal("Корзина", vm.NotesHeaderTitle);
        }
        finally
        {
            if (File.Exists(settingsPath)) File.Delete(settingsPath);
        }
    }

    [Fact]
    public void ThemeService_ApplyThemeColors_HandlesHeadlessExecutionWithoutThrowing()
    {
        // When Application.Current is null (or during headless tests), ApplyThemeColors must not throw
        var exDark = Record.Exception(() => ThemeService.ApplyThemeColors(true));
        Assert.Null(exDark);

        var exLight = Record.Exception(() => ThemeService.ApplyThemeColors(false));
        Assert.Null(exLight);
    }
}

