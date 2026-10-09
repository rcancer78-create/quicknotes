using System.Collections.Generic;
using System.Linq;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.ViewModels;
using Xunit;

namespace QuickNotes.Tests;

public class TagDetectionServiceTests
{
    private readonly TagDetectionService _service = new();

    [Fact]
    public void Scenario1_DetectsTagBySynonym_OracleDB()
    {
        var oracleTag = new Tag
        {
            Id = 1,
            Name = "Oracle",
            Synonyms = new List<TagSynonym>
            {
                new() { Id = 1, TagId = 1, Value = "oracle db" },
                new() { Id = 2, TagId = 1, Value = "оракл" }
            }
        };

        string text = "Проблема возникла при подключении к Oracle DB";
        var detected = _service.DetectTags(text, new[] { oracleTag });

        Assert.Single(detected);
        Assert.Equal("Oracle", detected[0].Name);
    }

    [Fact]
    public void Scenario1_DetectsTagByRussianSynonym()
    {
        var oracleTag = new Tag
        {
            Id = 1,
            Name = "Oracle",
            Synonyms = new List<TagSynonym>
            {
                new() { Id = 2, TagId = 1, Value = "оракл" }
            }
        };

        string text = "Мы используем оракл в качестве основной базы";
        var detected = _service.DetectTags(text, new[] { oracleTag });

        Assert.Single(detected);
        Assert.Equal("Oracle", detected[0].Name);
    }

    [Fact]
    public void Scenario5_WordBoundaries_DoesNotMatchSubwords()
    {
        var aiTag = new Tag
        {
            Id = 2,
            Name = "AI"
        };

        string text = "railway server";
        var detected = _service.DetectTags(text, new[] { aiTag });

        Assert.Empty(detected);
    }

    [Fact]
    public void Scenario5_WordBoundaries_MatchesExactWord()
    {
        var aiTag = new Tag
        {
            Id = 2,
            Name = "AI"
        };

        string text = "Modern AI models are transforming software";
        var detected = _service.DetectTags(text, new[] { aiTag });

        Assert.Single(detected);
        Assert.Equal("AI", detected[0].Name);
    }

    [Fact]
    public void Scenario6_TechnicalTags_CSharp()
    {
        var csharpTag = new Tag
        {
            Id = 3,
            Name = "C#"
        };

        string text = "Application written in C#";
        var detected = _service.DetectTags(text, new[] { csharpTag });

        Assert.Single(detected);
        Assert.Equal("C#", detected[0].Name);
    }

    [Fact]
    public void Scenario6_TechnicalTags_Cpp_DotNet_1C()
    {
        var cppTag = new Tag { Id = 4, Name = "C++" };
        var dotNetTag = new Tag { Id = 5, Name = ".NET" };
        var oneCTag = new Tag { Id = 6, Name = "1C" };

        var tags = new[] { cppTag, dotNetTag, oneCTag };

        string text = "System core is in C++, backend runs on .NET 8, integration with 1C.";
        var detected = _service.DetectTags(text, tags);

        Assert.Equal(3, detected.Count);
        Assert.Contains(detected, t => t.Name == "C++");
        Assert.Contains(detected, t => t.Name == ".NET");
        Assert.Contains(detected, t => t.Name == "1C");
    }

    [Fact]
    public void Scenario3_SuppressedAutoTag_IsNotReaddedOnRescan()
    {
        var oracleTag = new Tag { Id = 1, Name = "Oracle" };
        var note = new Note
        {
            Id = 10,
            Text = "Working with Oracle database today",
            NoteTags = new List<NoteTag>()
        };

        // First scan: Oracle is detected
        _service.RescanNote(note, new[] { oracleTag });
        Assert.Single(note.NoteTags);
        Assert.Equal(TagOrigin.Auto, note.NoteTags.First().Origin);
        Assert.False(note.NoteTags.First().IsSuppressed);

        // User deletes the auto tag: it becomes suppressed
        note.NoteTags.First().IsSuppressed = true;

        // Rescan again with same text: Oracle must NOT be re-added as active tag
        _service.RescanNote(note, new[] { oracleTag });
        Assert.Single(note.NoteTags);
        Assert.True(note.NoteTags.First().IsSuppressed); // Still suppressed, not active
    }

