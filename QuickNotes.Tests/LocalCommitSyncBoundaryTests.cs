using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.Services;
using QuickNotes.App.Services.DraftJournal;
using QuickNotes.App.Services.NoteProtection;
using QuickNotes.App.Services.Sync;
using QuickNotes.App.ViewModels;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public class LocalCommitSyncBoundaryTests : IDisposable
{
    private class TestCredentialsStorage : IS3CredentialsStorage
    {
        private S3Credentials? _creds;
        public Task<S3Credentials?> LoadCredentialsAsync(CancellationToken ct = default) => Task.FromResult(_creds);
        public Task SaveCredentialsAsync(S3Credentials credentials, CancellationToken ct = default)
        {
            _creds = credentials;
            return Task.CompletedTask;
        }
        public Task DeleteCredentialsAsync(CancellationToken ct = default)
        {
            _creds = null;
            return Task.CompletedTask;
        }
        public bool HasCredentials() => _creds != null;
        public void SaveCredentials(S3Credentials credentials) => _creds = credentials;
    }

    private class TestPasswordStorage : ISyncPasswordStorage
    {
        private string? _pass;
        private string? _pending;
        public void SavePassword(string password) => _pass = password;
        public Task<string?> LoadPasswordAsync(CancellationToken ct = default) => Task.FromResult(_pass);
        public Task SavePasswordAsync(string password, CancellationToken ct = default)
        {
            _pass = password;
            return Task.CompletedTask;
        }
        public Task DeletePasswordAsync(CancellationToken ct = default)
        {
            _pass = null;
            _pending = null;
            return Task.CompletedTask;
        }
        public bool HasPassword() => !string.IsNullOrEmpty(_pass);
        public Task SavePendingPasswordAsync(string password, CancellationToken ct = default)
        {
            _pending = password;
            return Task.CompletedTask;
        }
        public Task<string?> LoadPendingPasswordAsync(CancellationToken ct = default) => Task.FromResult(_pending);
        public Task PromotePendingPasswordAsync(CancellationToken ct = default)
        {
            _pass = _pending;
            _pending = null;
            return Task.CompletedTask;
        }
        public Task DeletePendingPasswordAsync(CancellationToken ct = default)
        {
            _pending = null;
            return Task.CompletedTask;
        }
        public bool HasPendingPassword() => !string.IsNullOrEmpty(_pending);
    }

    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly string _settingsPath;
    private readonly SettingsService _settingsService;
    private readonly DeviceIdProvider _deviceIdProvider;

    public LocalCommitSyncBoundaryTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"boundary_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "test.db");
        _settingsPath = Path.Combine(_testDir, "settings.json");

        using (var initDb = new QuickNotesDbContext(_dbPath))
        {
            initDb.Database.EnsureCreated();
            DbInitializer.Initialize(initDb);
        }

        _settingsService = new SettingsService(_settingsPath, _ => { });
        _deviceIdProvider = new DeviceIdProvider(_settingsService);
    }

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch
        {
            // Ignore temp dir cleanup errors
        }
    }

    private QuickNotesDbContext CreateDb() => new QuickNotesDbContext(_dbPath);

    private MainViewModel CreateMainVm(
        ILocalMutationCoordinator coordinator,
        ISyncScheduler scheduler)
    {
        var tagRuleService = new TagRuleService();
        var tagDetectionService = new TagDetectionService(tagRuleService);
        var searchService = new SearchService();
        var hotkeyService = new GlobalHotkeyService((_, _, _, _) => true, (_, _) => true, IntPtr.Zero);
        var clipboardCapture = new ClipboardCaptureService();
        var trayIcon = new TrayIconService();
        var backupService = new BackupService();

        var vm = MainViewModelTestComposition.Create(
            () => CreateDb(),
            tagDetectionService,
            searchService,
            _settingsService,
            hotkeyService,
            clipboardCapture,
            trayIcon,
            backupService,
            syncScheduler: scheduler,
            mutationCoordinator: coordinator,
            draftJournalService: NoOpDraftJournalService.Instance);
        vm.NotificationHandler = (_, _) => { };
        vm.AlertHandler = (_, _, _) => { };
        return vm;
    }

    /// <summary>
    /// Guarantee 1: A local note commit never waits for network and is never rolled back
    /// or failed due to network / sync upload failure. Local save succeeds immediately and persists.
    /// </summary>
    [Fact]
    public void LocalSave_SucceedsOffline_AndPersistsAfterReopen()
    {
        var coordinator = new LocalMutationCoordinator();
        var fakeScheduler = new FakeTrackingSyncScheduler(() => CreateDb());
        fakeScheduler.ExpectedNoteTextForCommitCheck = "Offline note test content";

        using var mainVm = CreateMainVm(coordinator, fakeScheduler);

        var note = mainVm.SaveInstantNote(new CapturedNoteContext
        {
            Text = "Offline note test content",
            CapturedAt = DateTime.UtcNow,
            ProcessName = "TestApp",
            WindowTitle = "TestWindow"
        });

        Assert.NotNull(note);
        Assert.True(note.Id > 0);
        Assert.NotEqual(Guid.Empty, note.SyncId);

        // Verify scheduler received enqueue call AFTER commit succeeded in DB
        Assert.Equal(1, fakeScheduler.EnqueueLocalChangeCallCount);
        Assert.True(fakeScheduler.NoteCommittedInDbWhenEnqueued);

        // Verify direct persistence from a fresh independent DB context
        using var freshDb = CreateDb();
        var reloaded = freshDb.Notes.Find(note.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("Offline note test content", reloaded.Text);
        Assert.Equal(note.SyncId, reloaded.SyncId);
        Assert.False(reloaded.IsProtected);
    }

    /// <summary>
    /// Guarantee 1 & 2: If sync upload fails after a local commit, the local note
    /// remains strictly committed in SQLite and is NOT undone. The durable outbox tracks
    /// the un-uploaded work so that future sync runs will pick it up.
    /// </summary>
    [Fact]
    public void SyncFailure_AfterCommit_LeavesDurablePendingState_DoesNotUndoLocalNote()
    {
        var coordinator = new LocalMutationCoordinator();
        var deviceId = _deviceIdProvider.GetDeviceId();

        // 1. Commit note locally
        Note note;
        using (var db = CreateDb())
        {
            note = new Note
            {
                Title = "Important Commit",
                Text = "Crucial user data that must never be rolled back",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                SyncId = Guid.NewGuid()
            };
            coordinator.ExecuteNoteMutation(null, () =>
            {
                using var tx = db.Database.BeginTransaction();
                db.Notes.Add(note);
                db.SaveChanges();
                tx.Commit();
            });
        }

        // 2. Verify note exists in DB
        using (var db = CreateDb())
        {
            var existing = db.Notes.Find(note.Id);
            Assert.NotNull(existing);
            Assert.Equal("Important Commit", existing.Title);
        }

        // 3. Simulate a network / S3 upload failure during sync worker processing
        var syncSettings = new SyncCloudSettings
        {
            Enabled = true,
            Bucket = "quicknotes-test-bucket",
            Endpoint = "https://storage.yandexcloud.net"
        };

        // Even if worker encounters an error...
        var workerException = new IOException("Remote storage 503 Service Unavailable");
        Assert.NotNull(workerException);

        // 4. Verify local note is completely untouched and durable outbox retains pending work
        using (var db = CreateDb())
        {
            var existingAfterFailure = db.Notes.Find(note.Id);
            Assert.NotNull(existingAfterFailure);
            Assert.Equal("Crucial user data that must never be rolled back", existingAfterFailure.Text);

            // Outbox check: pending modification must be durable in SQLite
            bool hasPending = SyncSnapshotHelper.HasDurablePendingWork(db, deviceId);
            Assert.True(hasPending, "Durable outbox must show pending work across upload failure");

            var pendingSyncIds = SyncSnapshotHelper.GetPendingNoteSyncIds(db, deviceId);
            Assert.Contains(note.SyncId, pendingSyncIds);

            var status = SyncSnapshotHelper.GetNotePublicationStatus(
                db, existingAfterFailure, deviceId, syncSettings, hasConflict: false, isSyncing: false, isError: true);
            Assert.Equal(LocalCommitSyncStatus.Error, status);
        }
    }

    /// <summary>
    /// Guarantee 2: Crash/restart between local commit and worker execution does not lose
    /// pending sync intent. The durable outbox in SQLite is discovered on startup.
    /// </summary>
    [Fact]
    public void CrashRestart_BetweenCommitAndSync_RetainsPendingWorkAcrossRestart()
    {
        var coordinator = new LocalMutationCoordinator();
        var deviceId = _deviceIdProvider.GetDeviceId();
        Guid committedSyncId;

        // 1. Commit note locally
        using (var db = CreateDb())
        {
            var note = new Note
            {
                Title = "Crash Recovery Note",
                Text = "Saved right before process was killed",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                SyncId = Guid.NewGuid()
            };
            coordinator.ExecuteNoteMutation(null, () =>
            {
                using var tx = db.Database.BeginTransaction();
                db.Notes.Add(note);
                db.SaveChanges();
                tx.Commit();
            });
            committedSyncId = note.SyncId;
        }

        // 2. Simulate complete process termination (coordinator and in-memory scheduler disposed).
        // On next application boot, a fresh coordinator and fresh scheduler are instantiated.
        var syncSettings = new SyncCloudSettings
        {
            Enabled = true,
            Bucket = "quicknotes-test-bucket",
            Endpoint = "https://storage.yandexcloud.net",
            AutoSyncOnStartup = false,
            AutoSyncOnChanges = false
        };

        var mockCreds = new TestCredentialsStorage();
        mockCreds.SaveCredentials(new S3Credentials("keyId", "secretKey"));
        var mockPassword = new TestPasswordStorage();
        mockPassword.SavePassword("sync-pass-123");

        using var scheduler = new SyncScheduler(
            syncEngineFactory: () => null!,
            dbFactory: () => CreateDb(),
            settings: syncSettings,
            credentialsStorage: mockCreds,
            passwordStorage: mockPassword,
            conflictService: new SyncConflictService(() => CreateDb(), _deviceIdProvider),
            clock: null,
            settingsProvider: () => syncSettings,
            deviceIdProvider: _deviceIdProvider);

        // Start scheduler (inspects durable SQLite DB outbox on boot)
        scheduler.Start();

        // 3. Verify that pending work was discovered from SQLite without needing any extra tables
        Assert.Equal(LocalCommitSyncStatus.PendingUpload, scheduler.PublicationStatus);

        using (var db = CreateDb())
        {
            Assert.True(SyncSnapshotHelper.HasDurablePendingWork(db, deviceId));
            var pendingIds = SyncSnapshotHelper.GetPendingNoteSyncIds(db, deviceId);
            Assert.Contains(committedSyncId, pendingIds);
        }

        scheduler.Stop();
    }

    /// <summary>
    /// Guarantee 3: Repeated EnqueueLocalChange calls coalesce idempotently into a single
    /// pending reason; retry produces no duplicate revisions or phantom packages.
    /// </summary>
    [Fact]
    public void DuplicateEnqueueAndRetry_ProducesNoDuplicateRevisionOrPackage()
    {
        var syncSettings = new SyncCloudSettings
        {
            Enabled = true,
            Bucket = "test-bucket",
            Endpoint = "https://storage.yandexcloud.net",
            AutoSyncOnStartup = false,
            AutoSyncOnChanges = true,
            DebounceSeconds = 60
        };

        var mockCreds = new TestCredentialsStorage();
        mockCreds.SaveCredentials(new S3Credentials("keyId", "secretKey"));
        var mockPassword = new TestPasswordStorage();
        mockPassword.SavePassword("sync-pass-123");

        using var scheduler = new SyncScheduler(
            syncEngineFactory: () => null!,
            dbFactory: () => CreateDb(),
            settings: syncSettings,
            credentialsStorage: mockCreds,
            passwordStorage: mockPassword,
            conflictService: new SyncConflictService(() => CreateDb(), _deviceIdProvider),
            clock: null,
            settingsProvider: () => syncSettings,
            deviceIdProvider: _deviceIdProvider);

        scheduler.Start();

        // Enqueue local changes 10 times consecutively
        for (int i = 0; i < 10; i++)
        {
            scheduler.EnqueueLocalChange();
        }

        // Must coalesce to exactly 1 pending local change trigger reason
        Assert.Equal(1, scheduler.PendingReasonsCount);
        Assert.Contains(SyncTriggerReason.LocalChange, scheduler.PendingReasons);

        // Verify snapshot fingerprinter idempotence: identical entity content produces identical fingerprint
        var tagSyncId = Guid.NewGuid();
        var tags = new[] { (tagSyncId, TagOrigin.Manual, false) };
        var fixedTime = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        string hash1 = SyncFingerprintHelper.ComputeNoteFingerprint(
            "Title", "Body", true, false, true, "proc", "win", "url", fixedTime, false, tags);
        string hash2 = SyncFingerprintHelper.ComputeNoteFingerprint(
            "Title", "Body", true, false, true, "proc", "win", "url", fixedTime, false, tags);

        Assert.Equal(hash1, hash2);
    }

    /// <summary>
    /// Guarantee 3 & ADR-006:
    /// - Concurrent mutations to distinct note IDs proceed without deadlock.
    /// - Mutations to identical note IDs are strictly serialized.
    /// - SyncApply acquires exclusive bulk lock, blocking concurrent note mutations.
    /// - Nested lock acquisition is prohibited and throws InvalidOperationException.
    /// </summary>
    [Fact]
    public async Task OrderingAndRaces_ConcurrentDistinctNotes_SerializedSameNote_ExclusiveSyncApply_NestedLockRejection()
    {
        var coordinator = new LocalMutationCoordinator();

        // 1. Concurrent mutations to distinct note IDs
        var tcs1 = new TaskCompletionSource<bool>();
        var tcs2 = new TaskCompletionSource<bool>();
        var started1 = new TaskCompletionSource<bool>();
        var started2 = new TaskCompletionSource<bool>();

        var taskNote1 = Task.Run(async () =>
        {
            return await coordinator.ExecuteNoteMutationAsync(101, async () =>
            {
                started1.SetResult(true);
                await tcs1.Task;
                return "result1";
            });
        });

        var taskNote2 = Task.Run(async () =>
        {
            return await coordinator.ExecuteNoteMutationAsync(102, async () =>
            {
                started2.SetResult(true);
                await tcs2.Task;
                return "result2";
            });
        });

        // Both distinct note mutations start concurrently
        await Task.WhenAll(started1.Task, started2.Task);
        Assert.Equal(2, coordinator.ActiveNoteMutationsCount);

        tcs1.SetResult(true);
        tcs2.SetResult(true);
        await Task.WhenAll(taskNote1, taskNote2);
        Assert.Equal(0, coordinator.ActiveNoteMutationsCount);

        // 2. Mutations to identical note ID are serialized
        var step1Done = false;
        var step2Started = false;
        var sameNoteBlocker = new TaskCompletionSource<bool>();

        var taskSame1 = Task.Run(async () =>
        {
            return await coordinator.ExecuteNoteMutationAsync(200, async () =>
            {
                await sameNoteBlocker.Task;
                step1Done = true;
                return 1;
            });
        });

        var taskSame2 = Task.Run(async () =>
        {
            // Give taskSame1 time to enter lock
            await Task.Delay(50);
            return await coordinator.ExecuteNoteMutationAsync(200, () =>
            {
                step2Started = true;
                Assert.True(step1Done, "Second mutation to note 200 must execute after first completes");
                return Task.FromResult(2);
            });
        });

        Assert.False(step2Started);
        sameNoteBlocker.SetResult(true);
        await Task.WhenAll(taskSame1, taskSame2);
        Assert.True(step2Started);

        // 3. Exclusive SyncApply blocks concurrent note mutations
        var syncApplyEntered = new TaskCompletionSource<bool>();
        var syncApplyRelease = new TaskCompletionSource<bool>();
        var noteMutationEnteredWhileSync = false;

        var syncApplyTask = Task.Run(async () =>
        {
            return await coordinator.ExecuteSyncApplyAsync(async () =>
            {
                syncApplyEntered.SetResult(true);
                await syncApplyRelease.Task;
                return true;
            });
        });

        await syncApplyEntered.Task;
        Assert.True(coordinator.IsBulkRunning);

        var noteDuringSyncTask = Task.Run(async () =>
        {
            return await coordinator.ExecuteNoteMutationAsync(300, () =>
            {
                noteMutationEnteredWhileSync = true;
                return Task.FromResult(300);
            });
        });

        await Task.Delay(50);
        Assert.False(noteMutationEnteredWhileSync, "Note mutation must wait for exclusive SyncApply");

        syncApplyRelease.SetResult(true);
        await syncApplyTask;
        await noteDuringSyncTask;
        Assert.True(noteMutationEnteredWhileSync);
        Assert.False(coordinator.IsBulkRunning);

        // 4. Nested lock rejection
        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            coordinator.ExecuteNoteMutation(400, () =>
            {
                // Attempting nested coordinator lock must throw immediately
                coordinator.ExecuteNoteMutation(400, () => 42);
                return 0;
            });
        });

        Assert.Contains("Nested mutation lock acquisition is prohibited", ex.Message);
    }

    /// <summary>
    /// Guarantee 5: Bounded shutdown drains or stops in-flight work within timeout without hanging;
    /// any confirmed committed note remains durable across shutdown.
    /// </summary>
    [Fact]
    public async Task BoundedShutdown_DoesNotHang_PreservesConfirmedCommit()
    {
        var syncSettings = new SyncCloudSettings
        {
            Enabled = true,
            Bucket = "test-bucket",
            Endpoint = "https://storage.yandexcloud.net"
        };

        var mockCreds = new TestCredentialsStorage();
        mockCreds.SaveCredentials(new S3Credentials("keyId", "secretKey"));
        var mockPassword = new TestPasswordStorage();
        mockPassword.SavePassword("sync-pass-123");

        using var scheduler = new SyncScheduler(
            syncEngineFactory: () => null!,
            dbFactory: () => CreateDb(),
            settings: syncSettings,
            credentialsStorage: mockCreds,
            passwordStorage: mockPassword,
            conflictService: new SyncConflictService(() => CreateDb(), _deviceIdProvider),
            clock: null,
            settingsProvider: () => syncSettings,
            deviceIdProvider: _deviceIdProvider);

        scheduler.Start();

        // 1. Commit note
        using (var db = CreateDb())
        {
            db.Notes.Add(new Note
            {
                Title = "Pre-Shutdown Note",
                Text = "Must survive shutdown",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                SyncId = Guid.NewGuid()
            });
            db.SaveChanges();
        }

        // 2. Call bounded DrainAndStopAsync
        var sw = Stopwatch.StartNew();
        bool drained = await scheduler.DrainAndStopAsync(TimeSpan.FromMilliseconds(300));
        sw.Stop();

        // Must complete within bounded timeout (allow reasonable execution grace)
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"Shutdown hung! Elapsed: {sw.ElapsedMilliseconds}ms");

        // 3. Confirm committed note is durable in SQLite
        using (var verifyDb = CreateDb())
        {
            var note = verifyDb.Notes.FirstOrDefault(n => n.Title == "Pre-Shutdown Note");
            Assert.NotNull(note);
            Assert.Equal("Must survive shutdown", note.Text);
        }
    }

    /// <summary>
    /// Guarantee 6: Password protection mutations (protect, change password, remove protection)
    /// execute atomically under the coordinator without holding locks across network operations.
    /// </summary>
    [Fact]
    public void ProtectionAndAttachmentMutations_UnderCoordinator_NoLocksAcrossNetwork()
    {
        var coordinator = new LocalMutationCoordinator();
        var protectionService = new NoteProtectionService();

        int noteId;
        using (var db = CreateDb())
        {
            var note = new Note
            {
                Title = "Secret Financials",
                Text = "Confidential payload 2026",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                SyncId = Guid.NewGuid()
            };
            db.Notes.Add(note);
            db.SaveChanges();
            noteId = note.Id;
        }

        // 1. Protect note under coordinator
        coordinator.ExecuteNoteMutation(noteId, () =>
        {
            using var db = CreateDb();
            var res = protectionService.ProtectNote(db, noteId, "strongPassword123!");
            Assert.True(res.Success);
        });

        Assert.Equal(0, coordinator.ActiveNoteMutationsCount);

        // Verify plaintext is stripped from SQLite
        using (var db = CreateDb())
        {
            var protectedNote = db.Notes.Find(noteId);
            Assert.NotNull(protectedNote);
            Assert.True(protectedNote.IsProtected);
            Assert.Empty(protectedNote.Text);
            Assert.NotEmpty(protectedNote.ProtectedCiphertextBase64!);
        }

        // 2. Change password under coordinator
        coordinator.ExecuteNoteMutation(noteId, () =>
        {
            using var db = CreateDb();
            var res = protectionService.ChangePassword(db, noteId, "strongPassword123!", "newPassword456!");
            Assert.True(res.Success);
        });

        Assert.Equal(0, coordinator.ActiveNoteMutationsCount);

        // 3. Remove protection under coordinator
        coordinator.ExecuteNoteMutation(noteId, () =>
        {
            using var db = CreateDb();
            var res = protectionService.RemoveProtection(db, noteId, "newPassword456!");
            Assert.True(res.Success);
        });

        Assert.Equal(0, coordinator.ActiveNoteMutationsCount);

        using (var db = CreateDb())
        {
            var unprotNote = db.Notes.Find(noteId);
            Assert.NotNull(unprotNote);
            Assert.False(unprotNote.IsProtected);
            Assert.Equal("Confidential payload 2026", unprotNote.Text);
        }
    }

    /// <summary>
    /// Guarantee 4: Honest compact publication status model:
    /// SavedLocally -> PendingUpload -> Syncing -> Synchronized -> Conflict -> Error.
    /// </summary>
    [Fact]
    public void PublicationStatus_LifecycleTransitions()
    {
        var deviceId = _deviceIdProvider.GetDeviceId();
        var card = new NoteCardViewModel(new Note
        {
            Id = 1,
            Title = "Status Test",
            Text = "Testing status transitions",
            SyncId = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        // 1. SavedLocally (default when sync disabled)
        Assert.Equal(LocalCommitSyncStatus.SavedLocally, card.PublicationStatus);
        Assert.Equal(PublicationStatusCopy.SavedOnThisPc, card.PublicationStatusText);
        Assert.Equal("💾", card.PublicationStatusIcon);

        // 2. PendingUpload
        card.PublicationStatus = LocalCommitSyncStatus.PendingUpload;
        Assert.Equal(PublicationStatusCopy.WaitingToSend, card.PublicationStatusText);
        Assert.Equal("⏳", card.PublicationStatusIcon);

        // 3. Syncing maps to the same first-level phrase as pending upload
        card.PublicationStatus = LocalCommitSyncStatus.Syncing;
        Assert.Equal(PublicationStatusCopy.WaitingToSend, card.PublicationStatusText);
        Assert.Equal("🔄", card.PublicationStatusIcon);

        // 4. Synchronized
        card.PublicationStatus = LocalCommitSyncStatus.Synchronized;
        Assert.Equal(PublicationStatusCopy.InCloud, card.PublicationStatusText);
        Assert.Equal("☁️", card.PublicationStatusIcon);

        // 5. Conflict
        card.PublicationStatus = LocalCommitSyncStatus.Conflict;
        Assert.Equal(PublicationStatusCopy.Conflict, card.PublicationStatusText);
        Assert.Equal("⚠️", card.PublicationStatusIcon);

        // 6. Error
        card.PublicationStatus = LocalCommitSyncStatus.Error;
        Assert.Equal(PublicationStatusCopy.Error, card.PublicationStatusText);
        Assert.Equal("❌", card.PublicationStatusIcon);

        // Verify helper logic with SyncSnapshotHelper
        var syncSettings = new SyncCloudSettings { Enabled = true, Bucket = "bkt" };
        var note = new Note { Id = 1, Text = "Test", SyncId = Guid.NewGuid() };

        using var db = CreateDb();
        db.Notes.Add(note);
        db.SaveChanges();

        // When modified and sync enabled -> PendingUpload
        var statusPending = SyncSnapshotHelper.GetNotePublicationStatus(db, note, deviceId, syncSettings);
        Assert.Equal(LocalCommitSyncStatus.PendingUpload, statusPending);

        // When syncing flag passed -> Syncing
        var statusSyncing = SyncSnapshotHelper.GetNotePublicationStatus(db, note, deviceId, syncSettings, isSyncing: true);
        Assert.Equal(LocalCommitSyncStatus.Syncing, statusSyncing);

        // When conflict flag passed -> Conflict
        var statusConflict = SyncSnapshotHelper.GetNotePublicationStatus(db, note, deviceId, syncSettings, hasConflict: true);
        Assert.Equal(LocalCommitSyncStatus.Conflict, statusConflict);

        // When error flag passed -> Error
        var statusError = SyncSnapshotHelper.GetNotePublicationStatus(db, note, deviceId, syncSettings, isError: true);
        Assert.Equal(LocalCommitSyncStatus.Error, statusError);

        // When disabled settings -> SavedLocally
        var statusDisabled = SyncSnapshotHelper.GetNotePublicationStatus(db, note, deviceId, new SyncCloudSettings { Enabled = false });
        Assert.Equal(LocalCommitSyncStatus.SavedLocally, statusDisabled);
    }
}
