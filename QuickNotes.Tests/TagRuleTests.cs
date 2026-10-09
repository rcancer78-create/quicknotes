using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.ViewModels;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public class TagRuleTests : IDisposable
{
    private readonly string _tempDirectory;

    public TagRuleTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "QuickNotes_TagRuleTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public void AllTerms_Semantics_RequiresAllTermsPresent()
    {
        var tag = new Tag { Id = 1, Name = "Backend" };
        var rule = new TagRule
        {
            TagId = 1,
            RequiredTerms = new List<string> { "api", "server" }
        };

        var service = new TagDetectionService();

        // 1. Only "api" present -> no match
        var match1 = service.MatchTag("Working on backend api today", tag, rule);
        Assert.Null(match1);

        // 2. Both "api" and "server" present -> matches
        var match2 = service.MatchTag("Deploying backend api to the server", tag, rule);
        Assert.NotNull(match2);
        Assert.Equal("Backend", match2.Tag.Name);
        Assert.Equal("по имени тега", match2.BaseReason);
        Assert.Equal("все: api, server", match2.RuleDescription);
        Assert.Equal("по имени тега (правило: все: api, server)", match2.Reason);
    }

    [Fact]
    public void AnyTerms_Semantics_RequiresAtLeastOneTermWhenNonEmpty()
    {
        var tag = new Tag { Id = 2, Name = "Deploy" };
        var rule = new TagRule
        {
            TagId = 2,
            AnyTerms = new List<string> { "docker", "k8s", "podman" }
        };

        var service = new TagDetectionService();

        // 1. None of any terms -> no match
        var match1 = service.MatchTag("Manual deploy process documentation", tag, rule);
        Assert.Null(match1);

        // 2. One of any terms ("k8s") present -> match
        var match2 = service.MatchTag("Automated deploy with k8s cluster", tag, rule);
        Assert.NotNull(match2);
        Assert.Equal("Deploy", match2.Tag.Name);
        Assert.Equal("любое: docker, k8s, podman", match2.RuleDescription);

        // 3. Empty AnyTerms -> passes trivially
        var ruleEmptyAny = new TagRule
        {
            TagId = 2,
            AnyTerms = new List<string>()
        };
        var match3 = service.MatchTag("Manual deploy process documentation", tag, ruleEmptyAny);
        Assert.NotNull(match3);
    }

    [Fact]
    public void ExcludedTerms_Semantics_RejectsWhenAnyExcludedTermPresent()
    {
        var tag = new Tag { Id = 3, Name = "Finance" };
        var rule = new TagRule
        {
            TagId = 3,
            ExcludedTerms = new List<string> { "draft", "archive", "test" }
        };

        var service = new TagDetectionService();

        // 1. Without excluded terms -> matches
        var match1 = service.MatchTag("Finance quarterly results summary", tag, rule);
        Assert.NotNull(match1);
        Assert.Equal("Finance", match1.Tag.Name);
        Assert.Equal("кроме: draft, archive, test", match1.RuleDescription);

        // 2. Contains "draft" -> rejected
        var matchDraft = service.MatchTag("Finance quarterly results (draft version)", tag, rule);
        Assert.Null(matchDraft);

        // 3. Contains "archive" -> rejected
        var matchArchive = service.MatchTag("Finance archive records", tag, rule);
        Assert.Null(matchArchive);

        // 4. Contains "test" -> rejected
        var matchTest = service.MatchTag("Finance test calculations", tag, rule);
        Assert.Null(matchTest);
    }

    [Fact]
    public void CombinedRules_All_Any_Excluded_And_SynonymMatching()
    {
        var tag = new Tag
        {
            Id = 4,
            Name = "Database",
            Synonyms = new List<TagSynonym>
            {
                new() { Id = 1, TagId = 4, Value = "хранилище" }
            }
        };

        var rule = new TagRule
        {
            TagId = 4,
            RequiredTerms = new List<string> { "prod" },
            AnyTerms = new List<string> { "postgres", "oracle" },
            ExcludedTerms = new List<string> { "mock", "deprecated" }
        };

        var service = new TagDetectionService();

        // 1. All conditions satisfied (matched via synonym "хранилище")
        string text1 = "Основное хранилище prod на базе postgres";
        var match1 = service.MatchTag(text1, tag, rule);
        Assert.NotNull(match1);
        Assert.False(match1.MatchedByTagName);
        Assert.Equal("хранилище", match1.MatchedTerm);
        Assert.Equal("по синониму «хранилище»", match1.BaseReason);
        Assert.Equal("все: prod; любое: postgres, oracle; кроме: mock, deprecated", match1.RuleDescription);
        Assert.Equal("по синониму «хранилище» (правило: все: prod; любое: postgres, oracle; кроме: mock, deprecated)", match1.Reason);

        // 2. Missing required term "prod" -> null
        string text2 = "Основное хранилище dev на базе postgres";
        Assert.Null(service.MatchTag(text2, tag, rule));

        // 3. Missing any terms (neither postgres nor oracle) -> null
        string text3 = "Основное хранилище prod на базе sqlite";
        Assert.Null(service.MatchTag(text3, tag, rule));

        // 4. Contains excluded term "mock" -> null
        string text4 = "Основное хранилище prod на базе postgres с mock данными";
        Assert.Null(service.MatchTag(text4, tag, rule));

        // 5. No base match at all -> null
        string text5 = "Обычный текст prod postgres";
        Assert.Null(service.MatchTag(text5, tag, rule));
    }

    [Fact]
    public void Case_And_TechnicalBoundaries_PreservedInRules()
    {
        var tag = new Tag { Id = 5, Name = "Core" };
        var rule = new TagRule
        {
            TagId = 5,
            RequiredTerms = new List<string> { ".NET", "C#", "C++", "1C" }
        };

        var service = new TagDetectionService();

        // Exact boundaries -> match
        string textOk = "Core platform: .NET 8, C#, C++, and 1C integration.";
        var matchOk = service.MatchTag(textOk, tag, rule);
        Assert.NotNull(matchOk);

        // Substrings (.NET inside ASP.NET, C# inside ABC#) -> fail
        string textSubword = "Core platform: ASP.NET, ABC#, and C+++.";
        var matchSub = service.MatchTag(textSubword, tag, rule);
        Assert.Null(matchSub);

        // Whitespace and case-insensitivity in rule terms
        var ruleWhitespace = new TagRule
        {
            TagId = 5,
            RequiredTerms = new List<string> { "oracle   db" }
        };
        var matchCase = service.MatchTag("Core module using ORACLE DB cluster", tag, ruleWhitespace);
        Assert.NotNull(matchCase);
    }

    [Fact]
    public void DefaultCompatibility_NoRules_BehavesIdentically()
    {
        var tag = new Tag { Id = 6, Name = "Frontend" };
        var service = new TagDetectionService();

        var match = service.MatchTag("Modern Frontend architecture", tag, (TagRule?)null);
        Assert.NotNull(match);
        Assert.Equal("Frontend", match.Tag.Name);
        Assert.Null(match.RuleDescription);
        Assert.Null(match.AppliedRule);
        Assert.Equal("по имени тега", match.BaseReason);
        Assert.Equal("по имени тега", match.Reason);

        var detected = service.DetectTags("Modern Frontend architecture", new[] { tag });
        Assert.Single(detected);
        Assert.Equal("Frontend", detected[0].Name);
    }

    [Fact]
    public void JsonRoundtrip_And_AtomicPersistence()
    {
        string filePath = Path.Combine(_tempDirectory, "rules.json");
        var store = new TagRuleService(filePath);

        var rule1 = new TagRule
        {
            TagId = 10,
            RequiredTerms = new List<string> { "server", "linux" },
            AnyTerms = new List<string> { "cloud", "onprem" },
            ExcludedTerms = new List<string> { "deprecated" }
        };

        var rule2 = new TagRule
        {
            TagId = 20,
            ExcludedTerms = new List<string> { "test" }
        };

        store.SaveRule(rule1);
        store.SaveRule(rule2);

        Assert.True(File.Exists(filePath));
        var tmpFiles = Directory.GetFiles(_tempDirectory, "*.tmp");
        Assert.Empty(tmpFiles);

        // Reload fresh from disk
        var reloadedStore = new TagRuleService(filePath);
        var loaded1 = reloadedStore.GetRule(10);
        var loaded2 = reloadedStore.GetRule(20);

        Assert.NotNull(loaded1);
        Assert.Equal(new[] { "server", "linux" }, loaded1.RequiredTerms);
        Assert.Equal(new[] { "cloud", "onprem" }, loaded1.AnyTerms);
        Assert.Equal(new[] { "deprecated" }, loaded1.ExcludedTerms);

        Assert.NotNull(loaded2);
        Assert.Empty(loaded2.RequiredTerms);
        Assert.Empty(loaded2.AnyTerms);
        Assert.Equal(new[] { "test" }, loaded2.ExcludedTerms);

        // Delete rule 20
        bool deleted = reloadedStore.DeleteRule(20);
        Assert.True(deleted);

        var afterDeleteStore = new TagRuleService(filePath);
        Assert.Null(afterDeleteStore.GetRule(20));
        Assert.NotNull(afterDeleteStore.GetRule(10));
    }

    [Fact]
    public void SafeLoading_CorruptedFile_DoesNotThrowAndSetsWarning()
    {
        string filePath = Path.Combine(_tempDirectory, "corrupted_rules.json");
        File.WriteAllText(filePath, "{ invalid json structure [[[ :::");

        var store = new TagRuleService(filePath);

        Assert.NotNull(store.LoadWarning);
        Assert.Contains("Не удалось загрузить", store.LoadWarning);
        Assert.Empty(store.GetAllRules());
    }

    [Fact]
    public void TagRuleViewModel_NoMutations_Before_Save_And_CancelLeavesStoreUntouched()
    {
        string filePath = Path.Combine(_tempDirectory, "cancel_test_rules.json");
        var store = new TagRuleService(filePath);

        var originalRule = new TagRule
        {
            TagId = 77,
            RequiredTerms = new List<string> { "initial" }
        };
        store.SaveRule(originalRule);

        var tag = new Tag { Id = 77, Name = "Security" };

        // 1. Open ViewModel and modify collections
        var vm = new TagRuleViewModel(tag, store.GetRule(77), store);
        vm.NewRequiredTerm = "addedRequired";
        vm.AddRequiredTermCommand.Execute(null);

        vm.NewExcludedTerm = "addedExcluded";
        vm.AddExcludedTermCommand.Execute(null);

        vm.DeleteRequiredTermCommand.Execute("initial");

        Assert.Single(vm.RequiredTerms);
        Assert.Equal("addedRequired", vm.RequiredTerms[0]);
        Assert.Single(vm.ExcludedTerms);
        Assert.Equal("addedExcluded", vm.ExcludedTerms[0]);

        // Store MUST NOT be mutated yet!
        var inStoreBeforeCancel = store.GetRule(77);
        Assert.NotNull(inStoreBeforeCancel);
        Assert.Equal(new[] { "initial" }, inStoreBeforeCancel.RequiredTerms);
        Assert.Empty(inStoreBeforeCancel.ExcludedTerms);

        // 2. User cancels
        vm.CancelCommand.Execute(null);

        var inStoreAfterCancel = store.GetRule(77);
        Assert.NotNull(inStoreAfterCancel);
        Assert.Equal(new[] { "initial" }, inStoreAfterCancel.RequiredTerms);
        Assert.Empty(inStoreAfterCancel.ExcludedTerms);

        // 3. Re-open, modify, and Save
        var vm2 = new TagRuleViewModel(tag, store.GetRule(77), store);
        vm2.NewRequiredTerm = "persistedTerm";
        vm2.AddRequiredTermCommand.Execute(null);
        vm2.SaveCommand.Execute(null);

        var inStoreAfterSave = store.GetRule(77);
        Assert.NotNull(inStoreAfterSave);
        Assert.Contains("persistedTerm", inStoreAfterSave.RequiredTerms);
    }

    [Fact]
    public void TagRuleService_Normalization_And_DuplicateProtection()
    {
        string filePath = Path.Combine(_tempDirectory, "dedup_rules.json");
        var store = new TagRuleService(filePath);

        var rule = new TagRule
        {
            TagId = 88,
            RequiredTerms = new List<string> { "  term1  ", "TERM1", "   ", "", "term2" },
            AnyTerms = new List<string> { "any1", "any1", " ANY1 " },
            ExcludedTerms = new List<string> { "ex1", "ex2", "EX1" }
        };

        store.SaveRule(rule);

        var saved = store.GetRule(88);
        Assert.NotNull(saved);
        Assert.Equal(2, saved.RequiredTerms.Count);
        Assert.Contains(saved.RequiredTerms, t => string.Equals(t, "term1", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(saved.RequiredTerms, t => string.Equals(t, "term2", StringComparison.OrdinalIgnoreCase));

        Assert.Single(saved.AnyTerms);
        Assert.Equal("any1", saved.AnyTerms[0], ignoreCase: true);

        Assert.Equal(2, saved.ExcludedTerms.Count);
    }

    [Fact]
    public void TagRescanPreview_UsesRules()
    {
        var tagDeploy = new Tag { Id = 1, Name = "Deploy" };
        var ruleDeploy = new TagRule
        {
            TagId = 1,
            ExcludedTerms = new List<string> { "production" }
        };

        var rules = new Dictionary<int, TagRule> { { 1, ruleDeploy } };
        var previewService = new TagRescanPreviewService();

        var noteProd = new Note
        {
            Id = 10,
            Text = "Deploy script for production server",
            NoteTags = new List<NoteTag>()
        };

        var noteStaging = new Note
        {
            Id = 11,
            Text = "Deploy script for staging cluster",
            NoteTags = new List<NoteTag>()
        };

        // Note 10 contains "production" (excluded) -> should NOT get Deploy
        // Note 11 does not contain "production" -> SHOULD get Deploy
        var preview = previewService.CalculatePreview(new[] { noteProd, noteStaging }, new[] { tagDeploy }, rules);

        Assert.Equal(1, preview.TotalNotesWithChanges);
        Assert.Equal(1, preview.TotalAddedTags);
        Assert.Single(preview.NoteChanges);
        Assert.Equal(11, preview.NoteChanges[0].NoteId);
        Assert.Contains("Deploy", preview.NoteChanges[0].AddedTagNames);
    }

    [Fact]
    public void RescanNote_UsesRules_RemovesUndetectedAutoTags()
    {
        var tag = new Tag { Id = 1, Name = "Release" };
        var rule = new TagRule
        {
            TagId = 1,
            ExcludedTerms = new List<string> { "hotfix" }
        };

        var note = new Note
        {
            Id = 5,
            Text = "Release notes for version 2.1 (hotfix)",
            NoteTags = new List<NoteTag>
            {
                new() { NoteId = 5, TagId = 1, Tag = tag, Origin = TagOrigin.Auto, IsSuppressed = false }
            }
        };

        var service = new TagDetectionService();
        var rules = new Dictionary<int, TagRule> { { 1, rule } };

        // Before rule or without rule: "Release" is detected
        // With rule: "hotfix" is present, so "Release" is no longer detected and gets removed from note
        service.RescanNote(note, new[] { tag }, rules);

        Assert.Empty(note.NoteTags);
    }

    [Fact]
    public void Suppressed_Manual_Deleted_Preserved_WithRules()
    {
        var tagManual = new Tag { Id = 1, Name = "Important" };
        var tagAuto = new Tag { Id = 2, Name = "Automation" };
        var tagSuppressed = new Tag { Id = 3, Name = "Suppressed" };

        var ruleManual = new TagRule { TagId = 1, RequiredTerms = new List<string> { "never_matches" } };
        var ruleSuppressed = new TagRule { TagId = 3 }; // No extra conditions

        var rules = new Dictionary<int, TagRule>
        {
            { 1, ruleManual },
            { 3, ruleSuppressed }
        };

        var note = new Note
        {
            Id = 100,
            Text = "Automation and Suppressed mentioned here",
            NoteTags = new List<NoteTag>
            {
                new() { NoteId = 100, TagId = 1, Tag = tagManual, Origin = TagOrigin.Manual, IsSuppressed = false },
                new() { NoteId = 100, TagId = 3, Tag = tagSuppressed, Origin = TagOrigin.Auto, IsSuppressed = true }
            }
        };

        var service = new TagDetectionService();
        service.RescanNote(note, new[] { tagManual, tagAuto, tagSuppressed }, rules);

        // 1. Manual tag kept despite rule requiring "never_matches"
        var manualLink = note.NoteTags.Single(nt => nt.TagId == 1);
        Assert.Equal(TagOrigin.Manual, manualLink.Origin);

        // 2. Automation added as Auto
        var autoLink = note.NoteTags.Single(nt => nt.TagId == 2);
        Assert.Equal(TagOrigin.Auto, autoLink.Origin);
        Assert.False(autoLink.IsSuppressed);

        // 3. Suppressed tag kept as suppressed (not made active)
        var suppLink = note.NoteTags.Single(nt => nt.TagId == 3);
        Assert.True(suppLink.IsSuppressed);

        // 4. Deleted note ignored in CalculatePreview
        var deletedNote = new Note
        {
            Id = 101,
            Text = "Automation mentioned",
            DeletedAt = DateTime.Now,
            NoteTags = new List<NoteTag>()
        };
        var previewService = new TagRescanPreviewService();
        var preview = previewService.CalculatePreview(new[] { deletedNote }, new[] { tagAuto }, rules);
        Assert.Equal(0, preview.TotalNotesScanned);
        Assert.Equal(0, preview.TotalAddedTags);
    }

    [Fact]
    public void NoteEditorViewModel_Rescan_And_Capture_RespectsRules()
    {
        var tag = new Tag { Id = 1, Name = "Deploy" };
        var rule = new TagRule
        {
            TagId = 1,
            RequiredTerms = new List<string> { "kubernetes" }
        };

        var allTags = new List<Tag> { tag };
        var rules = new Dictionary<int, TagRule> { { 1, rule } };
        var service = new TagDetectionService();

        // 1. Initial text has "Deploy", but not "kubernetes" -> no active tags
        using var vm = new NoteEditorViewModel(
            service,
            allTags,
            existingNote: null,
            initialText: "Deploy new service",
            rules: rules,
            draftJournalService: NoOpDraftJournalService.Instance);

        Assert.Empty(vm.ActiveTags);

        // 2. User adds "kubernetes" and rescans -> "Deploy" matches with rule explanation!
        vm.Text = "Deploy new service on kubernetes";
        vm.Rescan();

        Assert.Single(vm.ActiveTags);
        var activeTag = vm.ActiveTags[0];
        Assert.Equal("Deploy", activeTag.TagName);
        Assert.Equal("по имени тега (правило: все: kubernetes)", activeTag.Reason);

        // 3. User removes "kubernetes" and rescans -> "Deploy" is removed
        vm.Text = "Deploy new service on baremetal";
        vm.Rescan();

        Assert.Empty(vm.ActiveTags);
    }
}