    [Fact]
    public void Scenario4_ManualTag_IsPreservedOnRescan()
    {
        var importantTag = new Tag { Id = 20, Name = "Important" };
        var oracleTag = new Tag { Id = 1, Name = "Oracle" };

        var note = new Note
        {
            Id = 11,
            Text = "Regular text without any keywords",
            NoteTags = new List<NoteTag>
            {
                new()
                {
                    NoteId = 11,
                    TagId = 20,
                    Tag = importantTag,
                    Origin = TagOrigin.Manual,
                    IsSuppressed = false
                }
            }
        };

        // Rescan: text does not mention "Important", but it's manual so must be kept
        _service.RescanNote(note, new[] { importantTag, oracleTag });

        Assert.Single(note.NoteTags);
        Assert.Equal(20, note.NoteTags.First().TagId);
        Assert.Equal(TagOrigin.Manual, note.NoteTags.First().Origin);
    }

    [Fact]
    public void DetailedMatch_NameVsSynonym_EqualLength_PrefersTagName()
    {
        var tag = new Tag
        {
            Id = 10,
            Name = "Postgres",
            Synonyms = new List<TagSynonym>
            {
                new() { Id = 1, TagId = 10, Value = "postgres" },
                new() { Id = 2, TagId = 10, Value = "pgresql8" }
            }
        };

        var match = _service.MatchTag("Using Postgres in our architecture", tag);

        Assert.NotNull(match);
        Assert.True(match.MatchedByTagName);
        Assert.Null(match.MatchedSynonym);
        Assert.Equal("Postgres", match.MatchedTerm);
        Assert.Equal("по имени тега", match.Reason);
    }

    [Fact]
    public void DetailedMatch_NameVsSynonym_LongerSynonymWins()
    {
        var tag = new Tag
        {
            Id = 11,
            Name = "DB",
            Synonyms = new List<TagSynonym>
            {
                new() { Id = 1, TagId = 11, Value = "Database" }
            }
        };

        var match = _service.MatchTag("Connecting to database server", tag);

        Assert.NotNull(match);
        Assert.False(match.MatchedByTagName);
        Assert.NotNull(match.MatchedSynonym);
        Assert.Equal("Database", match.MatchedTerm);
        Assert.Equal("по синониму «Database»", match.Reason);
    }

    [Fact]
    public void DetailedMatch_NameVsSynonym_LongerTagNameWins()
    {
        var tag = new Tag
        {
            Id = 12,
            Name = "PostgreSQL",
            Synonyms = new List<TagSynonym>
            {
                new() { Id = 1, TagId = 12, Value = "Postgres" }
            }
        };

        // Text contains both PostgreSQL (len 10) and Postgres (len 8)
        var match = _service.MatchTag("Migrating from Postgres to PostgreSQL clusters", tag);

        Assert.NotNull(match);
        Assert.True(match.MatchedByTagName);
        Assert.Equal("PostgreSQL", match.MatchedTerm);
        Assert.Equal("по имени тега", match.Reason);
    }

    [Fact]
    public void DetailedMatch_LongestSynonymVsShortIntersection()
    {
        var tag = new Tag
        {
            Id = 13,
            Name = "Oracle",
            Synonyms = new List<TagSynonym>
            {
                new() { Id = 1, TagId = 13, Value = "оракл" }, // len 5
                new() { Id = 2, TagId = 13, Value = "база данных оракл" }, // len 17
                new() { Id = 3, TagId = 13, Value = "oracle db" } // len 9
            }
        };

        // Text has "база данных оракл" (len 17), which overlaps with "оракл" (len 5)
        var match = _service.MatchTag("На проде развернута база данных оракл для биллинга", tag);

        Assert.NotNull(match);
        Assert.False(match.MatchedByTagName);
        Assert.NotNull(match.MatchedSynonym);
        Assert.Equal("база данных оракл", match.MatchedTerm);
        Assert.Equal("по синониму «база данных оракл»", match.Reason);
    }

    [Fact]
    public void DetailedMatch_SynonymsEqualLength_StableOrder()
    {
        var tag = new Tag
        {
            Id = 14,
            Name = "System",
            Synonyms = new List<TagSynonym>
            {
                new() { Id = 101, TagId = 14, Value = "termA" }, // len 5
                new() { Id = 102, TagId = 14, Value = "termB" }  // len 5
            }
        };

        var match1 = _service.MatchTag("Using termA and termB concurrently", tag);
        var match2 = _service.MatchTag("Using termB and termA concurrently", tag);

        // Stable order must pick termA consistently
        Assert.NotNull(match1);
        Assert.NotNull(match2);
        Assert.Equal("termA", match1.MatchedTerm);
        Assert.Equal("termA", match2.MatchedTerm);
        Assert.Equal("по синониму «termA»", match1.Reason);
    }

