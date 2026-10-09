using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.Services;
using QuickNotes.App.Services.Crypto;
using QuickNotes.App.Services.Sync;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public class SyncEngineTests : IDisposable
{
    private readonly List<string> _tempFiles = new();
    private readonly List<IDisposable> _disposables = new();

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            try { d.Dispose(); } catch { }
        }

        SqliteTestUtil.ReleasePools();

        foreach (var file in _tempFiles)
        {
            SqliteTestUtil.TryDeleteFileAndSiblings(file);
        }
    }

    private string CreateTempDbPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"quicknotes_engine_test_{Guid.NewGuid():N}.db");
        _tempFiles.Add(path);
        return path;
    }

    private QuickNotesDbContext CreateContext(string dbPath)
    {
        var context = SqliteTestUtil.CreateContext(dbPath);
        _disposables.Add(context);
        return context;
    }

    #region In-Memory Transport with Fault Injection

    public class TestCloudObjectStoreTransport : FaultInjectingCloudObjectStoreTransport
    {
    }
    #endregion

    private class InMemoryCredentialsStorage : IS3CredentialsStorage
    {
        private S3Credentials? _creds;
        public InMemoryCredentialsStorage(S3Credentials? creds = null)
        {
            _creds = creds;
        }

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
    }

    #region Device Node Harness

    private class TestDeviceNode : IDisposable
    {
        public Guid DeviceId { get; }
        public string DbPath { get; }
        public QuickNotesDbContext Db { get; }
        public SyncCryptoService Crypto { get; }
        public FixedDeviceIdProvider DeviceIdProvider { get; }
        public SyncPackageExporter Exporter { get; }
        public SyncPackageImporter Importer { get; }
        public SyncObjectKeyHelper KeyHelper { get; }
        public InMemoryCredentialsStorage CredentialsStorage { get; }
        public SyncEngine Engine { get; }
        public AttachmentStorageService AttachmentStorage { get; }
        public string AttachmentsRoot { get; }

        public TestDeviceNode(
            string dbPath,
            Guid deviceId,
            TestCloudObjectStoreTransport transport,
            SyncCloudSettings settings,
            ICloudUsageService? usageService = null)
        {
            DeviceId = deviceId;
            DbPath = dbPath;
            Db = SqliteTestUtil.CreateContext(dbPath);
            DbInitializer.Initialize(Db);

            Crypto = new SyncCryptoService(iterations: 5_000);
            DeviceIdProvider = new FixedDeviceIdProvider(deviceId);
            Exporter = new SyncPackageExporter(Crypto, DeviceIdProvider);
            Importer = new SyncPackageImporter(Crypto);
            KeyHelper = new SyncObjectKeyHelper(settings);
            CredentialsStorage = new InMemoryCredentialsStorage(new S3Credentials("testKey", "testSecret"));
            AttachmentsRoot = Path.Combine(Path.GetTempPath(), $"qn_engine_att_{Guid.NewGuid():N}");
            AttachmentStorage = new AttachmentStorageService(AttachmentsRoot);
            var blobService = new SyncAttachmentBlobService(transport, Crypto, AttachmentStorage, KeyHelper, settings, usageService);

            Engine = new SyncEngine(
                transport,
                Exporter,
                Importer,
                DeviceIdProvider,
                settings,
                CredentialsStorage,
                KeyHelper,
                dateTimeProvider: null,
                dbFactory: () => SqliteTestUtil.CreateContext(dbPath),
                blobService: blobService);
        }

        public async Task<SyncCycleResult> SyncAsync(string password, SyncCycleOptions? options = null, CancellationToken ct = default)
        {
            return await Engine.RunSyncCycleAsync(Db, password, options, ct: ct);
        }

        public void Dispose()
        {
            Db.Dispose();
            try
            {
                if (Directory.Exists(AttachmentsRoot))
                    Directory.Delete(AttachmentsRoot, recursive: true);
            }
            catch { }
        }
    }

    private TestDeviceNode CreateDevice(
        TestCloudObjectStoreTransport transport,
        SyncCloudSettings settings,
        Guid? deviceId = null,
        ICloudUsageService? usageService = null)
    {
        string path = CreateTempDbPath();
        var node = new TestDeviceNode(path, deviceId ?? Guid.NewGuid(), transport, settings, usageService);
        _disposables.Add(node);
        return node;
    }

    private SyncCloudSettings CreateDefaultSettings()
    {
        return new SyncCloudSettings
        {
            Enabled = true,
            Endpoint = "https://storage.yandexcloud.net",
            Region = "ru-central1",
            Bucket = "notes-sync-bucket",
            Prefix = "sync-root"
        };
    }

    #endregion

    #region Scenarios

    [Fact]
    public async Task Scenario1_TwoDevices_LinearSync_ExchangesNotesTagsAndUpdatesBidirectionally()
    {
        var transport = new TestCloudObjectStoreTransport();
        var settings = CreateDefaultSettings();
        string password = "StrongPassword2026!";

        var deviceA = CreateDevice(transport, settings);
        var deviceB = CreateDevice(transport, settings);

        // 1. Device A creates Note 1 and Tag 1
        var tag1 = new Tag { Name = "Work" };
        deviceA.Db.Tags.Add(tag1);
        var note1 = new Note
        {
            Text = "Hello from Device A",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        deviceA.Db.Notes.Add(note1);
        await deviceA.Db.SaveChangesAsync();

        // Device A syncs -> uploads package 1
        var resA1 = await deviceA.SyncAsync(password);
        Assert.True(resA1.Success, string.Join("; ", resA1.Errors));
        Assert.True(resA1.PackageUploaded);
        Assert.NotNull(resA1.UploadedPackageId);

        // 2. Device B syncs -> pulls package 1 from Device A
        var resB1 = await deviceB.SyncAsync(password);
        Assert.True(resB1.Success, string.Join("; ", resB1.Errors));
        Assert.Equal(1, resB1.RemotePackagesPulled);

        // Verify Device B now has Note 1 and Tag 1
        var noteOnB = await deviceB.Db.Notes.FirstOrDefaultAsync();
        Assert.NotNull(noteOnB);
        Assert.Equal("Hello from Device A", noteOnB.Text);
        var tagOnB = await deviceB.Db.Tags.FirstOrDefaultAsync();
        Assert.NotNull(tagOnB);
        Assert.Equal("Work", tagOnB.Name);

        // 3. Device B updates Note 1
        noteOnB.Text = "Updated by Device B";
        noteOnB.UpdatedAt = DateTime.UtcNow;
        await deviceB.Db.SaveChangesAsync();

        // Device B syncs -> uploads package 2
        var resB2 = await deviceB.SyncAsync(password);
        Assert.True(resB2.Success, string.Join("; ", resB2.Errors));
        Assert.True(resB2.PackageUploaded);

        // 4. Device A syncs -> pulls package 2 from Device B
        var resA2 = await deviceA.SyncAsync(password);
        Assert.True(resA2.Success, string.Join("; ", resA2.Errors));
        Assert.Equal(1, resA2.RemotePackagesPulled);

        // Verify Device A has updated note
        var noteOnA = await deviceA.Db.Notes.FirstOrDefaultAsync(n => n.Id == note1.Id);
        Assert.NotNull(noteOnA);
        Assert.Equal("Updated by Device B", noteOnA.Text);
    }

    [Fact]
    public async Task Scenario2_ThreeDevices_StarSync_PropagatesChangesAcrossAllNodes()
    {
        var transport = new TestCloudObjectStoreTransport();
        var settings = CreateDefaultSettings();
        string password = "SharedPasscode!333";

        var devA = CreateDevice(transport, settings);
        var devB = CreateDevice(transport, settings);
        var devC = CreateDevice(transport, settings);

        // Dev A creates Note A
        devA.Db.Notes.Add(new Note { Text = "Note from Dev A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await devA.Db.SaveChangesAsync();
        var resA = await devA.SyncAsync(password);
        Assert.True(resA.Success);

        // Dev B and Dev C sync -> receive Note A
        var resB = await devB.SyncAsync(password);
        Assert.True(resB.Success);
        Assert.Equal(1, resB.RemotePackagesPulled);
        Assert.Equal("Note from Dev A", (await devB.Db.Notes.FirstAsync()).Text);

        var resC = await devC.SyncAsync(password);
        Assert.True(resC.Success);
        Assert.Equal(1, resC.RemotePackagesPulled);
        Assert.Equal("Note from Dev A", (await devC.Db.Notes.FirstAsync()).Text);

        // Dev C creates Note C and Tag C
        devC.Db.Tags.Add(new Tag { Name = "C-Tag" });
        devC.Db.Notes.Add(new Note { Text = "Note from Dev C", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await devC.Db.SaveChangesAsync();

        var resC2 = await devC.SyncAsync(password);
        Assert.True(resC2.Success);
        Assert.True(resC2.PackageUploaded);

        // Dev A syncs -> pulls from C
        var resA2 = await devA.SyncAsync(password);
        Assert.True(resA2.Success);
        Assert.Equal(1, resA2.RemotePackagesPulled);
        Assert.Equal(2, await devA.Db.Notes.CountAsync());
        Assert.Equal(1, await devA.Db.Tags.CountAsync());

        // Dev B syncs -> pulls from C
        var resB2 = await devB.SyncAsync(password);
        Assert.True(resB2.Success);
        Assert.Equal(1, resB2.RemotePackagesPulled);
        Assert.Equal(2, await devB.Db.Notes.CountAsync());
        Assert.Equal(1, await devB.Db.Tags.CountAsync());
    }

    [Fact]
    public async Task Scenario3_RepeatNoOp_WhenNoLocalChanges_DoesNotUploadNewPackageOrPointer()
    {
        var transport = new TestCloudObjectStoreTransport();
        var settings = CreateDefaultSettings();
        string password = "NoOpPassword!123";

        var device = CreateDevice(transport, settings);

        // Initial change
        device.Db.Notes.Add(new Note { Text = "Initial Note", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await device.Db.SaveChangesAsync();

        // 1. First sync uploads 1 package
        var res1 = await device.SyncAsync(password);
        Assert.True(res1.Success);
        Assert.True(res1.PackageUploaded);
        int initialPutImmutable = transport.PutImmutableCallsCount;
        int initialPutConditional = transport.PutConditionalCallsCount;
        Assert.Equal(1, initialPutImmutable);
        Assert.Equal(1, initialPutConditional);

        // 2. Second sync with ZERO local or remote changes
        var res2 = await device.SyncAsync(password);
        Assert.True(res2.Success);
        Assert.True(res2.NoOp);
        Assert.False(res2.PackageUploaded);
        Assert.Null(res2.UploadedPackageId);

        // Strict guarantee: ZERO new objects written to cloud store
        Assert.Equal(initialPutImmutable, transport.PutImmutableCallsCount);
        Assert.Equal(initialPutConditional, transport.PutConditionalCallsCount);

        // 3. Third sync also clean no-op
        var res3 = await device.SyncAsync(password);
        Assert.True(res3.Success);
        Assert.True(res3.NoOp);
        Assert.False(res3.PackageUploaded);
        Assert.Equal(initialPutImmutable, transport.PutImmutableCallsCount);
    }

    [Fact]
    public async Task Scenario4_PullBeforePush_RemoteChangesIntegratedBeforeLocalExport()
    {
        var transport = new TestCloudObjectStoreTransport();
        var settings = CreateDefaultSettings();
        string password = "PullBeforePush!456";

        var devA = CreateDevice(transport, settings);
        var devB = CreateDevice(transport, settings);

        // Device A writes note A and uploads
        devA.Db.Notes.Add(new Note { Text = "Note from A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await devA.Db.SaveChangesAsync();
        var resA = await devA.SyncAsync(password);
        Assert.True(resA.Success);

        // Device B has local changes BEFORE syncing
        devB.Db.Notes.Add(new Note { Text = "Note from B", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await devB.Db.SaveChangesAsync();

        // Device B syncs: pull-before-push means it pulls A's note FIRST, then pushes its note
        var resB = await devB.SyncAsync(password);
        Assert.True(resB.Success);
        Assert.Equal(1, resB.RemotePackagesPulled);
        Assert.True(resB.PackageUploaded);

        // Device B now has BOTH notes
        Assert.Equal(2, await devB.Db.Notes.CountAsync());

        // Device A syncs: pulls B's package
        var resA2 = await devA.SyncAsync(password);
        Assert.True(resA2.Success);
        Assert.Equal(1, resA2.RemotePackagesPulled);

        // Device A now also has BOTH notes
        Assert.Equal(2, await devA.Db.Notes.CountAsync());
    }

    [Fact]
    public async Task Scenario5_ConcurrentModifications_CreatesDurableConflictRecord_DoesNotOverwriteLocal_AndDoesNotDuplicate()
    {
        var transport = new TestCloudObjectStoreTransport();
        var settings = CreateDefaultSettings();
        string password = "ConflictPassword!789";

        var devA = CreateDevice(transport, settings);
        var devB = CreateDevice(transport, settings);

        // 1. Initial shared note
        var note = new Note { Text = "Original Text", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        devA.Db.Notes.Add(note);
        await devA.Db.SaveChangesAsync();
        await devA.SyncAsync(password);

        // Sync to dev B
        await devB.SyncAsync(password);
        var noteOnB = await devB.Db.Notes.FirstAsync();
        Assert.Equal("Original Text", noteOnB.Text);

        // 2. Both devices edit note concurrently
        note.Text = "Version modified by Device A";
        note.UpdatedAt = DateTime.UtcNow;
        await devA.Db.SaveChangesAsync();
        // Dev A uploads package
        var resA1 = await devA.SyncAsync(password);
        Assert.True(resA1.Success);

        // Dev B modifies local copy
        noteOnB.Text = "Version modified by Device B";
        noteOnB.UpdatedAt = DateTime.UtcNow;
        await devB.Db.SaveChangesAsync();

        // 3. Dev B syncs -> pulls A's package -> detects concurrent branch conflict!
        var resB = await devB.SyncAsync(password);
        Assert.True(resB.Success);
        Assert.Equal(1, resB.ConflictsDetected);

        // Durable conflict record exists in SyncConflicts
        var conflicts = await devB.Db.SyncConflicts.ToListAsync();
        Assert.Single(conflicts);
        var conflict = conflicts[0];
        Assert.Equal("Note", conflict.EntityType);
        Assert.False(conflict.IsResolved);
        Assert.NotNull(conflict.LocalDataJson);
        Assert.NotNull(conflict.RemoteDataJson);
        Assert.Contains("Version modified by Device B", conflict.LocalDataJson);
        Assert.Contains("Version modified by Device A", conflict.RemoteDataJson);

        // Device B's local note was NOT overwritten
        var freshNoteOnB = await devB.Db.Notes.FirstAsync();
        Assert.Equal("Version modified by Device B", freshNoteOnB.Text);

        // 4. Repeated sync on Dev B does NOT duplicate conflict record
        var resB_repeat = await devB.SyncAsync(password);
        Assert.True(resB_repeat.Success);
        var conflictsAfterRepeat = await devB.Db.SyncConflicts.ToListAsync();
        Assert.Single(conflictsAfterRepeat);
    }

    [Fact]
    public async Task Scenario6_Tombstones_PropagateDeletionsWithoutResurrection()
    {
        var transport = new TestCloudObjectStoreTransport();
        var settings = CreateDefaultSettings();
        string password = "TombstonePass!111";

        var devA = CreateDevice(transport, settings);
        var devB = CreateDevice(transport, settings);

        // Dev A creates note
        var note = new Note { Text = "Note To Be Deleted", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        devA.Db.Notes.Add(note);
        await devA.Db.SaveChangesAsync();
        await devA.SyncAsync(password);

        // Dev B receives note
        await devB.SyncAsync(password);
        var noteOnB = await devB.Db.Notes.FirstAsync();
        Assert.Null(noteOnB.DeletedAt);

        // Dev A deletes note (soft delete)
        note.DeletedAt = DateTime.UtcNow;
        note.UpdatedAt = DateTime.UtcNow;
        await devA.Db.SaveChangesAsync();
        await devA.SyncAsync(password);

        // Dev B syncs: deletion is applied
        var resB = await devB.SyncAsync(password);
        Assert.True(resB.Success);
        Assert.Equal(1, resB.RemoteEntitiesApplied.Deleted);

        var refreshedB = await devB.Db.Notes.FirstAsync();
        Assert.NotNull(refreshedB.DeletedAt);

        // Dev B syncs again -> note does NOT resurrect
        await devB.SyncAsync(password);
        var refreshedB2 = await devB.Db.Notes.FirstAsync();
        Assert.NotNull(refreshedB2.DeletedAt);
    }

    [Fact]
    public async Task Scenario7_CorruptedPointer_SkipsGracefullyAndAllowsOtherDevicesToContinue()
    {
        var transport = new TestCloudObjectStoreTransport();
        var settings = CreateDefaultSettings();
        string password = "CorruptPointerPass!222";

        var devA = CreateDevice(transport, settings);
        var devB = CreateDevice(transport, settings);

        // Dev A writes note and syncs
        devA.Db.Notes.Add(new Note { Text = "Valid Note", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await devA.Db.SaveChangesAsync();
        await devA.SyncAsync(password);

        // Corrupt Device C pointer in cloud store (invalid JSON)
        Guid alienDevId = Guid.NewGuid();
        string alienPointerKey = new SyncObjectKeyHelper(settings).GetDevicePointerKey(alienDevId);
        await transport.PutConditionalPointerAsync(
            alienPointerKey,
            Encoding.UTF8.GetBytes("{{{NOT_A_VALID_JSON_MANIFEST!!!"),
            expectedETag: null);

        // Dev B syncs: should reject alien corrupted pointer, but successfully pull Dev A's package
        var resB = await devB.SyncAsync(password);
        Assert.True(resB.Success);
        Assert.Equal(1, resB.RemotePackagesPulled);

        var noteOnB = await devB.Db.Notes.FirstOrDefaultAsync();
        Assert.NotNull(noteOnB);
        Assert.Equal("Valid Note", noteOnB.Text);
        Assert.Contains(resB.Errors, e => e.Contains("Ошибка валидации указателя"));
    }

    [Fact]
    public async Task Scenario8_AlienNamespaceOrPathTraversal_RejectedByValidatorSafely()
    {
        var transport = new TestCloudObjectStoreTransport();
        var settings = CreateDefaultSettings();
        string password = "AlienNamespacePass!333";

        var devA = CreateDevice(transport, settings);
        var devB = CreateDevice(transport, settings);

        // Dev A writes note and syncs
        devA.Db.Notes.Add(new Note { Text = "Good Note", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await devA.Db.SaveChangesAsync();
        await devA.SyncAsync(password);

        // Malicious device pointer with path traversal
        Guid maliciousDev = Guid.NewGuid();
        var keyHelper = new SyncObjectKeyHelper(settings);
        string maliciousPointerKey = keyHelper.GetDevicePointerKey(maliciousDev);

        var maliciousPayload = new DevicePointerPayload
        {
            FormatVersion = 1,
            DeviceId = maliciousDev,
            LatestPackageId = Guid.NewGuid(),
            LatestPackageKey = "sync-root/devices/../../other-customer/secret.json", // Path traversal attempt
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            PackageCount = 1
        };

        byte[] payloadBytes = JsonSerializer.SerializeToUtf8Bytes(maliciousPayload);
        await transport.PutConditionalPointerAsync(maliciousPointerKey, payloadBytes, expectedETag: null);

        // Dev B syncs: validator must reject malicious pointer and continue safely
        var resB = await devB.SyncAsync(password);
        Assert.True(resB.Success);
        Assert.Equal(1, resB.RemotePackagesPulled);
        Assert.Equal("Good Note", (await devB.Db.Notes.FirstAsync()).Text);
        Assert.Contains(resB.Errors, e => e.Contains("path traversal") || e.Contains("Ошибка валидации"));
    }

    [Fact]
    public async Task Scenario9_CorruptedPackagePayload_CatchesSafely_LeavesDbIntact_ReportsError()
    {
        var transport = new TestCloudObjectStoreTransport();
        var settings = CreateDefaultSettings();
        string password = "CorruptPackagePass!444";

        var devA = CreateDevice(transport, settings);
        var devB = CreateDevice(transport, settings);

        // Dev A uploads package
        devA.Db.Notes.Add(new Note { Text = "Before Corruption", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await devA.Db.SaveChangesAsync();
        var resA = await devA.SyncAsync(password);
        Assert.True(resA.Success);

        // Corrupt Dev A's package payload in transport
        string packageKey = new SyncObjectKeyHelper(settings).GetPackageKey(devA.DeviceId, resA.UploadedPackageId!.Value);
        transport.CorruptObject(packageKey, Encoding.UTF8.GetBytes("CORRUPTED_CIPHERTEXT_GARBAGE"));

        // Dev B syncs: should fail for corrupted package, roll back transaction, DB remains clean
        var resB = await devB.SyncAsync(password);
        Assert.True(resB.Success); // Cycle completed with errors logged, didn't crash engine
        Assert.Equal(0, resB.RemotePackagesPulled);
        Assert.NotEmpty(resB.Errors);

        // Database on B remains clean (no corrupt or partially imported notes)
        Assert.Empty(await devB.Db.Notes.ToListAsync());

        // Device checkpoint was NOT saved for failed package
        var deviceStateOnB = await devB.Db.SyncDeviceStates.FirstOrDefaultAsync(s => s.DeviceId == devA.DeviceId);
        Assert.Null(deviceStateOnB);
    }

    [Fact]
    public async Task Scenario10_OfflineHandling_ReturnsOfflineResultWithoutCorruptingDatabase()
    {
        var transport = new TestCloudObjectStoreTransport { IsOffline = true };
        var settings = CreateDefaultSettings();
        string password = "OfflinePass!555";

        var dev = CreateDevice(transport, settings);
        dev.Db.Notes.Add(new Note { Text = "Offline Note", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await dev.Db.SaveChangesAsync();

        var res = await dev.SyncAsync(password);
        Assert.False(res.Success);
        Assert.True(res.IsOffline);
        Assert.Contains("offline", res.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        // DB still healthy
        Assert.Single(await dev.Db.Notes.ToListAsync());
    }

    [Fact]
    public async Task Scenario11_EtagRaceCondition_ReturnsConflictResult_PreservesPendingState()
    {
        var transport = new TestCloudObjectStoreTransport();
        var settings = CreateDefaultSettings();
        string password = "EtagConflictPass!666";

        var dev = CreateDevice(transport, settings);
        dev.Db.Notes.Add(new Note { Text = "Race Condition Note", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await dev.Db.SaveChangesAsync();

        // Simulate pointer conflict on conditional PUT
        transport.SimulatePointerConflictOnPut = true;

        var res = await dev.SyncAsync(password);
        Assert.False(res.Success);
        Assert.True(res.IsEtagConflict);

        // Verify PendingPackageId is preserved in SQLite for safe retry
        var localState = await dev.Db.SyncLocalStates.FirstOrDefaultAsync(s => s.DeviceId == dev.DeviceId);
        Assert.NotNull(localState);
        Assert.NotNull(localState.PendingPackageId);
        Assert.NotNull(localState.PendingPackageKey);
    }

    [Fact]
    public async Task Scenario12_IndeterminatePut_CrashRecovery_ReusesExistingPackageWithoutDuplicate()
    {
        var transport = new TestCloudObjectStoreTransport();
        var settings = CreateDefaultSettings();
        string password = "CrashRecoveryPass!777";

        var dev = CreateDevice(transport, settings);
        dev.Db.Notes.Add(new Note { Text = "Crash Recovery Note", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await dev.Db.SaveChangesAsync();

        // Simulate crash right during PutConditionalPointer
        transport.ThrowOnPutConditionalPointer = true;

        var res1 = await dev.SyncAsync(password);
        Assert.False(res1.Success);
        Assert.NotNull(res1.ErrorMessage);
        Assert.Contains("Simulated crash", res1.ErrorMessage);

        // Verify package was uploaded before crash, and local state marked pending
        var localState = await dev.Db.SyncLocalStates.FirstOrDefaultAsync(s => s.DeviceId == dev.DeviceId);
        Assert.NotNull(localState);
        Assert.NotNull(localState.PendingPackageId);
        Guid pendingPkgId = localState.PendingPackageId.Value;

        // Transport has 1 immutable package
        Assert.Equal(1, transport.PutImmutableCallsCount);
        string packageKey = new SyncObjectKeyHelper(settings).GetPackageKey(dev.DeviceId, pendingPkgId);
        var pkgMeta = await transport.HeadObjectAsync(packageKey);
        Assert.NotNull(pkgMeta);

        // Now restore connection and run sync again: recovery flow!
        transport.ThrowOnPutConditionalPointer = false;

        var res2 = await dev.SyncAsync(password);
        Assert.True(res2.Success, string.Join("; ", res2.Errors));
        Assert.True(res2.PackageUploaded);
        Assert.Equal(pendingPkgId, res2.UploadedPackageId);

        // Guarantee: DID NOT call PutImmutableObjectAsync a second time!
        Assert.Equal(1, transport.PutImmutableCallsCount);

        // Pending state is cleared
        var localStateAfter = await dev.Db.SyncLocalStates.FirstOrDefaultAsync(s => s.DeviceId == dev.DeviceId);
        Assert.NotNull(localStateAfter);
        Assert.Null(localStateAfter.PendingPackageId);
        Assert.Equal(pendingPkgId, localStateAfter.LastUploadedPackageId);
    }

    [Fact]
    public async Task Scenario13_MaxPackageSizeLimit_SkipsOversizedPackage()
    {
        var transport = new TestCloudObjectStoreTransport();
        var settings = CreateDefaultSettings();
        string password = "SizeLimitPass!888";

        var devA = CreateDevice(transport, settings);
        var devB = CreateDevice(transport, settings);

        // Dev A uploads a package
        devA.Db.Notes.Add(new Note { Text = "Normal Note Content", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await devA.Db.SaveChangesAsync();
        var resA = await devA.SyncAsync(password);
        Assert.True(resA.Success);

        // Dev B syncs with strict limit of 10 bytes: package will exceed limit
        var smallOptions = new SyncCycleOptions { MaxPackageSizeBytes = 10 };
        var resB = await devB.SyncAsync(password, smallOptions);
        Assert.True(resB.Success);
        Assert.Equal(0, resB.RemotePackagesPulled);
        Assert.Contains(resB.Diagnostics, d => d.Contains("превышает максимальный размер"));

        // Dev B did not pull note
        Assert.Empty(await devB.Db.Notes.ToListAsync());
    }

    [Fact]
    public async Task Scenario14_Cancellation_AbortsPromptlyWithoutCorruptingState()
    {
        var transport = new TestCloudObjectStoreTransport();
        var settings = CreateDefaultSettings();
        string password = "CancelPass!999";

        var dev = CreateDevice(transport, settings);
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancel

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await dev.SyncAsync(password, ct: cts.Token);
        });

        // Transport was not touched
        Assert.Equal(0, transport.PutImmutableCallsCount);
        Assert.Equal(0, transport.PutConditionalCallsCount);
    }

    [Fact]
    public void Scenario15_Migration_V6_To_V8_CreatesSyncTables_DurableIndices_Idempotent_AndBackups()
    {
        string dbPath = CreateTempDbPath();
        bool backupCalled = false;

        // 1. Create a legacy v6 database manually
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                PRAGMA user_version = 6;
                CREATE TABLE Notes (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    SyncId TEXT NOT NULL COLLATE NOCASE,
                    Text TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    DeletedAt TEXT NULL,
                    IsPinned INTEGER NOT NULL DEFAULT 0,
                    IsFavorite INTEGER NOT NULL DEFAULT 0,
                    IsInbox INTEGER NOT NULL DEFAULT 0,
                    SourceProcessName TEXT NULL,
                    SourceWindowTitle TEXT NULL,
                    SourceUrl TEXT NULL,
                    CapturedAt TEXT NULL
                );
                CREATE UNIQUE INDEX IX_Notes_SyncId ON Notes(SyncId);

                CREATE TABLE SyncEntityStates (
                    SyncId TEXT PRIMARY KEY COLLATE NOCASE,
                    EntityType TEXT NOT NULL,
                    RevisionId TEXT NOT NULL COLLATE NOCASE,
                    ParentRevisionId TEXT NULL COLLATE NOCASE,
                    DeviceId TEXT NOT NULL COLLATE NOCASE,
                    UpdatedAtUtc TEXT NOT NULL,
                    IsDeleted INTEGER NOT NULL DEFAULT 0,
                    DeletedAtUtc TEXT NULL,
                    ContentHash TEXT NULL
                );

                INSERT INTO Notes (SyncId, Text, CreatedAt, UpdatedAt)
                VALUES ('11111111-1111-1111-1111-111111111111', 'Sample Note', '2026-01-01', '2026-01-01');
            ";
            cmd.ExecuteNonQuery();
        }

        // 2. Initialize with DbInitializer -> upgrade to v8
        using (var db = CreateContext(dbPath))
        {
            DbInitializer.Initialize(db, () => backupCalled = true);
        }

        // Verify backup was called
        Assert.True(backupCalled);

        // Verify version and tables
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version;";
            long ver = Convert.ToInt64(cmd.ExecuteScalar());
            Assert.Equal(DbInitializer.CurrentSchemaVersion, ver);
            Assert.True(ver >= 8);

            Assert.True(DbInitializer.TableExists(conn, "SyncDeviceStates"));
            Assert.True(DbInitializer.TableExists(conn, "SyncLocalStates"));
            Assert.True(DbInitializer.TableExists(conn, "SyncConflicts"));
            Assert.True(DbInitializer.ColumnExists(conn, "SyncLocalStates", "PendingPayloadBytes"));

            // Verify unique index on SyncConflicts
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='IX_SyncConflicts_SyncId_RemoteRevisionId';";
            long indexCount = Convert.ToInt64(cmd.ExecuteScalar());
            Assert.Equal(1, indexCount);
        }

        // 3. Run Initialize again (Idempotency)
        using (var db = CreateContext(dbPath))
        {
            DbInitializer.Initialize(db);
        }

        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version;";
            long ver = Convert.ToInt64(cmd.ExecuteScalar());
            Assert.Equal(DbInitializer.CurrentSchemaVersion, ver);
            Assert.True(ver >= 8);
        }
    }

    [Fact]
    public void Scenario16_PartialSchemaRecovery_RecoversMissingColumnsInSyncTables()
    {
        string dbPath = CreateTempDbPath();

        using (var db = CreateContext(dbPath))
        {
            DbInitializer.Initialize(db);
        }

        // Simulate partially missing columns by dropping columns
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE SyncLocalStates DROP COLUMN PendingPackageDigest;";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "ALTER TABLE SyncLocalStates DROP COLUMN PendingPayloadBytes;";
            cmd.ExecuteNonQuery();
            Assert.False(DbInitializer.ColumnExists(conn, "SyncLocalStates", "PendingPackageDigest"));
            Assert.False(DbInitializer.ColumnExists(conn, "SyncLocalStates", "PendingPayloadBytes"));
        }

        // Run Initialize again -> EnsureColumnExists restores missing columns
        using (var db = CreateContext(dbPath))
        {
            DbInitializer.Initialize(db);
        }

        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            Assert.True(DbInitializer.ColumnExists(conn, "SyncLocalStates", "PendingPackageDigest"));
            Assert.True(DbInitializer.ColumnExists(conn, "SyncLocalStates", "PendingPayloadBytes"));
        }
    }

    [Fact]
    public async Task Scenario17_Discovery_WithLargeNumberOfPackages_FindsPointerViaDelimiterAndCommonPrefixes()
    {
        var transport = new TestCloudObjectStoreTransport();
        var settings = CreateDefaultSettings();
        string password = "DiscoveryPass!111";

        var devA = CreateDevice(transport, settings);
        var devB = CreateDevice(transport, settings);

        // Dev A creates a note and uploads it
        devA.Db.Notes.Add(new Note { Text = "Important Note from Device A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await devA.Db.SaveChangesAsync();
        var resA = await devA.SyncAsync(password);
        Assert.True(resA.Success);

        // Simulate 1050 old packages under Device A's prefix
        // In lexicographical sort, "v1/devices/{devAId}/packages/..." < "v1/devices/{devAId}/pointer.json"
        // Without Delimiter="/", 1000 items would only return packages and hide pointer.json!
        for (int i = 0; i < 1050; i++)
        {
            string dummyKey = $"v1/devices/{devA.DeviceId:D}/packages/{Guid.NewGuid():D}.json";
            transport.Store[dummyKey] = new FaultInjectingCloudObjectStoreTransport.StoredObject
            {
                Content = Encoding.UTF8.GetBytes("{}"),
                ETag = Guid.NewGuid().ToString("N")
            };
        }

        // Dev B syncs: with delimiter-based discovery, Dev A's prefix is discovered in CommonPrefixes
        // and its pointer.json is fetched directly via HeadObject/GetObject
        var resB = await devB.SyncAsync(password);
        Assert.True(resB.Success);
        Assert.Equal(1, resB.RemotePackagesPulled);

        // Dev B has the note from Dev A
        var notesOnB = await devB.Db.Notes.ToListAsync();
        Assert.Single(notesOnB);
        Assert.Equal("Important Note from Device A", notesOnB[0].Text);
    }

    [Fact]
    public async Task Scenario18_Discovery_Pagination_RespectsMaxListPagesAndSetsIsTruncated()
    {
        var transport = new TestCloudObjectStoreTransport();
        var settings = CreateDefaultSettings();
        string password = "PaginationPass!222";

        // Create 6 remote devices with pointers in transport
        for (int i = 0; i < 6; i++)
        {
            var dev = CreateDevice(transport, settings);
            dev.Db.Notes.Add(new Note { Text = $"Remote Note {i}", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await dev.Db.SaveChangesAsync();
            var res = await dev.SyncAsync(password);
            Assert.True(res.Success);
        }

        var localDev = CreateDevice(transport, settings);

        // Set transport to return at most 2 keys per page, and MaxListPages = 2 (so max 4 devices discovered out of 6)
        transport.ForceMaxKeys = 2;
        var options = new SyncCycleOptions
        {
            MaxListPages = 2
        };

        var localRes = await localDev.SyncAsync(password, options);
        Assert.True(localRes.Success);
        Assert.True(localRes.IsTruncated);
        // Only 4 devices processed (2 pages * 2 keys = 4)
        Assert.Equal(4, localRes.RemoteDevicesExamined);
        Assert.Equal(4, localRes.RemotePackagesPulled);
    }

    [Fact]
    public async Task Scenario19_Recovery_WhenRemoteObjectMissing_ReSendsPendingPayloadBytesWithoutReExport()
    {
        var transport = new TestCloudObjectStoreTransport();
        var settings = CreateDefaultSettings();
        string password = "RecoveryMissingPass!333";

        var dev = CreateDevice(transport, settings);
        dev.Db.Notes.Add(new Note { Text = "Initial Note to be in Pending Package", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await dev.Db.SaveChangesAsync();

        // Simulate crash during pointer update
        transport.ThrowOnPutConditionalPointer = true;
        var res1 = await dev.SyncAsync(password);
        Assert.False(res1.Success);

        var localState = await dev.Db.SyncLocalStates.FirstOrDefaultAsync(s => s.DeviceId == dev.DeviceId);
        Assert.NotNull(localState);
        Assert.NotNull(localState.PendingPackageId);
        Assert.NotNull(localState.PendingPayloadBytes);
        byte[] savedPendingBytes = localState.PendingPayloadBytes!;
        Guid pendingPkgId = localState.PendingPackageId!.Value;
        string packageKey = new SyncObjectKeyHelper(settings).GetPackageKey(dev.DeviceId, pendingPkgId);

        // Simulate that remote package was NOT uploaded or was deleted from object store
        await transport.DeleteObjectAsync(packageKey);
        Assert.Null(await transport.HeadObjectAsync(packageKey));

        // Add a NEW note to local DB while pending package exists.
        // If engine re-exported the DB, the package would contain both notes, altering the payload & digest!
        dev.Db.Notes.Add(new Note { Text = "Second Note Added After Crash", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await dev.Db.SaveChangesAsync();

        // Reset crash simulation and run recovery
        transport.ThrowOnPutConditionalPointer = false;
        int immutableCallsBefore = transport.PutImmutableCallsCount;

        var res2 = await dev.SyncAsync(password);
        Assert.True(res2.Success);
        Assert.Equal(pendingPkgId, res2.UploadedPackageId);

        // The exact PendingPayloadBytes were re-sent to transport
        Assert.Equal(immutableCallsBefore + 1, transport.PutImmutableCallsCount);
        var remoteObj = await transport.GetObjectAsync(packageKey);
        Assert.NotNull(remoteObj);
        Assert.Equal(savedPendingBytes, remoteObj.Content);

        // Local pending state is cleared
        var localStateAfter = await dev.Db.SyncLocalStates.FirstOrDefaultAsync(s => s.DeviceId == dev.DeviceId);
        Assert.NotNull(localStateAfter);
        Assert.Null(localStateAfter.PendingPackageId);
        Assert.Null(localStateAfter.PendingPayloadBytes);
        Assert.Equal(pendingPkgId, localStateAfter.LastUploadedPackageId);
    }

    [Fact]
    public async Task Scenario20_Recovery_WhenRemoteObjectCorrupted_AbortsAndDoesNotUpdatePointer()
    {
        var transport = new TestCloudObjectStoreTransport();
        var settings = CreateDefaultSettings();
        string password = "RecoveryCorruptPass!444";

        var dev = CreateDevice(transport, settings);
        dev.Db.Notes.Add(new Note { Text = "Note for Corrupted Remote Object Test", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await dev.Db.SaveChangesAsync();

        // Crash on pointer update
        transport.ThrowOnPutConditionalPointer = true;
        var res1 = await dev.SyncAsync(password);
        Assert.False(res1.Success);

        var localState = await dev.Db.SyncLocalStates.FirstOrDefaultAsync(s => s.DeviceId == dev.DeviceId);
        Assert.NotNull(localState);
        Guid pendingPkgId = localState.PendingPackageId!.Value;
        string packageKey = new SyncObjectKeyHelper(settings).GetPackageKey(dev.DeviceId, pendingPkgId);

        // Corrupt the package in the remote object store (flip byte to alter digest without changing size)
        var corruptBytes = (byte[])localState.PendingPayloadBytes!.Clone();
        corruptBytes[0] ^= 0xFF;
        transport.CorruptObject(packageKey, corruptBytes);

        // Reset crash injection
        transport.ThrowOnPutConditionalPointer = false;
        int conditionalPutsBefore = transport.PutConditionalCallsCount;

        // Run sync: recovery must detect digest mismatch on remote object and abort push!
        var res2 = await dev.SyncAsync(password);
        Assert.False(res2.Success);
        Assert.Contains(res2.Errors, e => e.Contains("не совпадает с PendingPackageDigest") || e.Contains("не совпадает с ожидаемым дайджестом"));

        // Pointer was NOT updated
        Assert.Equal(conditionalPutsBefore, transport.PutConditionalCallsCount);

        // Pending state is preserved for investigation
        var localStateAfter = await dev.Db.SyncLocalStates.FirstOrDefaultAsync(s => s.DeviceId == dev.DeviceId);
        Assert.NotNull(localStateAfter);
        Assert.Equal(pendingPkgId, localStateAfter.PendingPackageId);
        Assert.NotNull(localStateAfter.PendingPayloadBytes);
    }

    [Fact]
    public async Task Scenario21_Recovery_WhenPendingPayloadDigestMismatches_AbortsAndPreservesState()
    {
        var transport = new TestCloudObjectStoreTransport();
        var settings = CreateDefaultSettings();
        string password = "LocalDigestMismatchPass!555";

        var dev = CreateDevice(transport, settings);
        dev.Db.Notes.Add(new Note { Text = "Note for Local Digest Mismatch Test", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await dev.Db.SaveChangesAsync();

        transport.ThrowOnPutConditionalPointer = true;
        await dev.SyncAsync(password);

        // Tamper with PendingPackageDigest in local database
        var localState = await dev.Db.SyncLocalStates.FirstAsync(s => s.DeviceId == dev.DeviceId);
        localState.PendingPackageDigest = "TAMPERED_HASH_THAT_DOES_NOT_MATCH";
        await dev.Db.SaveChangesAsync();

        // Also delete remote object so engine has to validate local PendingPayloadBytes before upload
        string packageKey = new SyncObjectKeyHelper(settings).GetPackageKey(dev.DeviceId, localState.PendingPackageId!.Value);
        await transport.DeleteObjectAsync(packageKey);

        transport.ThrowOnPutConditionalPointer = false;
        int immutablePutsBefore = transport.PutImmutableCallsCount;

        // Run recovery
        var res2 = await dev.SyncAsync(password);
        Assert.False(res2.Success);
        Assert.Contains(res2.Errors, e => e.Contains("Дайджест PendingPayloadBytes не совпадает") || e.Contains("контрольная сумма сохранённого payload не совпадает"));

        // Did NOT upload corrupt payload or update pointer
        Assert.Equal(immutablePutsBefore, transport.PutImmutableCallsCount);

        // State remains preserved
        var localStateAfter = await dev.Db.SyncLocalStates.FirstAsync(s => s.DeviceId == dev.DeviceId);
        Assert.NotNull(localStateAfter.PendingPackageId);
        Assert.NotNull(localStateAfter.PendingPayloadBytes);
    }

    [Fact]
    public async Task Scenario22_Pull_WhenEnvelopeDeviceIdOrPackageIdMismatches_FailsForDeviceAndIsolates()
    {
        var transport = new TestCloudObjectStoreTransport();
        var settings = CreateDefaultSettings();
        string password = "EnvelopeMismatchPass!666";

        var devA = CreateDevice(transport, settings); // Malicious / spoofed envelope
        var devB = CreateDevice(transport, settings); // Local receiver
        var devC = CreateDevice(transport, settings); // Normal healthy remote device

        // Dev C creates normal note and syncs
        devC.Db.Notes.Add(new Note { Text = "Healthy Note from Dev C", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await devC.Db.SaveChangesAsync();
        var resC = await devC.SyncAsync(password);
        Assert.True(resC.Success);

        // Dev A creates normal note and syncs
        devA.Db.Notes.Add(new Note { Text = "Spoofed Note from Dev A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await devA.Db.SaveChangesAsync();
        var resA = await devA.SyncAsync(password);
        Assert.True(resA.Success);

        // Tamper with Dev A's envelope: change envelope.DeviceId to a spoofed Guid
        string packageKeyA = new SyncObjectKeyHelper(settings).GetPackageKey(devA.DeviceId, resA.UploadedPackageId!.Value);
        var storedObjA = transport.Store[packageKeyA];
        var envelopeA = JsonSerializer.Deserialize<SyncPackageEnvelope>(storedObjA.Content)!;
        envelopeA.DeviceId = Guid.NewGuid(); // Spoofed!
        storedObjA.Content = JsonSerializer.SerializeToUtf8Bytes(envelopeA);

        // Update pointerA's PackageDigest so digest check passes and envelope DeviceId mismatch check is exercised
        string pointerKeyA = new SyncObjectKeyHelper(settings).GetDevicePointerKey(devA.DeviceId);
        var pointerObjA = transport.Store[pointerKeyA];
        var pointerA = JsonSerializer.Deserialize<DevicePointerPayload>(pointerObjA.Content)!;
        pointerA.PackageDigest = SyncFingerprintHelper.ComputeSha256(storedObjA.Content);
        pointerObjA.Content = JsonSerializer.SerializeToUtf8Bytes(pointerA);

        // Dev B syncs: pulling Dev A must fail due to envelope DeviceId mismatch, but Dev C must succeed!
        var resB = await devB.SyncAsync(password);
        Assert.True(resB.Success); // Overall cycle succeeded
        Assert.Equal(1, resB.RemotePackagesPulled);
        Assert.Contains(resB.Errors, e => e.Contains("DeviceId в envelope") && e.Contains("не совпадает"));

        // Notes on B only contains note from Dev C, not spoofed note from Dev A
        var notesOnB = await devB.Db.Notes.ToListAsync();
        Assert.Single(notesOnB);
        Assert.Equal("Healthy Note from Dev C", notesOnB[0].Text);
    }

    [Fact]
    public async Task Scenario23_NormalPush_StoresPendingPayloadBytesAndClearsOnCommit()
    {
        var transport = new TestCloudObjectStoreTransport();
        var settings = CreateDefaultSettings();
        string password = "NormalPushPass!777";

        var dev = CreateDevice(transport, settings);
        dev.Db.Notes.Add(new Note { Text = "Normal Push Note", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await dev.Db.SaveChangesAsync();

        // 1. Crash before pointer update
        transport.ThrowOnPutConditionalPointer = true;
        var res1 = await dev.SyncAsync(password);
        Assert.False(res1.Success);

        // Verify PendingPayloadBytes is saved in DB
        var stateDuringPending = await dev.Db.SyncLocalStates.FirstOrDefaultAsync(s => s.DeviceId == dev.DeviceId);
        Assert.NotNull(stateDuringPending);
        Assert.NotNull(stateDuringPending.PendingPayloadBytes);
        Assert.NotEmpty(stateDuringPending.PendingPayloadBytes!);
        Assert.NotNull(stateDuringPending.PendingPackageDigest);
        Assert.Equal(SyncFingerprintHelper.ComputeSha256(stateDuringPending.PendingPayloadBytes!), stateDuringPending.PendingPackageDigest);

        // 2. Normal completion
        transport.ThrowOnPutConditionalPointer = false;
        var res2 = await dev.SyncAsync(password);
        Assert.True(res2.Success);

        // Verify PendingPayloadBytes and PendingPackageId are cleared
        var stateAfterCommit = await dev.Db.SyncLocalStates.FirstOrDefaultAsync(s => s.DeviceId == dev.DeviceId);
        Assert.NotNull(stateAfterCommit);
        Assert.Null(stateAfterCommit.PendingPayloadBytes);
        Assert.Null(stateAfterCommit.PendingPackageId);
        Assert.Null(stateAfterCommit.PendingPackageKey);
        Assert.Equal(res2.UploadedPackageId, stateAfterCommit.LastUploadedPackageId);
    }

    [Fact]
    public async Task AttachmentBlobs_AreUploadedBeforePackageAndPointer()
    {
        var transport = new TestCloudObjectStoreTransport();
        var settings = CreateDefaultSettings();
        const string password = "AttachmentOrderPass!";
        var deviceA = CreateDevice(transport, settings);

        var note = new Note { Text = "Note with file", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        deviceA.Db.Notes.Add(note);
        await deviceA.Db.SaveChangesAsync();
        AddLocalAttachment(deviceA, note.Id, Encoding.UTF8.GetBytes("unique-attachment-bytes-21f"), "report.pdf");

        var res = await deviceA.SyncAsync(password);
        Assert.True(res.Success, string.Join("; ", res.Errors));
        Assert.True(res.PackageUploaded);
        Assert.True(res.BlobsUploaded >= 1);

        int firstBlob = transport.PutImmutableKeyOrder.FindIndex(k => k.Contains("/blobs/") && k.EndsWith(".bin"));
        int firstPackage = transport.PutImmutableKeyOrder.FindIndex(k => k.Contains("/packages/") && k.EndsWith(".json"));
        Assert.True(firstBlob >= 0, "Blob object must be written.");
        Assert.True(firstPackage >= 0, "Package object must be written.");
        Assert.True(firstBlob < firstPackage, "Blob must be uploaded before the package.");
        Assert.DoesNotContain(transport.Store.Keys, k => k.Contains("report.pdf", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TwoDevices_ExchangeNoteWithAttachment_ThroughSharedFakeStore()
    {
        var transport = new TestCloudObjectStoreTransport();
        var settings = CreateDefaultSettings();
        const string password = "SharedAttachmentPassword!";
        var deviceA = CreateDevice(transport, settings);
        var deviceB = CreateDevice(transport, settings);

        byte[] content = Encoding.UTF8.GetBytes("payload-from-device-a-not-in-json");
        var note = new Note { Text = "Has attachment", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        deviceA.Db.Notes.Add(note);
        await deviceA.Db.SaveChangesAsync();
        var attA = AddLocalAttachment(deviceA, note.Id, content, "secret-name.docx");

        var resA = await deviceA.SyncAsync(password);
        Assert.True(resA.Success, string.Join("; ", resA.Errors));
        Assert.True(resA.PackageUploaded);

        foreach (var obj in transport.Store.Values)
        {
            string asText = Encoding.UTF8.GetString(obj.Content);
            Assert.DoesNotContain("payload-from-device-a-not-in-json", asText, StringComparison.Ordinal);
        }

        var resB = await deviceB.SyncAsync(password);
        Assert.True(resB.Success, string.Join("; ", resB.Errors));
        Assert.True(resB.BlobsDownloaded >= 1);

        var attB = await deviceB.Db.NoteAttachments.FirstOrDefaultAsync();
        Assert.NotNull(attB);
        Assert.Equal(attA.Sha256, attB.Sha256);
        Assert.False(string.IsNullOrEmpty(attB.StoredFileName));
        Assert.False(string.IsNullOrEmpty(attB.RelativePath));
        string pathB = deviceB.AttachmentStorage.GetFullPath(attB.RelativePath);
        Assert.True(File.Exists(pathB));
        Assert.Equal(content, File.ReadAllBytes(pathB));
        Assert.Equal(attA.Sha256, Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());
    }

    [Fact]
    public async Task InboundKdfBudget_OrdinaryPackageAndAttachment_Passes()
    {
        var transport = new TestCloudObjectStoreTransport();
        var settings = CreateDefaultSettings();
        const string password = "KdfBudgetOrdinaryPass!";
        var deviceA = CreateDevice(transport, settings);
        var deviceB = CreateDevice(transport, settings);

        var note = new Note { Text = "ordinary kdf", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        deviceA.Db.Notes.Add(note);
        await deviceA.Db.SaveChangesAsync();
        AddLocalAttachment(deviceA, note.Id, Encoding.UTF8.GetBytes("ordinary-bytes"), "a.bin");

        Assert.True((await deviceA.SyncAsync(password)).Success);

        var pull = await deviceB.SyncAsync(password);
        Assert.True(pull.Success, string.Join("; ", pull.Errors));
        Assert.False(pull.IsKdfWorkBudgetExceeded);
        Assert.Single(await deviceB.Db.Notes.ToListAsync());
        Assert.Single(await deviceB.Db.NoteAttachments.ToListAsync());
    }

    [Fact]
    public async Task InboundKdfBudget_SharedBetweenPackageAndBlob_ExactThenPlusOneFailsBeforeBlobPbkdf2()
    {
        var transport = new TestCloudObjectStoreTransport();
        var settings = CreateDefaultSettings();
        const string password = "KdfBudgetSharedPass!";
        var deviceA = CreateDevice(transport, settings);
        var deviceB = CreateDevice(transport, settings);
        int n = deviceA.Crypto.Iterations;

        var note = new Note { Text = "shared budget", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        deviceA.Db.Notes.Add(note);
        await deviceA.Db.SaveChangesAsync();
        AddLocalAttachment(deviceA, note.Id, Encoding.UTF8.GetBytes("shared-blob"), "b.bin");
        Assert.True((await deviceA.SyncAsync(password)).Success);

        KdfDiagnostics.ResetPbkdf2Invocations();
        var exact = await deviceB.SyncAsync(password, new SyncCycleOptions
        {
            PullOnly = true,
            UntrustedInboundKdfCycleBudgetIterations = n + n
        });
        Assert.True(exact.Success, string.Join("; ", exact.Errors));
        Assert.Equal(2, KdfDiagnostics.Pbkdf2Invocations);

        await ResetReceiverAsync(deviceB);

        KdfDiagnostics.ResetPbkdf2Invocations();
        var plusOne = await deviceB.SyncAsync(password, new SyncCycleOptions
        {
            PullOnly = true,
            UntrustedInboundKdfCycleBudgetIterations = n + n - 1
        });
        Assert.False(plusOne.Success);
        Assert.True(plusOne.IsKdfWorkBudgetExceeded);
        Assert.False(plusOne.IsOffline);
        Assert.False(plusOne.IsAuthError);
        Assert.False(plusOne.IsCancelled);
        Assert.Equal(UntrustedInboundKdfWorkBudget.UserFacingMessage, plusOne.ErrorMessage);
        Assert.Equal(1, KdfDiagnostics.Pbkdf2Invocations);
        Assert.Empty(await deviceB.Db.Notes.ToListAsync());
        Assert.Empty(await deviceB.Db.NoteAttachments.ToListAsync());
        Assert.Empty(await deviceB.Db.SyncDeviceStates.ToListAsync());
        Assert.Empty(Directory.GetFiles(deviceB.AttachmentStorage.AttachmentsDirectory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task InboundKdfBudget_MaliciousManyBlobsAndSingleHostileMax_FailBeforePbkdf2WithoutMutation()
    {
        var transport = new TestCloudObjectStoreTransport();
        var settings = CreateDefaultSettings();
        const string password = "KdfBudgetHostilePass!";
        var deviceA = CreateDevice(transport, settings);
        var deviceB = CreateDevice(transport, settings);
        int n = deviceA.Crypto.Iterations;

        var note = new Note { Text = "many blobs", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        deviceA.Db.Notes.Add(note);
        await deviceA.Db.SaveChangesAsync();
        for (int i = 0; i < 8; i++)
        {
            AddLocalAttachment(deviceA, note.Id, Encoding.UTF8.GetBytes("blob-" + i), $"f{i}.bin");
        }

        Assert.True((await deviceA.SyncAsync(password)).Success);

        KdfDiagnostics.ResetPbkdf2Invocations();
        var many = await deviceB.SyncAsync(password, new SyncCycleOptions
        {
            PullOnly = true,
            UntrustedInboundKdfCycleBudgetIterations = n + (2 * n)
        });
        Assert.False(many.Success);
        Assert.True(many.IsKdfWorkBudgetExceeded);
        Assert.Equal(1, KdfDiagnostics.Pbkdf2Invocations);
        Assert.Empty(await deviceB.Db.Notes.ToListAsync());
        Assert.Empty(Directory.GetFiles(deviceB.AttachmentStorage.AttachmentsDirectory, "*", SearchOption.AllDirectories));

        TamperRemotePackageDescriptor(transport, settings, deviceA.DeviceId, KdfDescriptorConstants.MaxPbkdf2Iterations);
        await ResetReceiverAsync(deviceB);
        KdfDiagnostics.ResetPbkdf2Invocations();
        var hostileMax = await deviceB.SyncAsync(password, new SyncCycleOptions
        {
            PullOnly = true,
            UntrustedInboundKdfCycleBudgetIterations = KdfDescriptorConstants.MaxPbkdf2Iterations - 1
        });
        Assert.False(hostileMax.Success);
        Assert.True(hostileMax.IsKdfWorkBudgetExceeded);
        Assert.Equal(0, KdfDiagnostics.Pbkdf2Invocations);
        Assert.Empty(await deviceB.Db.Notes.ToListAsync());
        Assert.DoesNotContain("password", hostileMax.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/packages/", hostileMax.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InboundKdfBudget_LocalPush_IsNotCharged_AndCancellationStillThrows()
    {
        var transport = new TestCloudObjectStoreTransport();
        var settings = CreateDefaultSettings();
        const string password = "KdfBudgetLocalPushPass!";
        var device = CreateDevice(transport, settings);
        device.Db.Notes.Add(new Note { Text = "local only", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await device.Db.SaveChangesAsync();

        var push = await device.SyncAsync(password, new SyncCycleOptions
        {
            UntrustedInboundKdfCycleBudgetIterations = 1
        });
        Assert.True(push.Success, string.Join("; ", push.Errors));
        Assert.True(push.PackageUploaded);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            device.SyncAsync(password, ct: cts.Token));
    }

    private static async Task ResetReceiverAsync(TestDeviceNode device)
    {
        device.Db.NoteAttachments.RemoveRange(device.Db.NoteAttachments);
        device.Db.Notes.RemoveRange(device.Db.Notes);
        device.Db.SyncEntityStates.RemoveRange(device.Db.SyncEntityStates);
        device.Db.SyncDeviceStates.RemoveRange(device.Db.SyncDeviceStates);
        await device.Db.SaveChangesAsync();
        if (Directory.Exists(device.AttachmentStorage.AttachmentsDirectory))
        {
            foreach (string file in Directory.GetFiles(device.AttachmentStorage.AttachmentsDirectory, "*", SearchOption.AllDirectories))
            {
                File.Delete(file);
            }
        }
    }

    private static void TamperRemotePackageDescriptor(
        TestCloudObjectStoreTransport transport,
        SyncCloudSettings settings,
        Guid sourceDeviceId,
        int claimedIterations)
    {
        var keys = new SyncObjectKeyHelper(settings);
        string pointerKey = keys.GetDevicePointerKey(sourceDeviceId);
        var pointer = JsonSerializer.Deserialize<DevicePointerPayload>(transport.Store[pointerKey].Content)!;
        string packageKey = keys.GetPackageKey(sourceDeviceId, pointer.LatestPackageId);
        var envelope = JsonSerializer.Deserialize<SyncPackageEnvelope>(transport.Store[packageKey].Content)!;
        envelope.Crypto.KdfDescriptor = KdfDescriptor.Pbkdf2(claimedIterations).ToCanonicalText();
        byte[] rewritten = JsonSerializer.SerializeToUtf8Bytes(envelope);
        transport.Store[packageKey].Content = rewritten;
        pointer.PackageDigest = SyncFingerprintHelper.ComputeSha256(rewritten);
        transport.Store[pointerKey].Content = JsonSerializer.SerializeToUtf8Bytes(pointer);
    }

    [Fact]
    public async Task BlobUploadFailure_DoesNotPublishPackage()
    {
        var transport = new TestCloudObjectStoreTransport();
        var settings = CreateDefaultSettings();
        const string password = "FailBlobThenRetry!";
        var deviceA = CreateDevice(transport, settings);

        var note = new Note { Text = "Will fail blob", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        deviceA.Db.Notes.Add(note);
        await deviceA.Db.SaveChangesAsync();
        var att = AddLocalAttachment(deviceA, note.Id, Encoding.UTF8.GetBytes("need-cloud"), "a.bin");
        File.Delete(Path.Combine(deviceA.AttachmentStorage.AttachmentsDirectory, att.StoredFileName));

        var res1 = await deviceA.SyncAsync(password);
        Assert.False(res1.Success);
        Assert.True(res1.BlobErrors >= 1);
        Assert.DoesNotContain(transport.Store.Keys, k => k.Contains("/packages/"));

        File.WriteAllBytes(Path.Combine(deviceA.AttachmentStorage.AttachmentsDirectory, att.StoredFileName), Encoding.UTF8.GetBytes("need-cloud"));
        var res2 = await deviceA.SyncAsync(password);
        Assert.True(res2.Success, string.Join("; ", res2.Errors));
        Assert.Contains(transport.Store.Keys, k => k.Contains("/packages/"));
        Assert.Contains(transport.Store.Keys, k => k.Contains("/blobs/"));
    }

    [Fact]
    public async Task UnusableExistingBlob_DoesNotPublishPackageOrOverwriteBlob()
    {
        var transport = new TestCloudObjectStoreTransport();
        var settings = CreateDefaultSettings();
        const string password = "UnusableExistingBlobPass!";
        var deviceA = CreateDevice(transport, settings);

        byte[] content = Encoding.UTF8.GetBytes("local-bytes-must-not-overwrite-corrupt-cloud");
        var note = new Note { Text = "Blocked by corrupt blob", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        deviceA.Db.Notes.Add(note);
        await deviceA.Db.SaveChangesAsync();
        var att = AddLocalAttachment(deviceA, note.Id, content, "blocked.bin");

        string blobKey = deviceA.KeyHelper.GetBlobKey(att.Sha256);
        byte[] corrupt = Encoding.UTF8.GetBytes("not-a-qnba-envelope");
        transport.Store[blobKey] = new FaultInjectingCloudObjectStoreTransport.StoredObject
        {
            Content = corrupt,
            ETag = "corrupt-etag",
            ContentType = "application/octet-stream"
        };

        var res = await deviceA.SyncAsync(password);
        Assert.False(res.Success);
        Assert.True(res.BlobErrors >= 1);
        Assert.DoesNotContain(transport.Store.Keys, k => k.Contains("/packages/"));
        Assert.Equal(corrupt, transport.Store[blobKey].Content);
    }

    private class MutableUsageService : ICloudUsageService
    {
        public CloudUsageResult Result { get; set; } = CloudUsageResult.Success(0, 0, 1, false, "sync-root/v1/", "notes-sync-bucket");
        public Action? OnCalculate { get; set; }
        public CloudUsageResult? CachedUsage => Result;
        public bool IsCalculating => false;
        public Task<CloudUsageResult> CalculateUsageAsync(bool forceRefresh = false, CancellationToken ct = default)
        {
            OnCalculate?.Invoke();
            return Task.FromResult(Result);
        }
        public void InvalidateCache() { }
        public void Dispose() { }
    }

    [Fact]
    public async Task QuotaAt950_NewBlob_DoesNotPublishPackage()
    {
        var transport = new TestCloudObjectStoreTransport();
        var settings = CreateDefaultSettings();
        var usage = new MutableUsageService
        {
            Result = CloudUsageResult.Success(
                CloudQuotaPolicy.BlockNewAttachmentThresholdBytes, 8, 1, false, "sync-root/v1/", settings.Bucket)
        };
        const string password = "QuotaBlockPass!";
        var deviceA = CreateDevice(transport, settings, usageService: usage);

        var note = new Note { Text = "new file over quota", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        deviceA.Db.Notes.Add(note);
        await deviceA.Db.SaveChangesAsync();
        AddLocalAttachment(deviceA, note.Id, Encoding.UTF8.GetBytes("brand-new-bytes"), "new.bin");

        var res = await deviceA.SyncAsync(password);
        Assert.False(res.Success);
        Assert.True(res.BlobErrors >= 1);
        Assert.DoesNotContain(transport.Store.Keys, k => k.Contains("/packages/"));
        Assert.DoesNotContain(transport.Store.Keys, k => k.Contains("/blobs/"));
    }

    [Fact]
    public async Task QuotaAt950_ExistingVerifiedBlob_StillPublishesPackage()
    {
        var transport = new TestCloudObjectStoreTransport();
        var settings = CreateDefaultSettings();
        var usage = new MutableUsageService
        {
            Result = CloudUsageResult.Success(1024, 1, 1, false, "sync-root/v1/", settings.Bucket)
        };
        const string password = "QuotaDedupPass!";
        var deviceA = CreateDevice(transport, settings, usageService: usage);

        var note = new Note { Text = "has file", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        deviceA.Db.Notes.Add(note);
        await deviceA.Db.SaveChangesAsync();
        AddLocalAttachment(deviceA, note.Id, Encoding.UTF8.GetBytes("already-cloud"), "keep.bin");

        var first = await deviceA.SyncAsync(password);
        Assert.True(first.Success, string.Join("; ", first.Errors));
        Assert.Contains(transport.Store.Keys, k => k.Contains("/blobs/"));

        usage.Result = CloudUsageResult.Success(
            CloudQuotaPolicy.BlockNewAttachmentThresholdBytes, 20, 1, false, "sync-root/v1/", settings.Bucket);
        deviceA.Db.Notes.Add(new Note { Text = "metadata-only after quota", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await deviceA.Db.SaveChangesAsync();

        var second = await deviceA.SyncAsync(password);
        Assert.True(second.Success, string.Join("; ", second.Errors));
        Assert.True(second.PackageUploaded);
    }

    [Fact]
    public async Task PackagePutFailureAfterBlobs_RetryPublishesSamePendingWithoutDuplicateBlob()
    {
        var transport = new TestCloudObjectStoreTransport();
        var settings = CreateDefaultSettings();
        const string password = "PkgPutRetryPass!";
        var deviceA = CreateDevice(transport, settings);

        var note = new Note { Text = "blob then package fail", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        deviceA.Db.Notes.Add(note);
        await deviceA.Db.SaveChangesAsync();
        AddLocalAttachment(deviceA, note.Id, Encoding.UTF8.GetBytes("retry-pkg-bytes"), "r.bin");

        transport.ThrowOnPutImmutableKeyContains = "/packages/";
        var res1 = await deviceA.SyncAsync(password);
        Assert.False(res1.Success);
        Assert.Contains(transport.Store.Keys, k => k.Contains("/blobs/") && k.EndsWith(".bin"));
        Assert.DoesNotContain(transport.Store.Keys, k => k.Contains("/packages/"));
        int blobPuts = transport.PutImmutableKeyOrder.Count(k => k.Contains("/blobs/"));

        var pending = await deviceA.Db.SyncLocalStates.FirstAsync();
        Assert.True(pending.PendingPackageId.HasValue);

        transport.ThrowOnPutImmutableKeyContains = null;
        var res2 = await deviceA.SyncAsync(password);
        Assert.True(res2.Success, string.Join("; ", res2.Errors));
        Assert.True(res2.PackageUploaded);
        Assert.Contains(transport.Store.Keys, k => k.Contains("/packages/"));
        Assert.Equal(blobPuts, transport.PutImmutableKeyOrder.Count(k => k.Contains("/blobs/")));
        Assert.Single(transport.Store.Keys.Where(k => k.Contains("/packages/")));
    }

    [Fact]
    public async Task QuotaAt950_MidCycleAfterFirstBlob_DoesNotPublishPackage()
    {
        var transport = new TestCloudObjectStoreTransport();
        var settings = CreateDefaultSettings();
        var usage = new MutableUsageService { Result = CloudUsageResult.Success(1024, 1, 1, false, "sync-root/v1/", settings.Bucket) };
        int calls = 0;
        usage.OnCalculate = () =>
        {
            calls++;
            if (calls >= 2)
            {
                usage.Result = CloudUsageResult.Success(
                    CloudQuotaPolicy.BlockNewAttachmentThresholdBytes, 9, 1, false, "sync-root/v1/", settings.Bucket);
            }
        };

        const string password = "QuotaMidCyclePass!";
        var deviceA = CreateDevice(transport, settings, usageService: usage);
        var note = new Note { Text = "two files", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        deviceA.Db.Notes.Add(note);
        await deviceA.Db.SaveChangesAsync();
        AddLocalAttachment(deviceA, note.Id, Encoding.UTF8.GetBytes("first-blob-ok"), "a.bin");
        AddLocalAttachment(deviceA, note.Id, Encoding.UTF8.GetBytes("second-blob-blocked"), "b.bin");

        var res = await deviceA.SyncAsync(password);
        Assert.False(res.Success);
        Assert.True(res.BlobErrors >= 1);
        Assert.DoesNotContain(transport.Store.Keys, k => k.Contains("/packages/"));
        Assert.Equal(1, transport.Store.Keys.Count(k => k.Contains("/blobs/") && k.EndsWith(".bin")));
    }

    [Fact]
    public async Task MissingRemoteBlob_DoesNotAdvancePullCheckpoint_AndRetriesAfterRestore()
    {
        var transport = new TestCloudObjectStoreTransport();
        var settings = CreateDefaultSettings();
        const string password = "MissingBlobCheckpoint!";
        var deviceA = CreateDevice(transport, settings);
        var deviceB = CreateDevice(transport, settings);

        var note = new Note { Text = "with file", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        deviceA.Db.Notes.Add(note);
        await deviceA.Db.SaveChangesAsync();
        var att = AddLocalAttachment(deviceA, note.Id, Encoding.UTF8.GetBytes("cloud-bytes"), "c.bin");
        var resA = await deviceA.SyncAsync(password);
        Assert.True(resA.Success, string.Join("; ", resA.Errors));

        string blobKey = deviceA.KeyHelper.GetBlobKey(att.Sha256);
        Assert.True(transport.Store.TryRemove(blobKey, out var savedBlob));

        var resB1 = await deviceB.SyncAsync(password);
        Assert.True(resB1.BlobErrors >= 1);
        Assert.Equal(0, resB1.RemotePackagesPulled);
        Assert.Empty(await deviceB.Db.SyncDeviceStates.ToListAsync());
        Assert.True(await deviceB.Db.Notes.AnyAsync());

        transport.Store[blobKey] = savedBlob!;
        var resB2 = await deviceB.SyncAsync(password);
        Assert.True(resB2.Success, string.Join("; ", resB2.Errors));
        Assert.True(resB2.BlobsDownloaded >= 1);
        Assert.Equal(1, resB2.RemotePackagesPulled);
        Assert.NotEmpty(await deviceB.Db.SyncDeviceStates.ToListAsync());
    }

    [Fact]
    public async Task WrongPassword_DoesNotAdvanceCheckpoint_OrApplyRemoteNotes()
    {
        var transport = new TestCloudObjectStoreTransport();
        var settings = CreateDefaultSettings();
        var deviceA = CreateDevice(transport, settings);
        var deviceB = CreateDevice(transport, settings);
        deviceA.Db.Notes.Add(new Note { Text = "secret-note", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await deviceA.Db.SaveChangesAsync();
        Assert.True((await deviceA.SyncAsync("CorrectPassword21H!")).Success);

        var resB = await deviceB.SyncAsync("WrongPassword21H!");
        Assert.NotEmpty(resB.Errors);
        Assert.Empty(await deviceB.Db.Notes.ToListAsync());
        Assert.Empty(await deviceB.Db.SyncDeviceStates.ToListAsync());
    }

    [Fact]
    public async Task TwoDevices_TombstoneVsLocalEdit_CreatesConflict_DoesNotOverwriteLocal()
    {
        var transport = new TestCloudObjectStoreTransport();
        var settings = CreateDefaultSettings();
        const string password = "TombstoneVsEdit!";
        var devA = CreateDevice(transport, settings);
        var devB = CreateDevice(transport, settings);

        var note = new Note { Text = "shared", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        devA.Db.Notes.Add(note);
        await devA.Db.SaveChangesAsync();
        await devA.SyncAsync(password);
        await devB.SyncAsync(password);
        var noteOnB = await devB.Db.Notes.FirstAsync();

        note.DeletedAt = DateTime.UtcNow;
        note.UpdatedAt = DateTime.UtcNow;
        await devA.Db.SaveChangesAsync();
        await devA.SyncAsync(password);

        noteOnB.Text = "edited while A deleted";
        noteOnB.UpdatedAt = DateTime.UtcNow;
        await devB.Db.SaveChangesAsync();
        var resB = await devB.SyncAsync(password);
        Assert.True(resB.Success, string.Join("; ", resB.Errors));
        Assert.True(resB.ConflictsDetected >= 1);
        var local = await devB.Db.Notes.FirstAsync();
        Assert.Equal("edited while A deleted", local.Text);
        Assert.Null(local.DeletedAt);
    }

    [Fact]
    public async Task TwoDevices_IdenticalAttachmentBytes_DedupeSingleBlobKey()
    {
        var transport = new TestCloudObjectStoreTransport();
        var settings = CreateDefaultSettings();
        const string password = "SameBytesDedupe!";
        var devA = CreateDevice(transport, settings);
        var devB = CreateDevice(transport, settings);
        byte[] same = Encoding.UTF8.GetBytes("identical-payload-21h");

        var noteA = new Note { Text = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        devA.Db.Notes.Add(noteA);
        await devA.Db.SaveChangesAsync();
        AddLocalAttachment(devA, noteA.Id, same, "a.bin");
        Assert.True((await devA.SyncAsync(password)).Success);

        var noteB = new Note { Text = "B", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        devB.Db.Notes.Add(noteB);
        await devB.Db.SaveChangesAsync();
        AddLocalAttachment(devB, noteB.Id, same, "b.bin");
        var resB = await devB.SyncAsync(password);
        Assert.True(resB.Success, string.Join("; ", resB.Errors));
        Assert.Equal(1, transport.Store.Keys.Count(k => k.Contains("/blobs/") && k.EndsWith(".bin")));
    }

    [Fact]
    public async Task TwoDevices_AttachmentMetadataConflict_KeepsLocalAndRecordsConflict()
    {
        var transport = new TestCloudObjectStoreTransport();
        var settings = CreateDefaultSettings();
        const string password = "AttMetaConflict!";
        var devA = CreateDevice(transport, settings);
        var devB = CreateDevice(transport, settings);

        var note = new Note { Text = "n", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        devA.Db.Notes.Add(note);
        await devA.Db.SaveChangesAsync();
        var attA = AddLocalAttachment(devA, note.Id, Encoding.UTF8.GetBytes("meta"), "v1.bin");
        await devA.SyncAsync(password);
        await devB.SyncAsync(password);

        attA.OriginalFileName = "renamed-by-a.bin";
        await devA.Db.SaveChangesAsync();
        await devA.SyncAsync(password);

        var attB = await devB.Db.NoteAttachments.FirstAsync();
        attB.ContentType = "application/x-edited";
        await devB.Db.SaveChangesAsync();
        var resB = await devB.SyncAsync(password);
        Assert.True(resB.Success, string.Join("; ", resB.Errors));
        Assert.True(resB.ConflictsDetected >= 1);
        var conflicts = await devB.Db.SyncConflicts.ToListAsync();
        Assert.Contains(conflicts, c => c.EntityType == "NoteAttachment");
        var localAtt = await devB.Db.NoteAttachments.FirstAsync();
        Assert.Equal("application/x-edited", localAtt.ContentType);
    }

    [Fact]
    public async Task TwoDevices_OfflineEditThenReconnect_PushesWithoutDataLoss()
    {
        var transport = new TestCloudObjectStoreTransport();
        var settings = CreateDefaultSettings();
        const string password = "OfflineReconnect!";
        var devA = CreateDevice(transport, settings);
        var devB = CreateDevice(transport, settings);
        devA.Db.Notes.Add(new Note { Text = "seed", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await devA.Db.SaveChangesAsync();
        await devA.SyncAsync(password);
        await devB.SyncAsync(password);

        var noteOnB = await devB.Db.Notes.FirstAsync();
        noteOnB.Text = "edited-offline";
        noteOnB.UpdatedAt = DateTime.UtcNow;
        await devB.Db.SaveChangesAsync();
        transport.IsOffline = true;
        var offline = await devB.SyncAsync(password);
        Assert.True(offline.IsOffline);
        Assert.Equal("edited-offline", (await devB.Db.Notes.FirstAsync()).Text);

        transport.IsOffline = false;
        var online = await devB.SyncAsync(password);
        Assert.True(online.Success, string.Join("; ", online.Errors));
        Assert.True(online.PackageUploaded);

        var resA = await devA.SyncAsync(password);
        Assert.True(resA.Success, string.Join("; ", resA.Errors));
        Assert.Equal("edited-offline", (await devA.Db.Notes.FirstAsync()).Text);
    }

    private static NoteAttachment AddLocalAttachment(TestDeviceNode device, int noteId, byte[] content, string originalName)
    {
        string source = Path.Combine(device.AttachmentsRoot, "src-" + Guid.NewGuid().ToString("N") + Path.GetExtension(originalName));
        File.WriteAllBytes(source, content);
        var saved = device.AttachmentStorage.SaveAttachment(source, 10 * 1024 * 1024);
        var att = new NoteAttachment
        {
            NoteId = noteId,
            OriginalFileName = originalName,
            StoredFileName = saved.StoredFileName,
            RelativePath = saved.RelativePath,
            ContentType = saved.ContentType,
            Size = saved.Size,
            Sha256 = saved.Sha256,
            CreatedAt = DateTime.UtcNow
        };
        device.Db.NoteAttachments.Add(att);
        device.Db.SaveChanges();
        return att;
    }

    private class TrackingDbContext : QuickNotesDbContext
    {
        public bool IsDisposed { get; private set; }

        public TrackingDbContext(string dbPath) : base(dbPath) { }

        public override void Dispose()
        {
            IsDisposed = true;
            base.Dispose();
        }

        public override async ValueTask DisposeAsync()
        {
            IsDisposed = true;
            await base.DisposeAsync();
        }
    }

    [Fact]
    public async Task Scenario24_RunSyncCycleAsync_OverloadWithDbFactory_AwaitsInsideUsingAndKeepsContextAlive()
    {
        var transport = new TestCloudObjectStoreTransport();
        var settings = CreateDefaultSettings();
        string dbPath = CreateTempDbPath();

        using (var initDb = new QuickNotesDbContext(dbPath))
        {
            initDb.Database.EnsureCreated();
        }

        TrackingDbContext? capturedContext = null;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        transport.BeforeListObjectsAsync = async () =>
        {
            await tcs.Task;
        };

        var deviceId = Guid.NewGuid();
        var crypto = new SyncCryptoService();
        var deviceIdProvider = new FixedDeviceIdProvider(deviceId);
        var exporter = new SyncPackageExporter(crypto, deviceIdProvider);
        var importer = new SyncPackageImporter(crypto);
        var creds = new InMemoryCredentialsStorage(new S3Credentials("key-123", "secret-456"));

        var engine = new SyncEngine(
            transport,
            exporter,
            importer,
            deviceIdProvider,
            settings,
            creds,
            dbFactory: () =>
            {
                capturedContext = new TrackingDbContext(dbPath);
                return capturedContext;
            });

        var syncTask = engine.RunSyncCycleAsync("Password123!");

        // Wait a short moment for the background sync task to reach the blocked transport call
        await Task.Delay(150);

        Assert.False(syncTask.IsCompleted, "Sync task must still be running while transport is blocked.");
        Assert.NotNull(capturedContext);
        Assert.False(capturedContext!.IsDisposed, "DbContext must NOT be disposed while async operation is executing inside using.");

        // Unblock the transport
        tcs.SetResult(true);

        var result = await syncTask;

        Assert.True(result.Success);
        Assert.True(capturedContext.IsDisposed, "DbContext must be properly disposed after operation completes.");
    }

    #endregion
}
