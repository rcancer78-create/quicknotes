using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.ViewModels;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public class TagSuggestionServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<QuickNotesDbContext> _options;
    private readonly string _tempSettingsPath;
    private readonly List<IDisposable> _disposables = new();
    private readonly TagSuggestionService _suggestionService = new();

    public TagSuggestionServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<QuickNotesDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new QuickNotesDbContext(_options);
        DbInitializer.Initialize(db);

        _tempSettingsPath = Path.Combine(Path.GetTempPath(), $"quicknotes-suggestion-test-{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            try { d.Dispose(); } catch { }
        }
        _connection.Dispose();
        if (File.Exists(_tempSettingsPath))
        {
            try { File.Delete(_tempSettingsPath); } catch { }
        }
    }

    private MainViewModel CreateViewModel()
    {
        var settingsService = new SettingsService(_tempSettingsPath, _ => { });
        var hotkey = new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero);
        var tray = new TrayIconService();
        _disposables.Add(hotkey);
        _disposables.Add(tray);
        var debouncer = new SearchDebouncer(0, a => a());
        _disposables.Add(debouncer);

        var vm = MainViewModelTestComposition.Create(
            () => new QuickNotesDbContext(_options),
            new TagDetectionService(),
            new SearchService(),
            settingsService,
            hotkey,
            new ClipboardCaptureService(),
            tray,
            new BackupService(),
            debouncer,
            new TagMergeService(),
            new TagRescanPreviewService(new TagDetectionService()),
            _suggestionService,
            draftJournalService: NoOpDraftJournalService.Instance);
        vm.NotificationHandler = (msg, title) => { };
        _disposables.Add(vm);
        return vm;
    }

    [Fact]
    public void SuggestTags_ExcludesDeletedNotes()
    {
        var notes = new List<Note>
        {
            new() { Id = 1, Text = "Изучаем Kubernetes в продакшене", DeletedAt = null },
            new() { Id = 2, Text = "Настройка Kubernetes кластера", DeletedAt = DateTime.Now }, // Deleted!
            new() { Id = 3, Text = "Архитектура кластера Kubernetes", DeletedAt = DateTime.Now }, // Deleted!
            new() { Id = 4, Text = "Только в корзине: Терраформ", DeletedAt = DateTime.Now },
            new() { Id = 5, Text = "Тоже корзина: Терраформ", DeletedAt = DateTime.Now }
        };

        var suggestions = _suggestionService.SuggestTags(notes, existingTags: Array.Empty<Tag>());

        // Kubernetes appears only in 1 active note (Id=1), so with default minNotesCount=2 it should NOT be suggested.
        Assert.DoesNotContain(suggestions, s => s.Word.Equals("kubernetes", StringComparison.OrdinalIgnoreCase));
        // Терраформ appears only in deleted notes, so it should never be suggested.
        Assert.DoesNotContain(suggestions, s => s.Word.Equals("терраформ", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SuggestTags_CountsDistinctNotesNotCharacterOccurrences()
    {
        var notes = new List<Note>
        {
            // Note 1 has 'Docker' 5 times
            new() { Id = 1, Text = "Docker Docker Docker Docker Docker используется для контейнеризации", DeletedAt = null },
            // Note 2 has 'Docker' once
            new() { Id = 2, Text = "Надо обновить образ Docker на сервере", DeletedAt = null },
            // Note 3 does not have Docker
            new() { Id = 3, Text = "Обычная заметка без ключевых слов", DeletedAt = null }
        };

        var suggestions = _suggestionService.SuggestTags(notes, existingTags: Array.Empty<Tag>());

        var dockerSuggestion = suggestions.FirstOrDefault(s => s.Word.Equals("docker", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(dockerSuggestion);
        // Must count distinct notes (2 notes), not occurrences (6 times)
        Assert.Equal(2, dockerSuggestion.NotesCount);
        Assert.Equal(2, dockerSuggestion.NoteIds.Count);
        Assert.Contains(1, dockerSuggestion.NoteIds);
        Assert.Contains(2, dockerSuggestion.NoteIds);
    }

    [Fact]
    public void SuggestTags_ExcludesExistingTagNames_CaseInsensitive()
    {
        var notes = new List<Note>
        {
            new() { Id = 1, Text = "Работаем с базой Postgres", DeletedAt = null },
            new() { Id = 2, Text = "Оптимизация запросов в postgres", DeletedAt = null }
        };

        var existingTags = new[]
        {
            new Tag { Id = 1, Name = "Postgres" }
        };

        var suggestions = _suggestionService.SuggestTags(notes, existingTags);

        Assert.DoesNotContain(suggestions, s => s.Word.Equals("postgres", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SuggestTags_ExcludesExistingSynonyms_CaseInsensitive()
    {
        var notes = new List<Note>
        {
            new() { Id = 1, Text = "Кластер оракл в работе", DeletedAt = null },
            new() { Id = 2, Text = "Миграция данных из ОРАКЛ", DeletedAt = null }
        };

        var existingTags = new[]
        {
            new Tag
            {
                Id = 1,
                Name = "Oracle",
                Synonyms = new List<TagSynonym>
                {
                    new() { Id = 10, TagId = 1, Value = "оракл" }
                }
            }
        };

        var suggestions = _suggestionService.SuggestTags(notes, existingTags);

        Assert.DoesNotContain(suggestions, s => s.Word.Equals("оракл", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SuggestTags_ExcludesShortNoise()
    {
        var notes = new List<Note>
        {
            new() { Id = 1, Text = "а б в г д е ж и к л м на по за от до не но да он мы то so no do my", DeletedAt = null },
            new() { Id = 2, Text = "а б в г д е ж и к л м на по за от до не но да он мы то so no do my", DeletedAt = null }
        };

        var suggestions = _suggestionService.SuggestTags(notes, existingTags: Array.Empty<Tag>());

        // 1-letter and 2-letter non-technical words must be eliminated as noise
        Assert.Empty(suggestions);
    }

    [Fact]
    public void SuggestTags_ExcludesUrlsAndDomainTokens()
    {
        var notes = new List<Note>
        {
            new() { Id = 1, Text = "Ссылки: https://github.com/dotnet/wpf и https://example.org/api/v1", DeletedAt = null },
            new() { Id = 2, Text = "Документация: https://example.org/api/v2 и www.github.com/dotnet/runtime", DeletedAt = null }
        };

        var suggestions = _suggestionService.SuggestTags(notes, existingTags: Array.Empty<Tag>());

        // URL domain/path words should not leak into suggestions
        Assert.DoesNotContain(suggestions, s => s.Word.Equals("https", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(suggestions, s => s.Word.Equals("github", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(suggestions, s => s.Word.Equals("example", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(suggestions, s => s.Word.Equals("com", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(suggestions, s => s.Word.Equals("org", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SuggestTags_ExcludesBareDomainTokens()
    {
        var notes = new List<Note>
        {
            new() { Id = 1, Text = "Документация github.com и api.example.org", DeletedAt = null },
            new() { Id = 2, Text = "Снова github.com и api.example.org", DeletedAt = null }
        };

        var suggestions = _suggestionService.SuggestTags(notes, existingTags: Array.Empty<Tag>());

        Assert.DoesNotContain(suggestions, s => s.Word.Equals("github", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(suggestions, s => s.Word.Equals("example", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(suggestions, s => s.Word.Equals("org", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SuggestTags_DeduplicatesRepeatedPersistedNoteIds()
    {
        var notes = new List<Note>
        {
            new() { Id = 7, Text = "Docker deployment", DeletedAt = null },
            new() { Id = 7, Text = "Docker deployment duplicated by caller", DeletedAt = null },
            new() { Id = 8, Text = "Docker production", DeletedAt = null }
        };

        var suggestions = _suggestionService.SuggestTags(notes, existingTags: Array.Empty<Tag>());
        var docker = suggestions.Single(s => s.Word.Equals("docker", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(2, docker.NotesCount);
        Assert.Equal(new[] { 7, 8 }, docker.NoteIds.OrderBy(id => id));
    }

    [Fact]
    public void SuggestTags_ExcludesPureNumbers()
    {
        var notes = new List<Note>
        {
            new() { Id = 1, Text = "Номера релизов 100 200 2026", DeletedAt = null },
            new() { Id = 2, Text = "Обновления 100 200 2026", DeletedAt = null }
        };

        var suggestions = _suggestionService.SuggestTags(notes, existingTags: Array.Empty<Tag>());

        Assert.DoesNotContain(suggestions, s => s.Word == "100");
        Assert.DoesNotContain(suggestions, s => s.Word == "200");
        Assert.DoesNotContain(suggestions, s => s.Word == "2026");
    }

    [Fact]
    public void SuggestTags_ExcludesRussianAndEnglishStopWords()
    {
        var notes = new List<Note>
        {
            new() { Id = 1, Text = "Этот который был когда очень здесь потому which where when there should would", DeletedAt = null },
            new() { Id = 2, Text = "Этот который был когда очень здесь потому which where when there should would", DeletedAt = null }
        };

        var suggestions = _suggestionService.SuggestTags(notes, existingTags: Array.Empty<Tag>());

        Assert.Empty(suggestions);
    }

    [Fact]
    public void SuggestTags_SupportsTechnicalTokens_CSharp_Cpp_DotNet_1C()
    {
        var notes = new List<Note>
        {
            new() { Id = 1, Text = "Серверная часть на C# и .NET 8, движок на C++, интеграция с 1C.", DeletedAt = null },
            new() { Id = 2, Text = "Клиент использует C# и .NET, библиотека C++, обмен через 1C.", DeletedAt = null }
        };

        var suggestions = _suggestionService.SuggestTags(notes, existingTags: Array.Empty<Tag>());

        Assert.Contains(suggestions, s => s.Word == "C#");
        Assert.Contains(suggestions, s => s.Word == ".NET");
        Assert.Contains(suggestions, s => s.Word == "C++");
        Assert.Contains(suggestions, s => s.Word == "1C");

        var csharp = suggestions.Single(s => s.Word == "C#");
        Assert.Equal(2, csharp.NotesCount);

        var dotnet = suggestions.Single(s => s.Word == ".NET");
        Assert.Equal(2, dotnet.NotesCount);

        var cpp = suggestions.Single(s => s.Word == "C++");
        Assert.Equal(2, cpp.NotesCount);

        var oneC = suggestions.Single(s => s.Word == "1C");
        Assert.Equal(2, oneC.NotesCount);
    }

    [Fact]
    public void SuggestTags_DeterministicRanking_NotesCountThenAlphabetical()
    {
        var notes = new List<Note>
        {
            new() { Id = 1, Text = "Альфа Бета Гамма", DeletedAt = null },
            new() { Id = 2, Text = "Альфа Бета Гамма", DeletedAt = null },
            new() { Id = 3, Text = "Бета Гамма", DeletedAt = null },
            new() { Id = 4, Text = "Гамма", DeletedAt = null }
        };

        // Occurrences:
        // гамма: 4 notes
        // бета: 3 notes
        // альфа: 2 notes
        var suggestions = _suggestionService.SuggestTags(notes, existingTags: Array.Empty<Tag>());

        Assert.True(suggestions.Count >= 3);
        Assert.Equal("гамма", suggestions[0].Word);
        Assert.Equal(4, suggestions[0].NotesCount);

        Assert.Equal("бета", suggestions[1].Word);
        Assert.Equal(3, suggestions[1].NotesCount);

        Assert.Equal("альфа", suggestions[2].Word);
        Assert.Equal(2, suggestions[2].NotesCount);
    }

    [Fact]
    public void SuggestTags_DeterministicRanking_AlphabeticalWhenCountsEqual()
    {
        var notes = new List<Note>
        {
            new() { Id = 1, Text = "Zebra Apple Banana", DeletedAt = null },
            new() { Id = 2, Text = "Banana Apple Zebra", DeletedAt = null }
        };

        var suggestions = _suggestionService.SuggestTags(notes, existingTags: Array.Empty<Tag>());

        // All have count = 2, alphabetical order: apple, banana, zebra
        var words = suggestions.Select(s => s.Word).ToList();
        Assert.Equal(new[] { "apple", "banana", "zebra" }, words);
    }

    [Fact]
    public void SuggestTags_ConfigurableOptions_MinNotesCountAndMaxSuggestions()
    {
        var notes = new List<Note>
        {
            new() { Id = 1, Text = "One Two Three Four Five", DeletedAt = null },
            new() { Id = 2, Text = "One Two Three Four Five", DeletedAt = null },
            new() { Id = 3, Text = "One Two Three Four", DeletedAt = null },
            new() { Id = 4, Text = "One Two Three", DeletedAt = null },
            new() { Id = 5, Text = "One Two", DeletedAt = null },
            new() { Id = 6, Text = "One", DeletedAt = null }
        };

        // MinNotesCount = 4 -> only One (6), Two (5), Three (4), Four (4) qualify; Five (2) is filtered out
        var options = new TagSuggestionOptions
        {
            MinNotesCount = 4,
            MaxSuggestions = 2
        };

        var suggestions = _suggestionService.SuggestTags(notes, existingTags: Array.Empty<Tag>(), options);

        Assert.Equal(2, suggestions.Count);
        Assert.Equal("one", suggestions[0].Word);
        Assert.Equal(6, suggestions[0].NotesCount);
        Assert.Equal("two", suggestions[1].Word);
        Assert.Equal(5, suggestions[1].NotesCount);
    }

    [Fact]
    public void SuggestTags_DoesNotMutateNotesOrTags()
    {
        var note = new Note
        {
            Id = 42,
            Text = "Docker контейнеры и Kubernetes",
            DeletedAt = null,
            NoteTags = new List<NoteTag>()
        };
        var notes = new[] { note, new Note { Id = 43, Text = "Docker swarm", DeletedAt = null } };
        var tag = new Tag { Id = 1, Name = "InitialTag" };
        var tags = new[] { tag };

        _suggestionService.SuggestTags(notes, tags);

        Assert.Equal(42, note.Id);
        Assert.Equal("Docker контейнеры и Kubernetes", note.Text);
        Assert.Empty(note.NoteTags);
        Assert.Null(note.DeletedAt);
        Assert.Equal("InitialTag", tag.Name);
    }

    [Fact]
    public void SuggestTags_UnifiesCyrillicAndLatin1C()
    {
        var notes = new List<Note>
        {
            new() { Id = 1, Text = "Интеграция с 1C завершена", DeletedAt = null }, // Latin C
            new() { Id = 2, Text = "Синхронизация через 1С работает", DeletedAt = null } // Cyrillic С
        };

        var suggestions = _suggestionService.SuggestTags(notes, existingTags: Array.Empty<Tag>());

        var oneC = suggestions.SingleOrDefault(s => s.Word == "1C");
        Assert.NotNull(oneC);
        Assert.Equal(2, oneC.NotesCount);
    }

    [Fact]
    public void ViewModel_SelectionAndCommands_WorkAsExpected()
    {
        var item1 = new TagSuggestionItem { Word = "docker", NotesCount = 3, Preview = "...Docker в проде..." };
        var item2 = new TagSuggestionItem { Word = "kubernetes", NotesCount = 2, Preview = "...Kubernetes кластер..." };

        var vm = new TagSuggestionsViewModel(new[] { item1, item2 });

        Assert.True(vm.HasSuggestions);
        Assert.Equal(2, vm.Suggestions.Count);
        Assert.Equal(item1, vm.SelectedSuggestion);
        Assert.True(vm.CanCreateTag);
        Assert.Equal("docker", vm.SelectedTagName);

        bool? closeResult = null;
        vm.RequestClose += res => closeResult = res;

        // Cancel
        vm.CancelCommand.Execute(null);
        Assert.False(closeResult);

        // Confirm
        closeResult = null;
        vm.SelectedSuggestion = item2;
        Assert.Equal("kubernetes", vm.SelectedTagName);
        vm.CreateTagCommand.Execute(null);
        Assert.True(closeResult);

        // When selection is cleared
        vm.SelectedSuggestion = null;
        Assert.False(vm.CanCreateTag);
    }

    [Fact]
    public void MainViewModel_NoMutationBeforeExplicitCreate_WhenDialogCancelled()
    {
        using (var db = new QuickNotesDbContext(_options))
        {
            db.Notes.Add(new Note { Text = "Изучаем Ansible для автоматизации" });
            db.Notes.Add(new Note { Text = "Настройка плейбуков Ansible" });
            db.SaveChanges();
        }

        var mainVm = CreateViewModel();

        // Simulate user opening dialog and clicking Cancel (returns false)
        bool dialogOpened = false;
        mainVm.RequestOpenTagSuggestions += vm =>
        {
            dialogOpened = true;
            Assert.Contains(vm.Suggestions, s => s.Word.Equals("ansible", StringComparison.OrdinalIgnoreCase));
            return false; // User cancelled!
        };

        mainVm.SuggestTagsCommand.Execute(null);

        Assert.True(dialogOpened);

        using (var db = new QuickNotesDbContext(_options))
        {
            Assert.Empty(db.Tags); // No tag was created!
        }
    }

    [Fact]
    public void MainViewModel_CreatesRootTagAfterConfirmation()
    {
        using (var db = new QuickNotesDbContext(_options))
        {
            db.Notes.Add(new Note { Text = "Изучаем Ansible для автоматизации" });
            db.Notes.Add(new Note { Text = "Настройка плейбуков Ansible" });
            db.SaveChanges();
        }

        var mainVm = CreateViewModel();

        bool dialogOpened = false;
        mainVm.RequestOpenTagSuggestions += vm =>
        {
            dialogOpened = true;
            var ansible = vm.Suggestions.FirstOrDefault(s => s.Word.Equals("ansible", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(ansible);
            vm.SelectedSuggestion = ansible;
            return true; // User clicked "Создать тег"!
        };

        mainVm.SuggestTagsCommand.Execute(null);

        Assert.True(dialogOpened);

        using (var db = new QuickNotesDbContext(_options))
        {
            var created = db.Tags.SingleOrDefault(t => t.Name == "ansible");
            Assert.NotNull(created);
            Assert.Null(created.ParentTagId); // Created as root tag!
        }

        // Tree roots reloaded
        Assert.Contains(mainVm.TagTreeRoots, node => node.Name == "ansible");
    }

    [Fact]
    public void MainViewModel_CreateTagFromSuggestion_ProtectsAgainstDuplicates()
    {
        using (var db = new QuickNotesDbContext(_options))
        {
            db.Tags.Add(new Tag { Name = "Docker", ParentTagId = null });
            db.SaveChanges();
        }

        var mainVm = CreateViewModel();

        // Attempting to create duplicate (case-insensitive)
        bool success = mainVm.CreateTagFromSuggestion("docker");
        Assert.False(success);

        bool successUpper = mainVm.CreateTagFromSuggestion("DOCKER");
        Assert.False(successUpper);

        using (var db = new QuickNotesDbContext(_options))
        {
            Assert.Single(db.Tags); // Still exactly one tag!
        }
    }
}