    [Fact]
    public void DetailedMatch_CasingAndWhitespaceNormalization()
    {
        var tag = new Tag
        {
            Id = 15,
            Name = "Oracle",
            Synonyms = new List<TagSynonym>
            {
                new() { Id = 1, TagId = 15, Value = "  oracle   db  " },
                new() { Id = 2, TagId = 15, Value = "оракл" }
            }
        };

        // Case-insensitive matching with multiple spaces in synonym
        var match = _service.MatchTag("Issue on ORACLE   DB cluster", tag);
        Assert.NotNull(match);
        Assert.Equal("oracle db", match.MatchedTerm);
        Assert.Equal("по синониму «oracle db»", match.Reason);

        var matchRu = _service.MatchTag("Используем ОРАКЛ на сервере", tag);
        Assert.NotNull(matchRu);
        Assert.Equal("оракл", matchRu.MatchedTerm);
        Assert.Equal("по синониму «оракл»", matchRu.Reason);
    }

    [Fact]
    public void DetailedMatch_TechnicalTerms_CSharp_Cpp_DotNet_1C()
    {
        var csharpTag = new Tag { Id = 20, Name = "C#" };
        var cppTag = new Tag { Id = 21, Name = "C++" };
        var dotNetTag = new Tag { Id = 22, Name = ".NET" };
        var oneCTag = new Tag { Id = 23, Name = "1C" };
        var dotNetSynTag = new Tag
        {
            Id = 24,
            Name = "DotNet",
            Synonyms = new List<TagSynonym> { new() { Id = 1, TagId = 24, Value = ".NET" } }
        };

        // Exact technical boundaries
        var mCsharp = _service.MatchTag("Code in C# language", csharpTag);
        Assert.NotNull(mCsharp);
        Assert.True(mCsharp.MatchedByTagName);
        Assert.Equal("по имени тега", mCsharp.Reason);

        // Subword should not match
        Assert.Null(_service.MatchTag("Code in ABC# language", csharpTag));
        Assert.Null(_service.MatchTag("Code in C#sharp language", csharpTag));

        var mCpp = _service.MatchTag("Engine written in C++.", cppTag);
        Assert.NotNull(mCpp);
        Assert.Equal("по имени тега", mCpp.Reason);
        Assert.Null(_service.MatchTag("Engine written in C+++.", cppTag));

        var mDotNet = _service.MatchTag("Platform: .NET 8 runtime", dotNetTag);
        Assert.NotNull(mDotNet);
        Assert.Equal("по имени тега", mDotNet.Reason);
        Assert.Null(_service.MatchTag("Platform: ASP.NET runtime", dotNetTag));

        var m1C = _service.MatchTag("Интеграция с 1C завершена", oneCTag);
        Assert.NotNull(m1C);
        Assert.Equal("по имени тега", m1C.Reason);

        // Technical synonym match
        var mDotNetSyn = _service.MatchTag("Platform: .NET 8 runtime", dotNetSynTag);
        Assert.NotNull(mDotNetSyn);
        Assert.False(mDotNetSyn.MatchedByTagName);
        Assert.Equal("по синониму «.NET»", mDotNetSyn.Reason);
    }

    [Fact]
    public void RescanNote_Compatibility_DetectTagsAndRescanNoteWorkAsExpected()
    {
        var csharpTag = new Tag { Id = 1, Name = "C#" };
        var oracleTag = new Tag
        {
            Id = 2,
            Name = "Oracle",
            Synonyms = new List<TagSynonym> { new() { Id = 1, TagId = 2, Value = "оракл" } }
        };
        var manualTag = new Tag { Id = 3, Name = "ManualOnly" };
        var oldAutoTag = new Tag { Id = 4, Name = "OldAuto" };

        var note = new Note
        {
            Id = 100,
            Text = "C# and оракл are used here",
            NoteTags = new List<NoteTag>
            {
                new() { NoteId = 100, TagId = 3, Tag = manualTag, Origin = TagOrigin.Manual, IsSuppressed = false },
                new() { NoteId = 100, TagId = 4, Tag = oldAutoTag, Origin = TagOrigin.Auto, IsSuppressed = false }
            }
        };

        var allTags = new[] { csharpTag, oracleTag, manualTag, oldAutoTag };

        // 1. DetectTags returns C# and Oracle
        var detected = _service.DetectTags(note.Text, allTags);
        Assert.Equal(2, detected.Count);
        Assert.Contains(detected, t => t.Id == 1);
        Assert.Contains(detected, t => t.Id == 2);

        // 2. RescanNote: adds C# and Oracle as Auto, keeps ManualOnly, removes OldAuto
        _service.RescanNote(note, allTags);
        Assert.Equal(3, note.NoteTags.Count);

        var csharpLink = note.NoteTags.Single(nt => nt.TagId == 1);
        Assert.Equal(TagOrigin.Auto, csharpLink.Origin);
        Assert.False(csharpLink.IsSuppressed);

        var oracleLink = note.NoteTags.Single(nt => nt.TagId == 2);
        Assert.Equal(TagOrigin.Auto, oracleLink.Origin);
        Assert.False(oracleLink.IsSuppressed);

        var manualLink = note.NoteTags.Single(nt => nt.TagId == 3);
        Assert.Equal(TagOrigin.Manual, manualLink.Origin);
        Assert.False(manualLink.IsSuppressed);

        Assert.DoesNotContain(note.NoteTags, nt => nt.TagId == 4);
    }

