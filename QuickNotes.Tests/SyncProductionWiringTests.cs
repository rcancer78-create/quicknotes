using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.Services;
using QuickNotes.App.Services.Sync;
using QuickNotes.App.ViewModels;
using Xunit;

namespace QuickNotes.Tests;

public class FakeTrackingSyncScheduler : ISyncScheduler
{
    private readonly Func<QuickNotesDbContext>? _dbFactory;

    public int EnqueueLocalChangeCallCount { get; set; }
    public int ManualSyncCallCount { get; set; }
    public int StartCallCount { get; set; }
    public int CancelCallCount { get; set; }
    public int NotifySettingsChangedCallCount { get; set; }

    public bool? NoteCommittedInDbWhenEnqueued { get; private set; }
    public string? ExpectedNoteTextForCommitCheck { get; set; }

    public SyncSchedulerQueueState QueueState { get; set; } = SyncSchedulerQueueState.Idle;
    public string QueueStatusDescription { get; set; } = "Готов";
    public IReadOnlyCollection<SyncTriggerReason> PendingReasons { get; set; } = Array.Empty<SyncTriggerReason>();
    public int PendingReasonsCount => PendingReasons.Count;
    public bool IsSyncRunning => QueueState == SyncSchedulerQueueState.Syncing;
    public DateTime? LastSuccessTimeUtc { get; set; }
    public DateTime? LastAttemptTimeUtc { get; set; }
    public SyncCycleResult? LastResult { get; set; }
    public int UnresolvedConflictsCount { get; set; }
    public TimeSpan? NextRetryDelay { get; set; }

    public LocalCommitSyncStatus PublicationStatus { get; set; } = LocalCommitSyncStatus.SavedLocally;

    public event EventHandler<SyncSchedulerStatusChangedEventArgs>? StatusChanged;
    public event EventHandler<SyncCycleResult>? SyncCompleted;

    public void RaiseStatusChanged(SyncSchedulerStatusChangedEventArgs args)
        => StatusChanged?.Invoke(this, args);

    public void RaiseSyncCompleted(SyncCycleResult result)
        => SyncCompleted?.Invoke(this, result);

    public FakeTrackingSyncScheduler(Func<QuickNotesDbContext>? dbFactory = null)
    {
        _dbFactory = dbFactory;
    }

    public void Start() => StartCallCount++;
    public void Stop() { }
    public virtual Task<bool> DrainAndStopAsync(TimeSpan timeout, CancellationToken ct = default) => Task.FromResult(true);

    public void EnqueueLocalChange()
    {
        EnqueueLocalChangeCallCount++;
        if (_dbFactory != null && !string.IsNullOrEmpty(ExpectedNoteTextForCommitCheck))
        {
            using var db = _dbFactory();
            // Strictly check if the entity exists in DB right now
            NoteCommittedInDbWhenEnqueued = db.Notes.Any(n => n.Text == ExpectedNoteTextForCommitCheck);
        }
    }

    public virtual Task<SyncCycleResult?> TriggerManualSyncAsync(CancellationToken ct = default)
    {
        ManualSyncCallCount++;
        return Task.FromResult<SyncCycleResult?>(SyncCycleResult.Succeeded(noOp: true));
    }

    public void CancelCurrentCycle() => CancelCallCount++;
    public void NotifySettingsChanged(SyncCloudSettings? newSettings = null) => NotifySettingsChangedCallCount++;
    public Task RefreshStateAsync(CancellationToken ct = default) => Task.CompletedTask;
    public void Dispose() { }
}

