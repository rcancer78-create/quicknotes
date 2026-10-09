using System;
using System.IO;
using System.Linq;
using System.Windows;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.ViewModels;
using QuickNotes.App.Views;
using Xunit;

namespace QuickNotes.Tests;

public sealed class MarkdownTaskNavigatorTests
{
    [Fact]
    public void Resolve_ExactTask_SelectsSameLine()
    {
        string text = "head\n- [ ] keep me\n";
        var locator = MarkdownTaskParser.Parse(text).Single();
        var result = MarkdownTaskNavigator.Resolve(text, locator);
        Assert.False(result.IsFallback);
        Assert.Equal(locator.LineNumber, result.LineNumber);
        Assert.Equal(locator.LineStartCharIndex, result.SelectionStart);
        Assert.True(result.SelectionStart + result.SelectionLength <= text.Length);
    }

    [Fact]
    public void Resolve_LinesInsertedAbove_FindsOriginalTask()
    {
        string original = "- [ ] unique body\n";
        var locator = MarkdownTaskParser.Parse(original).Single();
        string shifted = "new\nnew\n" + original;
        var result = MarkdownTaskNavigator.Resolve(shifted, locator);
        Assert.False(result.IsFallback);
        Assert.Equal(3, result.LineNumber);
        Assert.Contains("unique body", shifted.Substring(result.SelectionStart, result.SelectionLength), StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ChangedOrRemoved_FallsBackWithoutForeignCheckbox()
    {
        string original = "- [ ] alpha\n- [ ] beta\n";
        var alpha = MarkdownTaskParser.Parse(original)[0];
        string changed = "- [ ] gamma\n- [ ] beta\n";
        var result = MarkdownTaskNavigator.Resolve(changed, alpha);
        Assert.True(result.IsFallback);
        Assert.Equal(0, result.SelectionStart);
        Assert.Equal(0, result.SelectionLength);
    }

    [Fact]
    public void Resolve_DuplicateAmbiguityAfterRemoval_FallsBack()
    {
        string original = "- [ ] twin\n- [ ] twin\n";
        var second = MarkdownTaskParser.Parse(original)[1];
        Assert.Equal(2, second.SameFingerprintCount);
        string remaining = "- [ ] twin\n";
        var result = MarkdownTaskNavigator.Resolve(remaining, second);
        Assert.True(result.IsFallback);
        Assert.Equal(0, result.SelectionLength);
    }

    [Fact]
    public void Resolve_DuplicateExactLineUnchanged_HitsSameLine()
    {
        string original = "- [ ] twin\n- [ ] twin\n";
        var second = MarkdownTaskParser.Parse(original)[1];
        Assert.Equal(2, second.SameFingerprintCount);
        var result = MarkdownTaskNavigator.Resolve(original, second);
        Assert.False(result.IsFallback);
        Assert.Equal(second.LineNumber, result.LineNumber);
        Assert.Equal(second.LineStartCharIndex, result.SelectionStart);
    }

    [Fact]
    public void Resolve_DuplicateSwapOnSameLineNumbers_AfterSnapshotChange_FallsBack()
    {
        string original = "keep-alpha\n- [ ] twin\nkeep-beta\n- [ ] twin\n";
        var first = MarkdownTaskParser.Parse(original)[0];
        var second = MarkdownTaskParser.Parse(original)[1];
        Assert.Equal(2, first.LineNumber);
        Assert.Equal(4, second.LineNumber);
        string swapped = "keep-beta\n- [ ] twin\nkeep-alpha\n- [ ] twin\n";
        var current = MarkdownTaskParser.Parse(swapped);
        Assert.Equal(2, current[0].LineNumber);
        Assert.Equal(4, current[1].LineNumber);
        Assert.NotEqual(
            MarkdownTaskParser.ComputeNoteSnapshotProof(original),
            MarkdownTaskParser.ComputeNoteSnapshotProof(swapped));

        var firstResult = MarkdownTaskNavigator.Resolve(swapped, first);
        var secondResult = MarkdownTaskNavigator.Resolve(swapped, second);
        Assert.True(firstResult.IsFallback);
        Assert.True(secondResult.IsFallback);
        Assert.Equal(0, firstResult.SelectionLength);
        Assert.Equal(0, secondResult.SelectionLength);
    }

    [Fact]
    public void Resolve_DuplicateUnrelatedSnapshotChange_FallsBackEvenIfExactLineRemains()
    {
        string original = "- [ ] twin\n- [ ] twin\n";
        var second = MarkdownTaskParser.Parse(original)[1];
        string changed = "- [ ] twin\n- [ ] twin\nunrelated line\n";
        var current = MarkdownTaskParser.Parse(changed);
        Assert.Equal(2, current[1].LineNumber);
        Assert.Equal(second.LineNumber, current[1].LineNumber);

        var result = MarkdownTaskNavigator.Resolve(changed, second);
        Assert.True(result.IsFallback);
        Assert.Equal(0, result.SelectionStart);
        Assert.Equal(0, result.SelectionLength);
    }

    [Fact]
    public void Resolve_DuplicateAndUnique_DoNotSurfaceSnapshotProof()
    {
        string original = "- [ ] twin\n- [ ] twin\n- [ ] unique body\n";
        var locators = MarkdownTaskParser.Parse(original);
        var duplicate = locators[1];
        var unique = locators[2];
        string hex = Convert.ToHexString(duplicate.NoteSnapshotProof);
        var card = new TaskCardViewModel(duplicate);
        Assert.DoesNotContain(hex, card.AccessibilityName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(hex, card.TaskText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(hex, card.NoteTitle, StringComparison.OrdinalIgnoreCase);

        var hit = MarkdownTaskNavigator.Resolve(original, duplicate);
        var uniqueHit = MarkdownTaskNavigator.Resolve("pre\n" + original, unique);
        Assert.False(hit.IsFallback);
        Assert.False(uniqueHit.IsFallback);
        Assert.DoesNotContain(hex, MainViewModel.TaskNavigationFallbackStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_DuplicateSecond_WhenInsertsMakeNearestTheFirst_FallsBack()
    {
        string original = "- [ ] twin\n- [ ] twin\n";
        var second = MarkdownTaskParser.Parse(original)[1];
        string shifted = "- [ ] twin\ninserted\ninserted\ninserted\n- [ ] twin\n";
        var current = MarkdownTaskParser.Parse(shifted);
        Assert.Equal(2, current.Count);
        int nearestLine = current
            .OrderBy(t => Math.Abs(t.LineNumber - second.LineNumber))
            .ThenBy(t => t.LineNumber)
            .First()
            .LineNumber;
        Assert.Equal(1, nearestLine);

        var result = MarkdownTaskNavigator.Resolve(shifted, second);
        Assert.True(result.IsFallback);
        Assert.Equal(0, result.SelectionStart);
        Assert.Equal(0, result.SelectionLength);
    }

    [Fact]
    public void Resolve_DuplicateSecond_WhenReorderedSoNearestIsWrong_FallsBack()
    {
        string original = "- [ ] twin\n- [ ] twin\n";
        var second = MarkdownTaskParser.Parse(original)[1];
        Assert.Equal(2, second.LineNumber);
        string reordered = "pad\npad\npad\n- [ ] twin\n- [ ] twin\n";
        var current = MarkdownTaskParser.Parse(reordered);
        int nearestLine = current
            .OrderBy(t => Math.Abs(t.LineNumber - second.LineNumber))
            .ThenBy(t => t.LineNumber)
            .First()
            .LineNumber;
        Assert.Equal(4, nearestLine);
        Assert.NotEqual(5, nearestLine);

        var result = MarkdownTaskNavigator.Resolve(reordered, second);
        Assert.True(result.IsFallback);
        Assert.Equal(0, result.SelectionLength);
    }

    [Fact]
    public void Resolve_UniqueTask_AfterLineInsert_StillFound()
    {
        string original = "# H\n\n- [ ] unique body\n";
        var locator = MarkdownTaskParser.Parse(original).Single();
        string shifted = "pre\npre\npre\n" + original;
        var result = MarkdownTaskNavigator.Resolve(shifted, locator);
        Assert.False(result.IsFallback);
        Assert.Contains("unique body", shifted.Substring(result.SelectionStart, result.SelectionLength), StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_DuplicateWithInsertAbove_DoesNotUseNearestHint()
    {
        string original = "x\n- [ ] twin\n- [ ] twin\n";
        var first = MarkdownTaskParser.Parse(original)[0];
        string shifted = "pre\npre\n" + original;
        var result = MarkdownTaskNavigator.Resolve(shifted, first);
        Assert.True(result.IsFallback);
        Assert.Equal(0, result.SelectionStart);
        Assert.Equal(0, result.SelectionLength);
    }
}

[Collection(LiveProfileSnapshotCollection.Name)]
[TestCategory(TestCategories.UiSensitive)]
public sealed class MarkdownTaskNavigationUiTests
{
    [Fact]
    public void OpenTask_EditorSelectionStaysWithinBounds()
    {
        StaTestHarness.Run(() =>
        {
            string profile = Path.Combine(Path.GetTempPath(), "qn_task_nav_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(profile);
            try
            {
                using var fixture = new TestProfileComposition(profile, seedNotes: 0, ownsDirectory: false);
                using (var db = fixture.ContextFactory())
                {
                    db.Notes.Add(new Note
                    {
                        Title = "Nav",
                        Text = "# H\n\n- [ ] jump here please\n"
                    });
                    db.SaveChanges();
                }

                var (window, vm) = fixture;
                window.Show();
                var section = vm.VirtualSections.First(s => s.Section == NavigationSection.Tasks);
                vm.SelectSectionCommand.Execute(section);
                TasksIndexUiSmokeRunner.WaitForTasks(vm);
                Assert.NotEmpty(vm.Tasks);
                vm.OpenTask(vm.Tasks[0]);
                ThreePaneUiSmokeRunner.DoEvents();
                Assert.NotNull(vm.DetailEditor);
                int start = window.DetailBodyBox.SelectionStart;
                int length = window.DetailBodyBox.SelectionLength;
                Assert.InRange(start, 0, window.DetailBodyBox.Text.Length);
                Assert.InRange(length, 0, window.DetailBodyBox.Text.Length - start);
                Assert.Contains("jump here please", window.DetailBodyBox.SelectedText, StringComparison.Ordinal);
                Assert.True(vm.LastTaskNavigationWasExact);
                Assert.DoesNotContain("неоднозначна", vm.StatusText, StringComparison.Ordinal);
                window.Close();
            }
            finally
            {
                SqliteTestUtil.TryDeleteDirectory(profile);
            }
        }, TimeSpan.FromSeconds(40));
    }

    [Fact]
    public void OpenTask_DuplicateShiftedSoNearestIsWrong_FallsBackWithoutForeignSelection()
    {
        StaTestHarness.Run(() =>
        {
            string profile = Path.Combine(Path.GetTempPath(), "qn_task_nav_fb_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(profile);
            try
            {
                using var fixture = new TestProfileComposition(profile, seedNotes: 0, ownsDirectory: false);
                int noteId;
                using (var db = fixture.ContextFactory())
                {
                    var note = new Note
                    {
                        Title = "Twins",
                        Text = "- [ ] twin\n- [ ] twin\n"
                    };
                    db.Notes.Add(note);
                    db.SaveChanges();
                    noteId = note.Id;
                }

                var (window, vm) = fixture;
                window.Show();
                var section = vm.VirtualSections.First(s => s.Section == NavigationSection.Tasks);
                vm.SelectSectionCommand.Execute(section);
                TasksIndexUiSmokeRunner.WaitForTasks(vm);
                Assert.Equal(2, vm.Tasks.Count);
                var second = vm.Tasks.Single(t => t.Locator.LineNumber == 2);

                using (var db = fixture.ContextFactory())
                {
                    var note = db.Notes.First(n => n.Id == noteId);
                    note.Text = "- [ ] twin\ninserted\ninserted\ninserted\n- [ ] twin\n";
                    db.SaveChanges();
                }

                vm.OpenTask(second);
                ThreePaneUiSmokeRunner.DoEvents();
                vm.ApplyPendingTaskNavigation();
                ThreePaneUiSmokeRunner.DoEvents();
                Assert.False(vm.LastTaskNavigationWasExact);
                Assert.Equal(MainViewModel.TaskNavigationFallbackStatus, vm.StatusText);
                string proofHex = Convert.ToHexString(second.Locator.NoteSnapshotProof);
                Assert.DoesNotContain(proofHex, vm.StatusText, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(proofHex, window.DetailBodyBox.Text, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(0, window.DetailBodyBox.SelectionLength);
                Assert.Equal(0, window.DetailBodyBox.SelectionStart);
                Assert.True(string.IsNullOrEmpty(window.DetailBodyBox.SelectedText));
                window.Close();
            }
            finally
            {
                SqliteTestUtil.TryDeleteDirectory(profile);
            }
        }, TimeSpan.FromSeconds(40));
    }
}