    [Fact]
    public void NoteEditorViewModel_DisplaysCorrectReasons_OnCreateFromCapture()
    {
        var tag1 = new Tag { Id = 1, Name = "C#" };
        var tag2 = new Tag
        {
            Id = 2,
            Name = "Oracle",
            Synonyms = new List<TagSynonym> { new() { Id = 1, TagId = 2, Value = "оракл" } }
        };
        var allTags = new List<Tag> { tag1, tag2 };

        string captureText = "Developing with C# and оракл";
        var matches = _service.DetectDetailedTags(captureText, allTags);

        using var vm = new NoteEditorViewModel(
            _service,
            allTags,
            existingNote: null,
            initialText: captureText,
            initialAutoTags: matches.Select(m => m.Tag).ToList(),
            initialAutoMatches: matches,
            draftJournalService: NoOpDraftJournalService.Instance);

        Assert.Equal(2, vm.ActiveTags.Count);

        var csharpItem = vm.ActiveTags.Single(t => t.TagId == 1);
        Assert.Equal("C#", csharpItem.TagName);
        Assert.Equal(TagOrigin.Auto, csharpItem.Origin);
        Assert.Equal("по имени тега", csharpItem.Reason);

        var oracleItem = vm.ActiveTags.Single(t => t.TagId == 2);
        Assert.Equal("Oracle", oracleItem.TagName);
        Assert.Equal(TagOrigin.Auto, oracleItem.Origin);
        Assert.Equal("по синониму «оракл»", oracleItem.Reason);
    }

    [Fact]
    public void NoteEditorViewModel_DisplaysCorrectReasons_ExistingNoteAndManualTag()
    {
        var tag1 = new Tag { Id = 1, Name = "Postgres" };
        var tag2 = new Tag
        {
            Id = 2,
            Name = "Oracle",
            Synonyms = new List<TagSynonym> { new() { Id = 1, TagId = 2, Value = "оракл" } }
        };
        var tagManual = new Tag { Id = 3, Name = "Priority" };
        var tagSuppressed = new Tag { Id = 4, Name = "SuppressedTag" };
        var allTags = new List<Tag> { tag1, tag2, tagManual, tagSuppressed };

        var note = new Note
        {
            Id = 42,
            Text = "Migrating to Postgres and using оракл",
            NoteTags = new List<NoteTag>
            {
                new() { NoteId = 42, TagId = 1, Tag = tag1, Origin = TagOrigin.Auto, IsSuppressed = false },
                new() { NoteId = 42, TagId = 2, Tag = tag2, Origin = TagOrigin.Auto, IsSuppressed = false },
                new() { NoteId = 42, TagId = 3, Tag = tagManual, Origin = TagOrigin.Manual, IsSuppressed = false },
                new() { NoteId = 42, TagId = 4, Tag = tagSuppressed, Origin = TagOrigin.Auto, IsSuppressed = true }
            }
        };

        using var vm = new NoteEditorViewModel(_service, allTags, existingNote: note, draftJournalService: NoOpDraftJournalService.Instance);

        // Suppressed tag must NOT be in ActiveTags and must have no misleading reason
        Assert.Equal(3, vm.ActiveTags.Count);
        Assert.Contains(4, vm.SuppressedTagIds);

        var manualItem = vm.ActiveTags.Single(t => t.TagId == 3);
        Assert.Equal(TagOrigin.Manual, manualItem.Origin);
        Assert.Equal("добавлен вручную", manualItem.Reason);

        var postgresItem = vm.ActiveTags.Single(t => t.TagId == 1);
        Assert.Equal(TagOrigin.Auto, postgresItem.Origin);
        Assert.Equal("по имени тега", postgresItem.Reason);

        var oracleItem = vm.ActiveTags.Single(t => t.TagId == 2);
        Assert.Equal(TagOrigin.Auto, oracleItem.Origin);
        Assert.Equal("по синониму «оракл»", oracleItem.Reason);
    }

