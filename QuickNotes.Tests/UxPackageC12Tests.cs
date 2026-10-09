using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.ViewModels;
using Xunit;

namespace QuickNotes.Tests;

public sealed class UxPackageC12Tests
{
    [Fact]
    public void TagOriginCopy_UsesCompactRussianMarkers()
    {
        Assert.Equal("вручную", TagOriginCopy.Marker(TagOrigin.Manual));
        Assert.Equal("авто", TagOriginCopy.Marker(TagOrigin.Auto));
        Assert.Equal("скрыт", TagOriginCopy.Marker(TagOrigin.Auto, isSuppressed: true));
        Assert.Equal("Вернуть", TagOriginCopy.RestoreAction);
        Assert.Contains("вручную", TagOriginCopy.Tooltip(TagOrigin.Manual), StringComparison.Ordinal);
        Assert.Contains("автоматически", TagOriginCopy.Tooltip(TagOrigin.Auto), StringComparison.Ordinal);
        Assert.Contains("вернуть", TagOriginCopy.Tooltip(TagOrigin.Auto, isSuppressed: true), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoteEditor_ShowsOriginAndRestoresSuppressedAutoTag()
    {
        var autoTag = new Tag { Id = 1, Name = "Postgres" };
        var manualTag = new Tag { Id = 3, Name = "Priority" };
        var hiddenTag = new Tag { Id = 4, Name = "SuppressedTag" };
        var allTags = new List<Tag> { autoTag, manualTag, hiddenTag };
        var note = new Note
        {
            Id = 42,
            Text = "Migrating to Postgres",
            NoteTags = new List<NoteTag>
            {
                new() { NoteId = 42, TagId = 1, Tag = autoTag, Origin = TagOrigin.Auto, IsSuppressed = false },
                new() { NoteId = 42, TagId = 3, Tag = manualTag, Origin = TagOrigin.Manual, IsSuppressed = false },
                new() { NoteId = 42, TagId = 4, Tag = hiddenTag, Origin = TagOrigin.Auto, IsSuppressed = true }
            }
        };

        using var vm = new NoteEditorViewModel(new TagDetectionService(), allTags, existingNote: note, draftJournalService: NoOpDraftJournalService.Instance);

        Assert.Equal(2, vm.ActiveTags.Count);
        Assert.True(vm.HasSuppressedTags);
        Assert.Single(vm.SuppressedTags);
        Assert.Equal("авто", vm.ActiveTags.Single(t => t.TagId == 1).OriginMarker);
        Assert.Equal("вручную", vm.ActiveTags.Single(t => t.TagId == 3).OriginMarker);
        Assert.Equal("скрыт", vm.SuppressedTags[0].OriginMarker);
        Assert.Equal(4, vm.SuppressedTags[0].TagId);

        vm.RestoreSuppressedTagCommand.Execute(vm.SuppressedTags[0]);
        Assert.False(vm.HasSuppressedTags);
        Assert.DoesNotContain(4, vm.SuppressedTagIds);
        var restored = vm.ActiveTags.Single(t => t.TagId == 4);
        Assert.Equal(TagOrigin.Auto, restored.Origin);
        Assert.Equal(TagOriginCopy.RestoredAutoReason, restored.Reason);
        Assert.Equal("авто", restored.OriginMarker);
    }

    [Fact]
    public void TagRuleAndRescanCopy_HasNoEnglishAllAnyExcluded()
    {
        var vm = new TagRuleViewModel(new Tag { Id = 1, Name = "Работа" });
        Assert.DoesNotContain("(All)", vm.Explanation, StringComparison.Ordinal);
        Assert.DoesNotContain("(Any)", vm.Explanation, StringComparison.Ordinal);
        Assert.DoesNotContain("(Excluded)", vm.Explanation, StringComparison.Ordinal);
        Assert.Contains("Обязательные (все)", vm.Explanation, StringComparison.Ordinal);

        var preview = new TagRescanPreviewViewModel(new TagRescanPreviewResult());
        Assert.DoesNotContain("(Auto)", preview.WarningMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("(Manual)", preview.WarningMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("(Suppressed)", preview.WarningMessage, StringComparison.Ordinal);
        Assert.Contains("скрытые автотеги", preview.WarningMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TagSurfaces_BindOriginMarkerAndRestore_AndKeepCardsClean()
    {
        string root = FindSolutionRoot();
        string editor = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "Views", "NoteEditorWindow.xaml"));
        string main = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "Views", "MainWindow.xaml"));
        string rules = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "Views", "TagRuleDialog.xaml"));

        Assert.Contains("Binding OriginMarker", editor, StringComparison.Ordinal);
        Assert.Contains("RestoreSuppressedTagCommand", editor, StringComparison.Ordinal);
        Assert.Contains("Скрытые автотеги", editor, StringComparison.Ordinal);
        Assert.Contains("Binding OriginMarker", main, StringComparison.Ordinal);
        Assert.Contains("RestoreSuppressedTagCommand", main, StringComparison.Ordinal);
        Assert.DoesNotContain("(All)", rules, StringComparison.Ordinal);
        Assert.DoesNotContain("(Any)", rules, StringComparison.Ordinal);
        Assert.DoesNotContain("(Excluded)", rules, StringComparison.Ordinal);

        int start = main.IndexOf("DataType=\"{x:Type vm:NoteCardViewModel}\"", StringComparison.Ordinal);
        Assert.True(start >= 0);
        int end = main.IndexOf("</DataTemplate>", start, StringComparison.Ordinal);
        string card = main.Substring(start, end - start);
        Assert.DoesNotContain("OriginMarker", card, StringComparison.Ordinal);
        Assert.DoesNotContain("SuppressedTags", card, StringComparison.Ordinal);
    }

    private static string FindSolutionRoot()
    {
        var current = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "QuickNotes.sln")))
            {
                return current;
            }

            var parent = Directory.GetParent(current);
            if (parent == null)
            {
                break;
            }

            current = parent.FullName;
        }

        return @"D:\work\QuickNotes";
    }
}
