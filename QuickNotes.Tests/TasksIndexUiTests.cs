using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.ViewModels;
using QuickNotes.App.Views;
using Xunit;

namespace QuickNotes.Tests;

[Collection(LiveProfileSnapshotCollection.Name)]
[TestCategory(TestCategories.UiSensitive)]
public sealed class TasksIndexUiTests
{
    [Fact]
    public void KeyboardAccessibleNames_ArePresentInXaml()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ROADMAP.md")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var xaml = File.ReadAllText(Path.Combine(dir!.FullName, "QuickNotes.App", "Views", "MainWindow.xaml"));
        Assert.Contains("TasksListBox", xaml);
        Assert.Contains("AutomationProperties.Name=\"Список открытых задач\"", xaml);
        Assert.Contains("из текста заметок", xaml);
        Assert.Contains("ShowTasksEmptyState", xaml);
        Assert.Contains("LoadMoreNotesButton", xaml);
        Assert.DoesNotContain("WebView2", xaml);

        var windowOpen = xaml.IndexOf("<Window", StringComparison.Ordinal);
        var windowClose = xaml.IndexOf('>', windowOpen);
        Assert.True(windowOpen >= 0 && windowClose > windowOpen);
        string windowTag = xaml[windowOpen..windowClose];
        Assert.DoesNotContain("MinWidth=\"750\"", windowTag);
        var minWidthMatch = System.Text.RegularExpressions.Regex.Match(windowTag, @"MinWidth=""(\d+)""");
        Assert.True(minWidthMatch.Success, windowTag);
        Assert.True(int.Parse(minWidthMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) <= 640);
        Assert.True(WorkspaceLayoutHelper.MinWindowWidth <= 640);
    }

    [Fact]
    public void NarrowWindow_TaskListAndSearch_AreWithinClientBounds()
    {
        StaTestHarness.Run(() =>
        {
            string profile = Path.Combine(Path.GetTempPath(), "qn_task_ui_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(profile);
            try
            {
                using var fixture = new TestProfileComposition(profile, seedNotes: 0, ownsDirectory: false);
                using (var db = fixture.ContextFactory())
                {
                    db.Notes.Add(new QuickNotes.App.Models.Note
                    {
                        Title = "UI",
                        Text = "- [ ] compact action @2026-10-01\n"
                    });
                    db.SaveChanges();
                }

                var (window, vm) = fixture;
                Assert.True(window.MinWidth <= 640, window.MinWidth.ToString("F0"));
                Assert.True(window.MinHeight <= 560, window.MinHeight.ToString("F0"));
                vm.IsNarrow = true;
                vm.IsDetailActiveInNarrow = false;
                window.Width = 640;
                window.Height = 560;
                window.Show();
                vm.SelectSectionCommand.Execute(vm.VirtualSections.First(s => s.Section == NavigationSection.Tasks));
                TasksIndexUiSmokeRunner.WaitForTasks(vm);
                Assert.NotEmpty(vm.Tasks);
                vm.IsNarrow = true;
                vm.IsDetailActiveInNarrow = false;
                window.UpdateWorkspaceLayout();
                window.Measure(new Size(640, 560));
                window.Arrange(new Rect(0, 0, 640, 560));
                window.UpdateLayout();
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(640, 560));
                content.Arrange(new Rect(0, 0, 640, 560));
                content.UpdateLayout();

                Assert.True(window.SearchBox.IsVisible);
                Assert.True(window.TasksListBox.IsVisible);
                Assert.True(window.TasksListBox.ActualWidth > 0);
                var search = window.SearchBox.TransformToAncestor(content)
                    .TransformBounds(new Rect(0, 0, window.SearchBox.ActualWidth, window.SearchBox.ActualHeight));
                Assert.True(search.Right <= 642, search.Right.ToString("F1"));
                var list = window.TasksListBox.TransformToAncestor(content)
                    .TransformBounds(new Rect(0, 0, Math.Max(1, window.TasksListBox.ActualWidth), Math.Max(1, window.TasksListBox.ActualHeight)));
                Assert.True(list.Right <= 642, list.Right.ToString("F1"));
                Assert.True(list.Left >= -2, list.Left.ToString("F1"));
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(window.TasksListBox)));
                if (window.LoadMoreNotesButton.IsVisible && window.LoadMoreNotesButton.ActualWidth > 0)
                {
                    var more = window.LoadMoreNotesButton.TransformToAncestor(content)
                        .TransformBounds(new Rect(0, 0, window.LoadMoreNotesButton.ActualWidth, window.LoadMoreNotesButton.ActualHeight));
                    Assert.True(more.Right <= 642, more.Right.ToString("F1"));
                    Assert.True(more.Bottom <= 562, more.Bottom.ToString("F1"));
                }

                vm.OpenTask(vm.Tasks[0]);
                ThreePaneUiSmokeRunner.DoEvents();
                vm.ApplyPendingTaskNavigation();
                ThreePaneUiSmokeRunner.DoEvents();
                window.UpdateWorkspaceLayout();
                window.Measure(new Size(640, 560));
                window.Arrange(new Rect(0, 0, 640, 560));
                window.UpdateLayout();
                content.Measure(new Size(640, 560));
                content.Arrange(new Rect(0, 0, 640, 560));
                content.UpdateLayout();
                Assert.True(window.DetailBodyBox.IsVisible);
                var editor = window.DetailBodyBox.TransformToAncestor(content)
                    .TransformBounds(new Rect(0, 0, Math.Max(1, window.DetailBodyBox.ActualWidth), Math.Max(1, window.DetailBodyBox.ActualHeight)));
                Assert.True(editor.Left >= -2, editor.Left.ToString("F1"));
                Assert.True(editor.Right <= 642, editor.Right.ToString("F1"));
                window.Close();
            }
            finally
            {
                SqliteTestUtil.TryDeleteDirectory(profile);
            }
        }, TimeSpan.FromSeconds(40));
    }
}