    [Fact]
    public void NoteEditorViewModel_DoesNotInventReasonForUnmatchedExistingAutoTag()
    {
        var tag = new Tag { Id = 1, Name = "Oracle" };
        var note = new Note
        {
            Id = 99,
            Text = "Текст без совпадения",
            NoteTags = new List<NoteTag>
            {
                new() { NoteId = 99, TagId = 1, Tag = tag, Origin = TagOrigin.Auto, IsSuppressed = false }
            }
        };

        using var vm = new NoteEditorViewModel(_service, new List<Tag> { tag }, existingNote: note, draftJournalService: NoOpDraftJournalService.Instance);

        Assert.Single(vm.ActiveTags);
        Assert.Equal("автотег сохранён ранее (совпадение сейчас не найдено)", vm.ActiveTags[0].Reason);
    }

    [Fact]
    public void NoteEditorViewModel_AddTagCommand_SetsManualReason()
    {
        var tag = new Tag { Id = 10, Name = "CustomTag" };
        var allTags = new List<Tag> { tag };

        using var vm = new NoteEditorViewModel(_service, allTags, existingNote: null, initialText: "Some random text", draftJournalService: NoOpDraftJournalService.Instance);
        Assert.Empty(vm.ActiveTags);

        vm.SelectedTagToAdd = tag;
        vm.AddTagCommand.Execute(null);

        Assert.Single(vm.ActiveTags);
        var added = vm.ActiveTags.Single();
        Assert.Equal(10, added.TagId);
        Assert.Equal("CustomTag", added.TagName);
        Assert.Equal(TagOrigin.Manual, added.Origin);
        Assert.Equal("добавлен вручную", added.Reason);
    }

    [Fact]
    public void NoteEditorViewModel_Rescan_UpdatesReasonsAndPreservesManualAndSuppressed()
    {
        var tag1 = new Tag
        {
            Id = 1,
            Name = "DB",
            Synonyms = new List<TagSynonym> { new() { Id = 1, TagId = 1, Value = "Database" } }
        };
        var tag2 = new Tag { Id = 2, Name = "Temporary" };
        var tag3 = new Tag { Id = 3, Name = "Manual" };
        var allTags = new List<Tag> { tag1, tag2, tag3 };

        // Start note with text matching DB and Temporary, plus manual tag
        var note = new Note
        {
            Id = 50,
            Text = "DB and Temporary",
            NoteTags = new List<NoteTag>
            {
                new() { NoteId = 50, TagId = 1, Tag = tag1, Origin = TagOrigin.Auto, IsSuppressed = false },
                new() { NoteId = 50, TagId = 2, Tag = tag2, Origin = TagOrigin.Auto, IsSuppressed = false },
                new() { NoteId = 50, TagId = 3, Tag = tag3, Origin = TagOrigin.Manual, IsSuppressed = false }
            }
        };

        using var vm = new NoteEditorViewModel(_service, allTags, existingNote: note, draftJournalService: NoOpDraftJournalService.Instance);
        Assert.Equal(3, vm.ActiveTags.Count);
        Assert.Equal("по имени тега", vm.ActiveTags.Single(t => t.TagId == 1).Reason);

        // User changes text: "Database" (synonym of tag 1) replaces "DB", "Temporary" is removed
        vm.Text = "Using Database here";
        vm.Rescan();

        // 1. Tag 1 reason updated to synonym
        var tag1Item = vm.ActiveTags.Single(t => t.TagId == 1);
        Assert.Equal(TagOrigin.Auto, tag1Item.Origin);
        Assert.Equal("по синониму «Database»", tag1Item.Reason);

        // 2. Tag 2 (undetected Auto) removed
        Assert.DoesNotContain(vm.ActiveTags, t => t.TagId == 2);

        // 3. Manual tag preserved with "добавлен вручную"
        var manualItem = vm.ActiveTags.Single(t => t.TagId == 3);
        Assert.Equal(TagOrigin.Manual, manualItem.Origin);
        Assert.Equal("добавлен вручную", manualItem.Reason);

        // 4. Remove Tag 1 (becomes suppressed):
        vm.RemoveTagCommand.Execute(tag1Item);
        Assert.DoesNotContain(vm.ActiveTags, t => t.TagId == 1);
        Assert.Contains(1, vm.SuppressedTagIds);

        // 5. Rescan with same text: suppressed Tag 1 is NOT re-added
        vm.Rescan();
        Assert.DoesNotContain(vm.ActiveTags, t => t.TagId == 1);
    }
}