[TestCategory(TestCategories.Integration)]
public class SyncProductionWiringTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _settingsFilePath;
    private readonly SettingsService _settingsService;
    private readonly FakeTrackingSyncScheduler _scheduler;
    private readonly NoteTemplateService _templateService;
    private readonly TagDetectionService _tagDetectionService;
    private readonly MainViewModel _mainVm;

    public SyncProductionWiringTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"wiring_test_{Guid.NewGuid():N}.db");
        _settingsFilePath = Path.Combine(Path.GetTempPath(), $"wiring_settings_{Guid.NewGuid():N}.json");

        using (var initDb = new QuickNotesDbContext(_dbPath))
        {
            initDb.Database.EnsureCreated();
        }

        _settingsService = new SettingsService(_settingsFilePath, _ => { });
        _scheduler = new FakeTrackingSyncScheduler(() => new QuickNotesDbContext(_dbPath));
        _templateService = new NoteTemplateService(() => new QuickNotesDbContext(_dbPath));

        var tagRuleService = new TagRuleService();
        _tagDetectionService = new TagDetectionService(tagRuleService);
        var searchService = new SearchService();
        var hotkeyService = new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero);
        var clipboardCapture = new ClipboardCaptureService();
        var trayIcon = new TrayIconService();
        var backupService = new BackupService();

        _mainVm = MainViewModelTestComposition.Create(
            () => new QuickNotesDbContext(_dbPath),
            _tagDetectionService,
            searchService,
            _settingsService,
            hotkeyService,
            clipboardCapture,
            trayIcon,
            backupService,
            noteTemplateService: _templateService,
            tagRuleService: tagRuleService,
            syncScheduler: _scheduler,
            draftJournalService: NoOpDraftJournalService.Instance);
        _mainVm.NotificationHandler = (_, _) => { };
        _mainVm.AlertHandler = (_, _, _) => { };
    }

    public void Dispose()
    {
        _mainVm.Dispose();
        if (File.Exists(_dbPath)) try { File.Delete(_dbPath); } catch { }
        if (File.Exists(_settingsFilePath)) try { File.Delete(_settingsFilePath); } catch { }
    }

    [Fact]
    public void SaveInstantNote_CallsEnqueueLocalChange_StrictlyAfterDatabaseCommit()
    {
        _scheduler.ExpectedNoteTextForCommitCheck = "Instant Test Content";
        var capture = new CapturedNoteContext
        {
            Text = "Instant Test Content",
            ProcessName = "notepad.exe",
            WindowTitle = "Untitled - Notepad"
        };

        var note = _mainVm.SaveInstantNote(capture);

        Assert.NotNull(note);
        Assert.Equal(1, _scheduler.EnqueueLocalChangeCallCount);
        // Verified note was already committed in DB before EnqueueLocalChange was called!
        Assert.True(_scheduler.NoteCommittedInDbWhenEnqueued);
    }

    [Fact]
    public void NoteMutations_CallEnqueueLocalChange()
    {
        int noteId;
        using (var db = new QuickNotesDbContext(_dbPath))
        {
            var note = new Note
            {
                Text = "Mutable Note Content",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                SyncId = Guid.NewGuid()
            };
            db.Notes.Add(note);
            db.SaveChanges();
            noteId = note.Id;
        }

        _scheduler.EnqueueLocalChangeCallCount = 0;

        // 1. Toggle Pin
        _mainVm.TogglePinNote(noteId);
        Assert.Equal(1, _scheduler.EnqueueLocalChangeCallCount);

        // 2. Toggle Favorite
        _mainVm.ToggleFavoriteNote(noteId);
        Assert.Equal(2, _scheduler.EnqueueLocalChangeCallCount);

        // 3. Mark Processed
        _mainVm.MarkNoteProcessed(noteId);
        Assert.Equal(3, _scheduler.EnqueueLocalChangeCallCount);

        // 4. Restore Note
        _mainVm.RestoreNote(noteId);
        Assert.Equal(4, _scheduler.EnqueueLocalChangeCallCount);
    }

    [Fact]
    public void TagMutations_CallEnqueueLocalChange()
    {
        _scheduler.EnqueueLocalChangeCallCount = 0;

        // Create child tag from suggestion
        bool createdChild = _mainVm.CreateTagFromSuggestion("childtag");
        Assert.True(createdChild);
        Assert.True(_scheduler.EnqueueLocalChangeCallCount >= 1);

        // Create parent tag from suggestion
        bool createdParent = _mainVm.CreateTagFromSuggestion("parenttag");
        Assert.True(createdParent);

        int childTagId;
        int parentTagId;
        using (var db = new QuickNotesDbContext(_dbPath))
        {
            childTagId = db.Tags.First(t => t.Name == "childtag").Id;
            parentTagId = db.Tags.First(t => t.Name == "parenttag").Id;
        }

        int countBefore = _scheduler.EnqueueLocalChangeCallCount;
        bool moved = _mainVm.MoveTag(childTagId, parentTagId);
        Assert.True(moved);
        Assert.True(_scheduler.EnqueueLocalChangeCallCount > countBefore);

        using (var db = new QuickNotesDbContext(_dbPath))
        {
            var savedChild = db.Tags.Find(childTagId);
            Assert.NotNull(savedChild);
            Assert.Equal(parentTagId, savedChild.ParentTagId);
        }
    }

    [Fact]
    public void TemplateMutations_CallEnqueueLocalChange()
    {
        _scheduler.EnqueueLocalChangeCallCount = 0;

        // Create template
        var res1 = _templateService.CreateTemplate("Meeting Notes", "Template content");
        Assert.True(res1.Success);
        Assert.NotNull(res1.Template);
        Assert.Equal(1, _scheduler.EnqueueLocalChangeCallCount);

        // Update template
        var res2 = _templateService.UpdateTemplate(res1.Template.Id, "Updated Meeting Notes", "New content");
        Assert.True(res2.Success);
        Assert.Equal(2, _scheduler.EnqueueLocalChangeCallCount);

        // Delete template
        bool deleted = _templateService.DeleteTemplate(res1.Template.Id);
        Assert.True(deleted);
        Assert.Equal(3, _scheduler.EnqueueLocalChangeCallCount);
    }

    [Fact]
    public void MachineSettings_DoNotCallEnqueueLocalChange()
    {
        _scheduler.EnqueueLocalChangeCallCount = 0;
        _scheduler.NotifySettingsChangedCallCount = 0;

        // Changing machine UI settings (theme, hotkey, compact card, auto purge)
        var settings = _settingsService.CurrentSettings;
        settings.Theme = AppTheme.Dark;
        settings.AutoPurgeTrashDays = 14;
        _settingsService.SaveSettings(settings);

        _scheduler.NotifySettingsChanged();

        // EnqueueLocalChange must NOT have been called
        Assert.Equal(0, _scheduler.EnqueueLocalChangeCallCount);
        // Only NotifySettingsChanged was called
        Assert.Equal(1, _scheduler.NotifySettingsChangedCallCount);
    }

    [Fact]
    public void NoteEditorViewModel_NoteSavedEvent_TriggersEnqueueLocalChange()
    {
        using var editorVm = new NoteEditorViewModel(
            _tagDetectionService,
            new List<Tag>(),
            contextFactory: () => new QuickNotesDbContext(_dbPath),
            settingsService: _settingsService,
            draftJournalService: NoOpDraftJournalService.Instance);

        int localChangeCount = 0;
        editorVm.NoteSaved += () => localChangeCount++;

        editorVm.NotifyNoteSaved();

        Assert.Equal(1, localChangeCount);
    }
}
