using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.Services.NoteProtection;
using QuickNotes.App.Services.Sync;
using QuickNotes.App.ViewModels;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public sealed class NoteAssemblyServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _dbPath;

    public NoteAssemblyServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "qn_asm_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "db.sqlite");
        using var db = SqliteTestUtil.CreateContext(_dbPath);
        DbInitializer.Initialize(db);
    }

    public void Dispose()
    {
        NoteAssemblyService.TestInjectFailure = null;
        SqliteTestUtil.TryDeleteDirectory(_root);
    }

    [Fact]
    public void ComposeMarkdown_UsesTitleOrderSeparatorsAndNormalizedBodies()
    {
        string md = NoteAssemblyService.ComposeMarkdown(
            "Итог",
            "---",
            new[] { "первая\r\nстрока  ", "вторая" });
        Assert.Equal("# Итог\n\nпервая\r\nстрока  \n\n---\n\nвторая\n", md);

        string blank = NoteAssemblyService.ComposeMarkdown("T", string.Empty, new[] { "a", "b" });
        Assert.Equal("# T\n\na\n\nb\n", blank);

        string stars = NoteAssemblyService.ComposeMarkdown("T", "***", new[] { "a", "b" });
        Assert.Contains("\n\n***\n\n", stars);

        string custom = NoteAssemblyService.ComposeMarkdown("T", "* * *", new[] { "α", "β" });
        Assert.Contains("\n\n* * *\n\n", custom);

        Assert.Equal("a\nb", NoteAssemblyService.NormalizeNoteBody("a\r\nb\n\n"));
    }

    [Fact]
    public void Preview_EmptySet_Duplicates_AndDeleted_AreBlocked()
    {
        var service = new NoteAssemblyService();
        using (var db = SqliteTestUtil.CreateContext(_dbPath))
        {
            var a = AddNote(db, "A", "один");
            var b = AddNote(db, "B", "два");
            var deleted = AddNote(db, "D", "удалена");
            deleted.DeletedAt = DateTime.Now;
            db.SaveChanges();

            Assert.False(service.BuildPreview(db, Req(Array.Empty<int>(), "T")).IsValid);
            Assert.False(service.BuildPreview(db, Req(new[] { a.Id }, "T")).IsValid);
            var dup = service.BuildPreview(db, Req(new[] { a.Id, a.Id }, "T"));
            Assert.False(dup.IsValid);
            Assert.Contains("дважды", dup.BlockingReason, StringComparison.Ordinal);

            var gone = service.BuildPreview(db, Req(new[] { a.Id, deleted.Id }, "T"));
            Assert.False(gone.IsValid);
            Assert.Contains("удалён", gone.BlockingReason, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Preview_DoesNotWrite_AndKeepsUnicodeMarkdownEmptyBodiesAndTagUnion()
    {
        var service = new NoteAssemblyService();
        using var db = SqliteTestUtil.CreateContext(_dbPath);
        var tagWork = new Tag { Name = "работа" };
        var tagIdea = new Tag { Name = "идея" };
        db.Tags.AddRange(tagWork, tagIdea);
        db.SaveChanges();

        var first = AddNote(db, "Первая", "Текст **один** и ёлка");
        first.NoteTags.Add(new NoteTag { TagId = tagWork.Id, Origin = TagOrigin.Auto });
        var second = AddNote(db, "Вторая", "");
        second.NoteTags.Add(new NoteTag { TagId = tagWork.Id, Origin = TagOrigin.Manual });
        second.NoteTags.Add(new NoteTag { TagId = tagIdea.Id, Origin = TagOrigin.Auto });
        db.SaveChanges();
        int notes = db.Notes.Count();

        var preview = service.BuildPreview(db, new NoteAssemblyPreviewRequest
        {
            SourceNoteIds = new[] { second.Id, first.Id },
            Title = "  Сводка \nстрока ",
            SeparatorKind = NoteAssemblySeparatorKind.ThematicBreakDash
        });

        Assert.True(preview.IsValid);
        Assert.Equal(notes, db.Notes.Count());
        Assert.Equal("Сводка строка", preview.Title);
        Assert.StartsWith("# Сводка строка\n", preview.AssembledMarkdown);
        Assert.Contains("Текст **один** и ёлка", preview.AssembledMarkdown);
        Assert.Contains("---", preview.AssembledMarkdown);
        Assert.Equal(2, preview.ResultTags.Count);
        Assert.Contains(preview.ResultTags, t => t.TagName == "работа" && t.Origin == TagOrigin.Manual);
        Assert.Contains(preview.ResultTags, t => t.TagName == "идея" && t.Origin == TagOrigin.Auto);
        Assert.Equal(second.Id, preview.Sources[0].NoteId);
        Assert.Equal(first.Id, preview.Sources[1].NoteId);
    }

    [Fact]
    public void Preview_SameTitleAsExisting_IsAllowed()
    {
        var service = new NoteAssemblyService();
        using var db = SqliteTestUtil.CreateContext(_dbPath);
        var a = AddNote(db, "Одинаковый", "x");
        var b = AddNote(db, "Одинаковый", "y");
        var preview = service.BuildPreview(db, Req(new[] { a.Id, b.Id }, "Одинаковый"));
        Assert.True(preview.IsValid);
    }

    [Fact]
    public void Execute_CreatesNewNote_PreservesSources_WritesHistoryTagsAndFts()
    {
        var service = new NoteAssemblyService();
        using var db = SqliteTestUtil.CreateContext(_dbPath);
        var tag = new Tag { Name = "inbox" };
        db.Tags.Add(tag);
        db.SaveChanges();
        var a = AddNote(db, "A", "alpha");
        a.NoteTags.Add(new NoteTag { TagId = tag.Id, Origin = TagOrigin.Manual });
        var b = AddNote(db, "B", "beta");
        db.SaveChanges();
        DateTime aUpdated = a.UpdatedAt;
        DateTime bUpdated = b.UpdatedAt;
        string aText = a.Text;
        string bText = b.Text;

        var preview = service.BuildPreview(db, Req(new[] { a.Id, b.Id }, "Сборка один"));
        Assert.True(preview.IsValid);
        var result = service.Execute(db, new NoteAssemblyExecuteRequest { Preview = preview, MoveSourcesToTrash = false }, Coord());
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(0, result.TrashedSourceCount);

        var created = db.Notes.Include(n => n.NoteTags).First(n => n.Id == result.CreatedNoteId);
        Assert.Equal("Сборка один", created.Title);
        Assert.Equal(preview.AssembledMarkdown, created.Text);
        Assert.False(created.IsInbox);
        Assert.Null(created.DeletedAt);
        Assert.Contains(created.NoteTags, t => t.TagId == tag.Id && t.Origin == TagOrigin.Manual);
        Assert.Equal(1, db.NoteRevisions.Count(r => r.NoteId == created.Id));

        db.Entry(a).Reload();
        db.Entry(b).Reload();
        Assert.Equal(aText, a.Text);
        Assert.Equal(bText, b.Text);
        Assert.Null(a.DeletedAt);
        Assert.Null(b.DeletedAt);
        Assert.Equal(aUpdated, a.UpdatedAt);
        Assert.Equal(bUpdated, b.UpdatedAt);

        var fts = db.Database.SqlQueryRaw<int>("SELECT NoteId AS Value FROM NotesFts WHERE NoteId = {0}", created.Id).ToList();
        Assert.Contains(created.Id, fts);

        var preview2 = service.BuildPreview(db, Req(new[] { a.Id, b.Id }, "Сборка один"));
        var second = service.Execute(db, new NoteAssemblyExecuteRequest { Preview = preview2 }, Coord());
        Assert.True(second.Success);
        Assert.NotEqual(result.CreatedNoteId, second.CreatedNoteId);
        Assert.Equal(2, db.Notes.Count(n => n.Title == "Сборка один" && n.DeletedAt == null));
    }

    [Fact]
    public void Execute_SourceChangedOrDeletedAfterPreview_FailsWithoutWrite()
    {
        var service = new NoteAssemblyService();
        using var db = SqliteTestUtil.CreateContext(_dbPath);
        var a = AddNote(db, "A", "alpha");
        var b = AddNote(db, "B", "beta");
        var preview = service.BuildPreview(db, Req(new[] { a.Id, b.Id }, "Сборка"));
        a.Text = "changed";
        a.UpdatedAt = DateTime.Now.AddMinutes(1);
        db.SaveChanges();
        int count = db.Notes.Count();
        var failed = service.Execute(db, new NoteAssemblyExecuteRequest { Preview = preview }, Coord());
        Assert.False(failed.Success);
        Assert.Contains("изменились", failed.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(count, db.Notes.Count());

        var preview2 = service.BuildPreview(db, Req(new[] { a.Id, b.Id }, "Сборка"));
        b.DeletedAt = DateTime.Now;
        db.SaveChanges();
        var failedDelete = service.Execute(db, new NoteAssemblyExecuteRequest { Preview = preview2 }, Coord());
        Assert.False(failedDelete.Success);
        Assert.Equal(count, db.Notes.Count());
    }

    [Fact]
    public void Execute_Cancellation_DoesNotWrite()
    {
        var service = new NoteAssemblyService();
        using var db = SqliteTestUtil.CreateContext(_dbPath);
        var a = AddNote(db, "A", "alpha");
        var b = AddNote(db, "B", "beta");
        var preview = service.BuildPreview(db, Req(new[] { a.Id, b.Id }, "Сборка"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        int count = db.Notes.Count();
        var result = service.Execute(db, new NoteAssemblyExecuteRequest { Preview = preview }, Coord(), cancellationToken: cts.Token);
        Assert.False(result.Success);
        Assert.Contains("отмен", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(count, db.Notes.Count());
    }

    [Theory]
    [InlineData(NoteAssemblyTransactionPhase.Validation)]
    [InlineData(NoteAssemblyTransactionPhase.InsertNote)]
    [InlineData(NoteAssemblyTransactionPhase.Tags)]
    [InlineData(NoteAssemblyTransactionPhase.History)]
    [InlineData(NoteAssemblyTransactionPhase.TrashSources)]
    [InlineData(NoteAssemblyTransactionPhase.Commit)]
    public void Execute_FailureOnEachPhase_RollsBackNoteTrashTagsHistoryAndFts(NoteAssemblyTransactionPhase phase)
    {
        var service = new NoteAssemblyService();
        using var db = SqliteTestUtil.CreateContext(_dbPath);
        var tag = new Tag { Name = "t" };
        db.Tags.Add(tag);
        db.SaveChanges();
        var a = AddNote(db, "A", "alpha-phase");
        a.NoteTags.Add(new NoteTag { TagId = tag.Id, Origin = TagOrigin.Manual });
        var b = AddNote(db, "B", "beta-phase");
        db.SaveChanges();
        int aId = a.Id;
        int bId = b.Id;
        int notes = db.Notes.Count();
        int revisions = db.NoteRevisions.Count();
        int noteTags = db.NoteTags.Count();

        var preview = service.BuildPreview(db, Req(new[] { aId, bId }, "Фаза"));
        NoteAssemblyService.TestInjectFailure = p =>
        {
            if (p == phase)
            {
                throw new InvalidOperationException("injected-assembly-failure");
            }
        };

        try
        {
            var result = service.Execute(db, new NoteAssemblyExecuteRequest
            {
                Preview = preview,
                MoveSourcesToTrash = true,
                TrashAcknowledged = true
            }, Coord());
            Assert.False(result.Success);
        }
        finally
        {
            NoteAssemblyService.TestInjectFailure = null;
        }

        using var verify = SqliteTestUtil.CreateContext(_dbPath);
        Assert.Equal(notes, verify.Notes.Count());
        Assert.Equal(revisions, verify.NoteRevisions.Count());
        Assert.Equal(noteTags, verify.NoteTags.Count());
        Assert.Null(verify.Notes.Find(aId)!.DeletedAt);
        Assert.Null(verify.Notes.Find(bId)!.DeletedAt);
        Assert.Equal("alpha-phase", verify.Notes.Find(aId)!.Text);
        var ftsDebris = verify.Database.SqlQueryRaw<int>(
            "SELECT NoteId AS Value FROM NotesFts WHERE NotesFts MATCH {0}", "Фаза").ToList();
        Assert.Empty(ftsDebris);
    }

    [Fact]
    public void Execute_TrashAllSources_Atomically_WhenAcknowledged()
    {
        var service = new NoteAssemblyService();
        using var db = SqliteTestUtil.CreateContext(_dbPath);
        var a = AddNote(db, "A", "alpha");
        var b = AddNote(db, "B", "beta");
        var preview = service.BuildPreview(db, Req(new[] { a.Id, b.Id }, "Корзина"));
        var denied = service.Execute(db, new NoteAssemblyExecuteRequest
        {
            Preview = preview,
            MoveSourcesToTrash = true,
            TrashAcknowledged = false
        }, Coord());
        Assert.False(denied.Success);
        Assert.Null(db.Notes.Find(a.Id)!.DeletedAt);

        var ok = service.Execute(db, new NoteAssemblyExecuteRequest
        {
            Preview = preview,
            MoveSourcesToTrash = true,
            TrashAcknowledged = true
        }, Coord());
        Assert.True(ok.Success, ok.ErrorMessage);
        Assert.Equal(2, ok.TrashedSourceCount);
        Assert.NotNull(db.Notes.Find(a.Id)!.DeletedAt);
        Assert.NotNull(db.Notes.Find(b.Id)!.DeletedAt);
        Assert.Null(db.Notes.Find(ok.CreatedNoteId)!.DeletedAt);
    }

    [Fact]
    public void ProtectedLocked_BlocksPreviewAndExecute_WithoutPlaintextLeak()
    {
        string secret = "секрет-сборки-" + Guid.NewGuid().ToString("N");
        string logDir = Path.Combine(_root, "logs");
        Directory.CreateDirectory(logDir);
        string? previous = ErrorLogService.ScopedLogDirectory;
        ErrorLogService.ScopedLogDirectory = logDir;
        try
        {
            using var db = SqliteTestUtil.CreateContext(_dbPath);
            var open = AddNote(db, "Open", "открытый текст");
            var locked = AddNote(db, "SecretTitle", secret);
            db.SaveChanges();
            var protection = new NoteProtectionService();
            Assert.True(protection.ProtectNote(db, locked.Id, "пароль-сборки").Success);

            var service = new NoteAssemblyService();
            var preview = service.BuildPreview(db, Req(new[] { open.Id, locked.Id }, "Нельзя"));
            Assert.False(preview.IsValid);
            Assert.DoesNotContain(secret, preview.AssembledMarkdown);
            Assert.DoesNotContain(secret, preview.BlockingReason);
            Assert.DoesNotContain("SecretTitle", preview.AssembledMarkdown);
            Assert.Contains(preview.Sources, s => s.IsProtected && s.DisplayTitle == "Защищённая заметка");

            var execute = service.Execute(db, new NoteAssemblyExecuteRequest { Preview = preview }, Coord());
            Assert.False(execute.Success);
            Assert.Equal(2, db.Notes.Count());

            foreach (var file in Directory.GetFiles(logDir, "*", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file, Encoding.UTF8);
                Assert.DoesNotContain(secret, text);
                Assert.DoesNotContain("пароль-сборки", text);
            }
        }
        finally
        {
            ErrorLogService.ScopedLogDirectory = previous;
        }
    }

    [Fact]
    public void UnlockedProtected_StillBlocks_AndAllowedAssembleDoesNotLogPlaintext()
    {
        string secret = "unlocked-secret-" + Guid.NewGuid().ToString("N");
        string logDir = Path.Combine(_root, "logs2");
        Directory.CreateDirectory(logDir);
        string? previous = ErrorLogService.ScopedLogDirectory;
        ErrorLogService.ScopedLogDirectory = logDir;
        try
        {
            using var db = SqliteTestUtil.CreateContext(_dbPath);
            var openA = AddNote(db, "A", "тело A");
            var openB = AddNote(db, "B", "тело B");
            var locked = AddNote(db, "Hidden", secret);
            db.SaveChanges();
            var protection = new NoteProtectionService();
            Assert.True(protection.ProtectNote(db, locked.Id, "пароль").Success);
            protection.UnlockNote(db, locked.Id, "пароль");
            Assert.True(protection.IsUnlocked(locked.Id));

            var service = new NoteAssemblyService();
            var blocked = service.BuildPreview(db, Req(new[] { openA.Id, locked.Id }, "Нет"));
            Assert.False(blocked.IsValid);
            Assert.DoesNotContain(secret, blocked.AssembledMarkdown);

            var preview = service.BuildPreview(db, Req(new[] { openA.Id, openB.Id }, "Можно"));
            Assert.True(preview.IsValid);
            var result = service.Execute(db, new NoteAssemblyExecuteRequest { Preview = preview }, Coord());
            Assert.True(result.Success, result.ErrorMessage);
            Assert.DoesNotContain(secret, preview.AssembledMarkdown);

            foreach (var file in Directory.GetFiles(logDir, "*", SearchOption.AllDirectories))
            {
                Assert.DoesNotContain(secret, File.ReadAllText(file, Encoding.UTF8));
            }
        }
        finally
        {
            ErrorLogService.ScopedLogDirectory = previous;
        }
    }

    [Theory]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("suppress")]
    [InlineData("origin")]
    [InlineData("origin-manual-to-auto")]
    [InlineData("rename")]
    [InlineData("delete-tag")]
    public void Execute_ResultTagChangeAfterPreview_RejectsWithoutWrite(string mutation)
    {
        var service = new NoteAssemblyService();
        using var db = SqliteTestUtil.CreateContext(_dbPath);
        var work = new Tag { Name = "работа" };
        var extra = new Tag { Name = "идея" };
        db.Tags.AddRange(work, extra);
        db.SaveChanges();
        var a = AddNote(db, "A", "alpha");
        a.NoteTags.Add(new NoteTag
        {
            TagId = work.Id,
            Origin = mutation == "origin-manual-to-auto" ? TagOrigin.Manual : TagOrigin.Auto,
            IsSuppressed = false
        });
        var b = AddNote(db, "B", "beta");
        db.SaveChanges();
        int workId = work.Id;
        int extraId = extra.Id;
        int aId = a.Id;
        var preview = service.BuildPreview(db, Req(new[] { a.Id, b.Id }, "Теги"));
        Assert.True(preview.IsValid);
        Assert.Contains(preview.ResultTags, t => t.TagId == workId);
        int notes = db.Notes.Count();
        string staleHash = preview.PlanHash;

        switch (mutation)
        {
            case "add":
                db.NoteTags.Add(new NoteTag { NoteId = aId, TagId = extraId, Origin = TagOrigin.Manual, IsSuppressed = false });
                break;
            case "remove":
                db.NoteTags.Remove(db.NoteTags.First(nt => nt.NoteId == aId && nt.TagId == workId));
                break;
            case "suppress":
                db.NoteTags.First(nt => nt.NoteId == aId && nt.TagId == workId).IsSuppressed = true;
                break;
            case "origin":
                db.NoteTags.First(nt => nt.NoteId == aId && nt.TagId == workId).Origin = TagOrigin.Manual;
                break;
            case "origin-manual-to-auto":
                db.NoteTags.First(nt => nt.NoteId == aId && nt.TagId == workId).Origin = TagOrigin.Auto;
                break;
            case "rename":
                db.Tags.First(t => t.Id == workId).Name = "работа-переименована";
                break;
            case "delete-tag":
                db.Tags.Remove(db.Tags.First(t => t.Id == workId));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }

        db.SaveChanges();
        int notesAfterMutation = db.Notes.Count();
        int tagsAfterMutation = db.NoteTags.Count();
        var failed = service.Execute(db, new NoteAssemblyExecuteRequest { Preview = preview }, Coord());
        Assert.False(failed.Success);
        Assert.Contains("Теги результата изменились", failed.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(notesAfterMutation, db.Notes.Count());
        Assert.Equal(notes, notesAfterMutation);
        Assert.Equal(tagsAfterMutation, db.NoteTags.Count());
        Assert.DoesNotContain(db.Notes, n => n.Title == "Теги");
        var reloaded = service.BuildPreview(db, Req(new[] { a.Id, b.Id }, "Теги"));
        Assert.NotEqual(staleHash, reloaded.PlanHash);
    }

    [Fact]
    public void Execute_HeldLocalMutation_BlocksAssemblyThenRejectsChangedSourceWithoutPartialState()
    {
        var coordinator = new LocalMutationCoordinator();
        var service = new NoteAssemblyService();
        int aId;
        int bId;
        int notesBefore;
        using (var db = SqliteTestUtil.CreateContext(_dbPath))
        {
            var a = AddNote(db, "A", "alpha");
            var b = AddNote(db, "B", "beta");
            aId = a.Id;
            bId = b.Id;
            notesBefore = db.Notes.Count();
        }

        NoteAssemblyPreviewResult preview;
        using (var db = SqliteTestUtil.CreateContext(_dbPath))
        {
            preview = service.BuildPreview(db, Req(new[] { aId, bId }, "Гонка"));
            Assert.True(preview.IsValid);
        }

        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        Exception? mutationError = null;
        var mutationThread = new Thread(() =>
        {
            try
            {
                coordinator.ExecuteNoteMutation(aId, () =>
                {
                    entered.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(20)))
                    {
                        throw new TimeoutException("mutation release timed out");
                    }

                    using var db = SqliteTestUtil.CreateContext(_dbPath);
                    var note = db.Notes.First(n => n.Id == aId);
                    note.Text = "changed-before-boundary";
                    note.UpdatedAt = DateTime.Now.AddMinutes(1);
                    db.SaveChanges();
                });
            }
            catch (Exception ex)
            {
                mutationError = ex;
            }
        })
        {
            IsBackground = true
        };
        mutationThread.Start();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        NoteAssemblyExecuteResult? result = null;
        Exception? executeError = null;
        var assemblyThread = new Thread(() =>
        {
            try
            {
                using var db = SqliteTestUtil.CreateContext(_dbPath);
                result = service.Execute(db, new NoteAssemblyExecuteRequest { Preview = preview }, coordinator);
            }
            catch (Exception ex)
            {
                executeError = ex;
            }
        })
        {
            IsBackground = true
        };
        assemblyThread.Start();

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(5) && !coordinator.IsBulkRunning)
        {
            Thread.Sleep(10);
        }

        Assert.True(coordinator.IsBulkRunning);
        Assert.True(coordinator.ActiveNoteMutationsCount >= 1);
        Assert.False(assemblyThread.Join(50));
        using (var db = SqliteTestUtil.CreateContext(_dbPath))
        {
            Assert.Equal(notesBefore, db.Notes.Count());
            Assert.Equal("alpha", db.Notes.Find(aId)!.Text);
        }

        release.Set();
        Assert.True(mutationThread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(mutationError);
        Assert.True(assemblyThread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(executeError);
        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Contains("изменились", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        using var verify = SqliteTestUtil.CreateContext(_dbPath);
        Assert.Equal(notesBefore, verify.Notes.Count());
        Assert.Equal("changed-before-boundary", verify.Notes.Find(aId)!.Text);
        Assert.DoesNotContain(verify.Notes, n => n.Title == "Гонка");
        Assert.Equal(0, verify.NoteRevisions.Count());
    }

    [Fact]
    public void OpenNoteAssembly_EnqueuesSyncAndReloadOnlyAfterSuccessfulCommit()
    {
        var coordinator = new LocalMutationCoordinator();
        var scheduler = new FakeTrackingSyncScheduler(() => SqliteTestUtil.CreateContext(_dbPath));
        var settingsPath = Path.Combine(_root, "settings.json");
        var settingsService = new SettingsService(settingsPath, _ => { });
        var tagRuleService = new TagRuleService();
        using var mainVm = MainViewModelTestComposition.Create(
            () => SqliteTestUtil.CreateContext(_dbPath),
            new TagDetectionService(tagRuleService),
            new SearchService(),
            settingsService,
            new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero),
            new ClipboardCaptureService(),
            new TrayIconService(),
            new BackupService(),
            syncScheduler: scheduler,
            mutationCoordinator: coordinator,
            draftJournalService: QuickNotes.App.Services.DraftJournal.NoOpDraftJournalService.Instance);
        mainVm.NotificationHandler = (_, _) => { };
        mainVm.AlertHandler = (_, _, _) => { };

        int aId;
        int bId;
        using (var db = SqliteTestUtil.CreateContext(_dbPath))
        {
            aId = AddNote(db, "A", "alpha").Id;
            bId = AddNote(db, "B", "beta").Id;
        }

        mainVm.RefreshNotes();
        int phase = 0;
        mainVm.RequestOpenNoteAssembly += vm =>
        {
            if (vm.Sources.All(s => s.NoteId != aId))
            {
                Assert.True(vm.TryAddSource(aId));
            }

            if (vm.Sources.All(s => s.NoteId != bId))
            {
                Assert.True(vm.TryAddSource(bId));
            }
            vm.Title = "Синх";
            if (phase == 0)
            {
                using (var db = SqliteTestUtil.CreateContext(_dbPath))
                {
                    var note = db.Notes.First(n => n.Id == aId);
                    note.Text = "stale-before-commit";
                    note.UpdatedAt = DateTime.Now.AddMinutes(1);
                    db.SaveChanges();
                }

                int created = vm.Execute();
                Assert.Equal(0, created);
                Assert.False(vm.HasCompletedAssembly);
                return false;
            }

            int assembled = vm.Execute();
            Assert.True(assembled > 0);
            return true;
        };

        mainVm.OpenNoteAssembly();
        Assert.Equal(0, scheduler.EnqueueLocalChangeCallCount);
        using (var db = SqliteTestUtil.CreateContext(_dbPath))
        {
            Assert.Equal(2, db.Notes.Count());
        }

        phase = 1;
        mainVm.OpenNoteAssembly();
        Assert.Equal(1, scheduler.EnqueueLocalChangeCallCount);
        using (var db = SqliteTestUtil.CreateContext(_dbPath))
        {
            Assert.Equal(3, db.Notes.Count());
            Assert.Contains(db.Notes, n => n.Title == "Синх");
        }
    }

    private static ILocalMutationCoordinator Coord() => new LocalMutationCoordinator();

    private static Note AddNote(QuickNotesDbContext db, string title, string text)
    {
        var note = new Note { Title = title, Text = text, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
        db.Notes.Add(note);
        db.SaveChanges();
        return note;
    }

    private static NoteAssemblyPreviewRequest Req(IReadOnlyList<int> ids, string title) =>
        new()
        {
            SourceNoteIds = ids,
            Title = title,
            SeparatorKind = NoteAssemblySeparatorKind.ThematicBreakDash
        };
}
