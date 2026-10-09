using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RadioButton = System.Windows.Controls.RadioButton;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.ViewModels;
using QuickNotes.App.Views;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.UiSensitive)]
public sealed class NoteAssemblyUiTests : IDisposable
{
    private readonly string _root;
    private readonly string _dbPath;

    public NoteAssemblyUiTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qn_asmui_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "db.sqlite");
        using var db = SqliteTestUtil.CreateContext(_dbPath);
        DbInitializer.Initialize(db);
        db.Notes.Add(new Note { Title = "Первая", Text = "тело 1" });
        db.Notes.Add(new Note { Title = "Вторая", Text = "тело 2" });
        db.Notes.Add(new Note { Title = "Третья", Text = "тело 3" });
        db.SaveChanges();
    }

    public void Dispose() => SqliteTestUtil.TryDeleteDirectory(_root);

    [Fact]
    public void PreviewDoesNotWrite_DefaultPreserves_DestructiveNeedsAck_CommitDisabledUntilValid()
    {
        using var db = SqliteTestUtil.CreateContext(_dbPath);
        int[] ids = db.Notes.OrderBy(n => n.Id).Select(n => n.Id).Take(2).ToArray();
        int notesBefore = db.Notes.Count();
        var vm = new NoteAssemblyViewModel(() => SqliteTestUtil.CreateContext(_dbPath), new NoteAssemblyService(), new NoteHistoryService(), ids);
        vm.Title = "";
        Assert.False(vm.CanCommit);
        Assert.True(vm.HasBlockingReason);
        Assert.Equal(notesBefore, CountNotes());
        Assert.True(vm.PreserveSources);
        Assert.False(vm.MoveSourcesToTrash);

        vm.Title = "Итог UI";
        Assert.True(vm.HasValidPreview);
        Assert.True(vm.CanCommit);
        Assert.Contains("# Итог UI", vm.PreviewMarkdown);

        vm.MoveSourcesToTrash = true;
        Assert.False(vm.CanCommit);
        vm.TrashAcknowledged = true;
        Assert.True(vm.CanCommit);

        vm.MoveSourcesToTrash = false;
        Assert.False(vm.TrashAcknowledged);
        Assert.True(vm.CanCommit);
        Assert.Equal(notesBefore, CountNotes());
    }

    [Fact]
    public void ReorderTitleAndSeparator_AreVisibleInPreview()
    {
        using var db = SqliteTestUtil.CreateContext(_dbPath);
        int[] ids = db.Notes.OrderBy(n => n.Id).Select(n => n.Id).ToArray();
        var vm = new NoteAssemblyViewModel(() => SqliteTestUtil.CreateContext(_dbPath), new NoteAssemblyService(), new NoteHistoryService(), ids.Take(2));
        vm.Title = "Порядок";
        vm.SelectedSource = vm.Sources[1];
        Assert.True(vm.MoveSourceUpCommand.CanExecute(null));
        vm.MoveSourceUpCommand.Execute(null);
        Assert.Equal(ids[1], vm.Sources[0].NoteId);
        Assert.Contains("тело 2", vm.PreviewMarkdown);
        int firstBody = vm.PreviewMarkdown.IndexOf("тело 2", StringComparison.Ordinal);
        int secondBody = vm.PreviewMarkdown.IndexOf("тело 1", StringComparison.Ordinal);
        Assert.True(firstBody < secondBody);

        vm.SelectedSeparator = vm.SeparatorOptions.First(o => o.Kind == NoteAssemblySeparatorKind.ThematicBreakStar);
        Assert.Contains("***", vm.PreviewMarkdown);
        vm.SelectedSeparator = vm.SeparatorOptions.First(o => o.Kind == NoteAssemblySeparatorKind.Custom);
        vm.CustomSeparator = "====";
        Assert.Contains("====", vm.PreviewMarkdown);
        Assert.True(vm.IsCustomSeparator);
    }

    [Fact]
    public void KeyboardAccessibleNames_ArePresentInXaml()
    {
        var root = FindSolutionRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "Views", "NoteAssemblyWindow.xaml"));
        Assert.Contains("AssemblySourcesList", xaml);
        Assert.Contains("AssemblyMoveUpButton", xaml);
        Assert.Contains("AssemblyMoveDownButton", xaml);
        Assert.Contains("AssemblyRemoveSourceButton", xaml);
        Assert.Contains("AssemblySourceRow", xaml);
        Assert.Contains("AssemblyCandidateRow", xaml);
        Assert.Contains("AssemblyTitleBox", xaml);
        Assert.Contains("AssemblySeparatorCombo", xaml);
        Assert.Contains("AssemblyPreviewBox", xaml);
        Assert.Contains("AssemblyCandidateSearchBox", xaml);
        Assert.Contains("AssemblyLoadMoreCandidatesButton", xaml);
        Assert.Contains("AssemblyConfigSummaryText", xaml);
        Assert.Contains("AssemblyPreserveSourcesRadio", xaml);
        Assert.Contains("AssemblyTrashSourcesRadio", xaml);
        Assert.Contains("AssemblyTrashAckCheckBox", xaml);
        Assert.Contains("AssemblyCommitButton", xaml);
        Assert.Contains("AssemblySuccessSummaryPanel", xaml);
        Assert.Contains("AutomationProperties.Name=\"Список исходников сборки\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"Выполнить сборку заметок\"", xaml);
        Assert.Contains("Ctrl+↑", xaml);
        Assert.Contains("Binding CanCommit", xaml);
    }

    [Fact]
    public void NarrowWindow_CommitAndAck_AreReachableWithinClientBounds()
    {
        using var db = SqliteTestUtil.CreateContext(_dbPath);
        int[] ids = db.Notes.OrderBy(n => n.Id).Select(n => n.Id).Take(2).ToArray();
        StaTestHarness.Run(() =>
        {
            var vm = new NoteAssemblyViewModel(() => SqliteTestUtil.CreateContext(_dbPath), new NoteAssemblyService(), new NoteHistoryService(), ids);
            vm.Title = "Узкий";
            var window = new NoteAssemblyWindow(vm)
            {
                Width = 640,
                Height = 560,
                MinWidth = 640,
                MinHeight = 520
            };
            window.Show();
            window.UpdateLayout();
            Assert.True(vm.CanCommit);

            AssertSourceActionsAndCommitReachable(window, vm, "Узкий");

            vm.MoveSourcesToTrash = true;
            vm.TrashAcknowledged = false;
            window.UpdateLayout();
            var ack = (CheckBox)window.FindName("AssemblyTrashAckCheckBox")!;
            var trash = (RadioButton)window.FindName("AssemblyTrashSourcesRadio")!;
            var commit = (Button)window.FindName("AssemblyCommitButton")!;
            ack.BringIntoView();
            window.UpdateLayout();
            Assert.True(ack.IsVisible);
            Assert.True(trash.IsChecked);
            Assert.NotEqual(true, ack.IsChecked);
            Assert.False(vm.CanCommit);
            Assert.False(commit.IsEnabled);
            NoteAssemblyUiSmokeRunner.AssertVisibleInClient(window, ack, "AssemblyTrashAckCheckBox");
            NoteAssemblyUiSmokeRunner.AssertUnclipped(window, ack, "AssemblyTrashAckCheckBox", 16);
            NoteAssemblyUiSmokeRunner.AssertVisibleInClient(window, commit, "AssemblyCommitButton");
            NoteAssemblyUiSmokeRunner.AssertSourceActionsUnclipped(window);
            window.Close();
        }, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void SourceActions_AreUnclippedAtAcceptanceSizes_AndDoNotOverlapCandidates()
    {
        using var db = SqliteTestUtil.CreateContext(_dbPath);
        int[] ids = db.Notes.OrderBy(n => n.Id).Select(n => n.Id).ToArray();
        StaTestHarness.Run(() =>
        {
            foreach (var (width, height) in new[] { (780, 720), (640, 560) })
            {
                var vm = new NoteAssemblyViewModel(() => SqliteTestUtil.CreateContext(_dbPath), new NoteAssemblyService(), new NoteHistoryService(), ids);
                vm.Title = "Полный предпросмотр сборки";
                var window = new NoteAssemblyWindow(vm)
                {
                    Width = width,
                    Height = height,
                    MinWidth = 640,
                    MinHeight = 520
                };
                window.Show();
                window.UpdateLayout();
                AssertSourceActionsAndCommitReachable(window, vm, "Полный предпросмотр сборки");

                vm.MoveSourcesToTrash = true;
                vm.TrashAcknowledged = false;
                window.UpdateLayout();
                NoteAssemblyUiSmokeRunner.AssertSourceActionsUnclipped(window);
                var ack = (CheckBox)window.FindName("AssemblyTrashAckCheckBox")!;
                var preview = (TextBox)window.FindName("AssemblyPreviewBox")!;
                var commit = (Button)window.FindName("AssemblyCommitButton")!;
                NoteAssemblyUiSmokeRunner.AssertVisibleInClient(window, ack, "AssemblyTrashAckCheckBox");
                NoteAssemblyUiSmokeRunner.AssertVisibleInClient(window, preview, "AssemblyPreviewBox");
                NoteAssemblyUiSmokeRunner.AssertVisibleInClient(window, commit, "AssemblyCommitButton");
                Assert.True(preview.ActualHeight >= 96);
                window.Close();
            }
        }, TimeSpan.FromSeconds(30));
    }

    private static void AssertSourceActionsAndCommitReachable(NoteAssemblyWindow window, NoteAssemblyViewModel vm, string titleFragment)
    {
        var commit = (Button)window.FindName("AssemblyCommitButton")!;
        var title = (TextBox)window.FindName("AssemblyTitleBox")!;
        var preview = (TextBox)window.FindName("AssemblyPreviewBox")!;
        var config = (TextBlock)window.FindName("AssemblyConfigSummaryText")!;
        Assert.True(commit.ActualWidth > 0);
        Assert.True(title.ActualWidth > 0);
        Assert.True(preview.ActualHeight >= 96);
        Assert.Contains(titleFragment, config.Text, StringComparison.Ordinal);
        NoteAssemblyUiSmokeRunner.AssertVisibleInClient(window, preview, "AssemblyPreviewBox");
        NoteAssemblyUiSmokeRunner.AssertVisibleInClient(window, commit, "AssemblyCommitButton");
        NoteAssemblyUiSmokeRunner.AssertSourceActionsUnclipped(window);
        Assert.True(vm.CanCommit || vm.MoveSourcesToTrash);
    }

    [Fact]
    public void Candidates_SearchAndPaging_FindsOlderThanPage_HidesProtectedPlaintext()
    {
        DateTime now = DateTime.Now;
        int oldId;
        int protectedId;
        const string secretTitle = "SECRET_PROTECTED_TITLE_8_2";
        const string secretBody = "SECRET_PROTECTED_BODY_8_2";
        const string oldMarker = "AncientUniqueAssemblyToken";
        using (var db = SqliteTestUtil.CreateContext(_dbPath))
        {
            for (int i = 0; i < 210; i++)
            {
                db.Notes.Add(new Note
                {
                    Title = "Свежая " + i.ToString("000"),
                    Text = "тело свежей " + i,
                    CreatedAt = now.AddMinutes(i),
                    UpdatedAt = now.AddMinutes(i)
                });
            }

            var old = new Note
            {
                Title = "Древняя " + oldMarker,
                Text = "старое тело " + oldMarker,
                CreatedAt = now.AddYears(-2),
                UpdatedAt = now.AddYears(-2)
            };
            var locked = new Note
            {
                Title = secretTitle,
                Text = secretBody,
                IsProtected = true,
                CreatedAt = now.AddMinutes(500),
                UpdatedAt = now.AddMinutes(500)
            };
            db.Notes.Add(old);
            db.Notes.Add(locked);
            db.SaveChanges();
            oldId = old.Id;
            protectedId = locked.Id;
        }

        var vm = new NoteAssemblyViewModel(() => SqliteTestUtil.CreateContext(_dbPath), new NoteAssemblyService(), new NoteHistoryService());
        Assert.Equal(NoteAssemblyViewModel.CandidatePageSize, vm.Candidates.Count);
        Assert.True(vm.CandidateHasMore);
        Assert.True(vm.CandidateTotalCount > 200);
        Assert.DoesNotContain(vm.Candidates, c => c.NoteId == oldId);
        Assert.Contains("из", vm.CandidateStatusText, StringComparison.Ordinal);
        Assert.DoesNotContain(secretTitle, string.Join("|", vm.Candidates.Select(c => c.DisplayTitle)));
        Assert.DoesNotContain(secretBody, string.Join("|", vm.Candidates.Select(c => c.DisplayTitle)));

        int pages = 0;
        while (vm.CandidateHasMore && vm.Candidates.All(c => c.NoteId != oldId) && pages < 10)
        {
            vm.LoadMoreCandidates();
            pages++;
        }

        Assert.Contains(vm.Candidates, c => c.NoteId == oldId);
        Assert.True(pages >= 3);

        vm.CandidateSearchText = oldMarker;
        Assert.False(vm.CandidateHasMore);
        Assert.Single(vm.Candidates);
        Assert.Equal(oldId, vm.Candidates[0].NoteId);
        Assert.Contains(oldMarker, vm.Candidates[0].DisplayTitle, StringComparison.Ordinal);
        Assert.True(vm.TryAddSource(oldId));
        Assert.Contains(vm.Sources, s => s.NoteId == oldId);

        vm.CandidateSearchText = "zzz-no-such-assembly-note";
        Assert.Empty(vm.Candidates);
        Assert.Contains("Ничего не найдено", vm.CandidateStatusText, StringComparison.Ordinal);

        vm.CandidateSearchText = secretTitle;
        Assert.DoesNotContain(vm.Candidates, c => c.NoteId == protectedId);
        Assert.DoesNotContain(secretTitle, string.Join("|", vm.Candidates.Select(c => c.DisplayTitle)));
        Assert.DoesNotContain(secretBody, string.Join("|", vm.Candidates.Select(c => c.DisplayTitle)));

        vm.CandidateSearchText = "#" + protectedId;
        Assert.Single(vm.Candidates);
        Assert.Equal(protectedId, vm.Candidates[0].NoteId);
        Assert.Equal($"#{protectedId} · {NoteAssemblyViewModel.ProtectedCandidateLabel}", vm.Candidates[0].DisplayTitle);
        Assert.DoesNotContain(secretTitle, vm.Candidates[0].DisplayTitle);
        Assert.True(vm.TryAddSource(protectedId));
        Assert.Contains(vm.Sources, s => s.NoteId == protectedId && s.DisplayTitle == NoteAssemblyViewModel.ProtectedCandidateLabel);
        Assert.False(vm.CanCommit);
        Assert.DoesNotContain(secretTitle, vm.PreviewMarkdown);
        Assert.DoesNotContain(secretBody, vm.PreviewMarkdown);
        Assert.DoesNotContain(secretTitle, vm.PreviewConfigurationText);
    }

    private int CountNotes()
    {
        using var db = SqliteTestUtil.CreateContext(_dbPath);
        return db.Notes.Count();
    }

    private static string FindSolutionRoot()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            if (File.Exists(Path.Combine(dir, "QuickNotes.sln")))
            {
                return dir;
            }

            dir = Path.GetFullPath(Path.Combine(dir, ".."));
        }

        throw new DirectoryNotFoundException("QuickNotes.sln");
    }
}
