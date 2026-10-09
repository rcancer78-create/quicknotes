using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.Services;
using QuickNotes.App.Services.Sync;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public sealed class SyncFailureMatrixTests : IDisposable
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

    [Fact]
    public async Task KeepLocal_AfterEngineConflict_PushesChosenLocal_NoSilentLossOrPlaintext()
    {
        var transport = new FaultInjectingCloudObjectStoreTransport();
        var settings = DefaultSettings();
        const string password = "MatrixKeepLocal-21H!";
        const string localText = "keep-local-chosen-on-B";
        const string remoteText = "keep-local-rejected-from-A";

        var a = CreateDevice(transport, settings);
        var b = CreateDevice(transport, settings);
        await SeedDivergentNoteConflictAsync(a, b, password, remoteText, localText);

        var conflicts = CreateConflicts(b);
        var pending = await conflicts.GetUnresolvedConflictsAsync();
        Assert.Single(pending);
        var resolved = await conflicts.ResolveKeepLocalAsync(pending[0].Id);
        Assert.True(resolved.Success, resolved.ErrorMessage);
        Assert.Equal("KeepLocal", resolved.Action);

        b.Db.ChangeTracker.Clear();
        Assert.Equal(localText, (await b.Db.Notes.SingleAsync()).Text);
        Assert.True((await b.Db.SyncConflicts.SingleAsync()).IsResolved);

        var pushB = await b.SyncAsync(password);
        Assert.True(pushB.Success, string.Join("; ", pushB.Errors));
        Assert.True(pushB.PackageUploaded);

        a.Db.ChangeTracker.Clear();
        var pullA = await a.SyncAsync(password);
        Assert.True(pullA.Success, string.Join("; ", pullA.Errors));
        a.Db.ChangeTracker.Clear();
        Assert.Equal(localText, (await a.Db.Notes.SingleAsync()).Text);
        Assert.Single(await a.Db.Notes.ToListAsync());

        AssertStorePrivacy(transport, password, localText, remoteText);
    }

    [Fact]
    public async Task KeepRemote_AfterEngineConflict_AppliesRemote_ThenARemainsChosenVersion()
    {
        var transport = new FaultInjectingCloudObjectStoreTransport();
        var settings = DefaultSettings();
        const string password = "MatrixKeepRemote-21H!";
        const string localText = "keep-remote-discarded-on-B";
        const string remoteText = "keep-remote-chosen-from-A";

        var a = CreateDevice(transport, settings);
        var b = CreateDevice(transport, settings);
        await SeedDivergentNoteConflictAsync(a, b, password, remoteText, localText);

        var conflicts = CreateConflicts(b);
        var pending = await conflicts.GetUnresolvedConflictsAsync();
        Assert.Single(pending);
        var resolved = await conflicts.ResolveAcceptRemoteAsync(pending[0].Id);
        Assert.True(resolved.Success, resolved.ErrorMessage);
        Assert.Equal("AcceptRemote", resolved.Action);

        b.Db.ChangeTracker.Clear();
        Assert.Equal(remoteText, (await b.Db.Notes.SingleAsync()).Text);
        Assert.True((await b.Db.SyncConflicts.SingleAsync()).IsResolved);

        var pushB = await b.SyncAsync(password);
        Assert.True(pushB.Success, string.Join("; ", pushB.Errors));

        a.Db.ChangeTracker.Clear();
        var pullA = await a.SyncAsync(password);
        Assert.True(pullA.Success, string.Join("; ", pullA.Errors));
        a.Db.ChangeTracker.Clear();
        Assert.Equal(remoteText, (await a.Db.Notes.SingleAsync()).Text);
        Assert.Single(await a.Db.Notes.ToListAsync());

        AssertStorePrivacy(transport, password, localText, remoteText);
    }

    [Fact]
    public async Task AuthError_IsNotClassifiedAsOffline_AndLeavesLocalNote()
    {
        var transport = new FaultInjectingCloudObjectStoreTransport { IsAuthError = true };
        var device = CreateDevice(transport, DefaultSettings());
        const string password = "MatrixAuth-21H!";
        device.Db.Notes.Add(new Note { Text = "auth-local-kept", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await device.Db.SaveChangesAsync();

        var res = await device.SyncAsync(password);
        Assert.False(res.Success);
        Assert.True(res.IsAuthError);
        Assert.False(res.IsOffline);
        Assert.False(res.IsQuotaExceeded);
        Assert.False(res.IsEtagConflict);
        Assert.Equal("auth-local-kept", (await device.Db.Notes.SingleAsync()).Text);
    }

    [Fact]
    public async Task QuotaAndFullStore_AreNotClassifiedAsOffline_AndDoNotDropLocal()
    {
        var transport = new FaultInjectingCloudObjectStoreTransport { ThrowQuotaOnPut = true };
        var device = CreateDevice(transport, DefaultSettings());
        const string password = "MatrixQuota-21H!";
        device.Db.Notes.Add(new Note { Text = "quota-local-kept", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await device.Db.SaveChangesAsync();

        var quota = await device.SyncAsync(password);
        Assert.False(quota.Success);
        Assert.True(quota.IsQuotaExceeded);
        Assert.False(quota.IsOffline);
        Assert.False(quota.IsAuthError);
        Assert.Equal("quota-local-kept", (await device.Db.Notes.SingleAsync()).Text);
        Assert.Empty(transport.Store);

        transport.ThrowQuotaOnPut = false;
        transport.MaxStoreBytes = 8;
        var full = await device.SyncAsync(password);
        Assert.False(full.Success);
        Assert.True(full.IsQuotaExceeded);
        Assert.False(full.IsOffline);
        Assert.Equal("quota-local-kept", (await device.Db.Notes.SingleAsync()).Text);
        Assert.False(transport.ContainsUtf8(password));
        Assert.False(transport.ContainsUtf8("quota-local-kept"));
    }

    [Fact]
    public async Task EtagRace_IsNotOffline_PreservesPendingAndLocal()
    {
        var transport = new FaultInjectingCloudObjectStoreTransport { SimulatePointerConflictOnPut = true };
        var device = CreateDevice(transport, DefaultSettings());
        const string password = "MatrixEtag-21H!";
        device.Db.Notes.Add(new Note { Text = "etag-local-kept", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await device.Db.SaveChangesAsync();

        var res = await device.SyncAsync(password);
        Assert.False(res.Success);
        Assert.True(res.IsEtagConflict);
        Assert.False(res.IsOffline);
        Assert.False(res.IsQuotaExceeded);
        Assert.Equal("etag-local-kept", (await device.Db.Notes.SingleAsync()).Text);

        var localState = await device.Db.SyncLocalStates.FirstAsync(s => s.DeviceId == device.DeviceId);
        Assert.NotNull(localState.PendingPackageId);
    }

    [Fact]
    public async Task CorruptedRemoteGet_IsNotOffline_DoesNotApplyOrLeak()
    {
        var transport = new FaultInjectingCloudObjectStoreTransport();
        var settings = DefaultSettings();
        const string password = "MatrixCorrupt-21H!";
        var a = CreateDevice(transport, settings);
        var b = CreateDevice(transport, settings);
        a.Db.Notes.Add(new Note { Text = "corrupt-source-note", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await a.Db.SaveChangesAsync();
        Assert.True((await a.SyncAsync(password)).Success);

        transport.ThrowCorruptionOnGet = true;
        var pull = await b.SyncAsync(password);
        Assert.False(pull.Success);
        Assert.False(pull.IsOffline);
        Assert.False(pull.IsAuthError);
        Assert.Equal(0, pull.RemotePackagesPulled);
        Assert.Empty(await b.Db.Notes.ToListAsync());
        Assert.False(transport.ContainsUtf8(password));
        Assert.False(transport.ContainsUtf8("corrupt-source-note"));
    }

    [Fact]
    public async Task Recovery_DoesNotOverwriteNewerLocal_FollowUpPushSendsIt()
    {
        var transport = new FaultInjectingCloudObjectStoreTransport();
        var settings = DefaultSettings();
        const string password = "MatrixRecovery-21H!";
        var a = CreateDevice(transport, settings);
        var b = CreateDevice(transport, settings);

        a.Db.Notes.Add(new Note { Text = "pending-first-note", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await a.Db.SaveChangesAsync();
        transport.ThrowOnPutConditionalPointer = true;
        Assert.False((await a.SyncAsync(password)).Success);

        a.Db.Notes.Add(new Note { Text = "newer-local-after-crash", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await a.Db.SaveChangesAsync();

        transport.ThrowOnPutConditionalPointer = false;
        var recovered = await a.SyncAsync(password);
        Assert.True(recovered.Success, string.Join("; ", recovered.Errors));

        a.Db.ChangeTracker.Clear();
        var texts = (await a.Db.Notes.Select(n => n.Text).ToListAsync()).OrderBy(t => t).ToList();
        Assert.Equal(new[] { "newer-local-after-crash", "pending-first-note" }, texts);

        var follow = await a.SyncAsync(password);
        Assert.True(follow.Success, string.Join("; ", follow.Errors));
        Assert.True(follow.PackageUploaded);

        var pullB = await b.SyncAsync(password);
        Assert.True(pullB.Success, string.Join("; ", pullB.Errors));
        b.Db.ChangeTracker.Clear();
        var onB = (await b.Db.Notes.Select(n => n.Text).ToListAsync()).OrderBy(t => t).ToList();
        Assert.Equal(new[] { "newer-local-after-crash", "pending-first-note" }, onB);
        Assert.False(transport.ContainsUtf8(password));
    }

    private static SyncCloudSettings DefaultSettings() => new()
    {
        Enabled = true,
        Endpoint = "https://storage.yandexcloud.net",
        Region = "ru-central1",
        Bucket = "notes-sync-bucket",
        Prefix = "sync-root"
    };

    private static SyncConflictService CreateConflicts(TestDevice device)
        => new(() => SqliteTestUtil.CreateContext(device.DbPath), device.DeviceIdProvider, new NoteHistoryService());

    private static async Task SeedDivergentNoteConflictAsync(
        TestDevice a,
        TestDevice b,
        string password,
        string textOnA,
        string textOnB)
    {
        var note = new Note { Text = "shared-seed", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        a.Db.Notes.Add(note);
        await a.Db.SaveChangesAsync();
        Assert.True((await a.SyncAsync(password)).Success);
        Assert.True((await b.SyncAsync(password)).Success);

        note.Text = textOnA;
        note.UpdatedAt = DateTime.UtcNow;
        await a.Db.SaveChangesAsync();
        Assert.True((await a.SyncAsync(password)).Success);

        var noteOnB = await b.Db.Notes.SingleAsync();
        noteOnB.Text = textOnB;
        noteOnB.UpdatedAt = DateTime.UtcNow;
        await b.Db.SaveChangesAsync();
        var conflictCycle = await b.SyncAsync(password);
        Assert.True(conflictCycle.Success, string.Join("; ", conflictCycle.Errors));
        Assert.Equal(1, conflictCycle.ConflictsDetected);
        Assert.Equal(textOnB, (await b.Db.Notes.SingleAsync()).Text);
    }

    private static void AssertStorePrivacy(FaultInjectingCloudObjectStoreTransport transport, params string[] secrets)
    {
        foreach (string secret in secrets)
        {
            Assert.False(transport.ContainsUtf8(secret), "store leaked " + secret);
        }
    }

    private TestDevice CreateDevice(FaultInjectingCloudObjectStoreTransport transport, SyncCloudSettings settings)
    {
        string path = Path.Combine(Path.GetTempPath(), $"qn_matrix_{Guid.NewGuid():N}.db");
        _tempFiles.Add(path);
        var node = new TestDevice(path, Guid.NewGuid(), transport, settings);
        _disposables.Add(node);
        return node;
    }

    private sealed class TestDevice : IDisposable
    {
        public Guid DeviceId { get; }
        public string DbPath { get; }
        public QuickNotesDbContext Db { get; }
        public FixedDeviceIdProvider DeviceIdProvider { get; }
        public SyncEngine Engine { get; }

        public TestDevice(
            string dbPath,
            Guid deviceId,
            FaultInjectingCloudObjectStoreTransport transport,
            SyncCloudSettings settings)
        {
            DeviceId = deviceId;
            DbPath = dbPath;
            Db = SqliteTestUtil.CreateContext(dbPath);
            DbInitializer.Initialize(Db);
            var crypto = new SyncCryptoService(iterations: 5_000);
            DeviceIdProvider = new FixedDeviceIdProvider(deviceId);
            var keyHelper = new SyncObjectKeyHelper(settings);
            Engine = new SyncEngine(
                transport,
                new SyncPackageExporter(crypto, DeviceIdProvider),
                new SyncPackageImporter(crypto),
                DeviceIdProvider,
                settings,
                new Creds(),
                keyHelper,
                dbFactory: () => SqliteTestUtil.CreateContext(dbPath));
        }

        public Task<SyncCycleResult> SyncAsync(string password)
            => Engine.RunSyncCycleAsync(Db, password);

        public void Dispose() => Db.Dispose();
    }

    private sealed class Creds : IS3CredentialsStorage
    {
        public Task<S3Credentials?> LoadCredentialsAsync(System.Threading.CancellationToken ct = default)
            => Task.FromResult<S3Credentials?>(new S3Credentials("testKey", "testSecret"));
        public Task SaveCredentialsAsync(S3Credentials credentials, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteCredentialsAsync(System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public bool HasCredentials() => true;
    }
}
