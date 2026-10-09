using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.Services;
using QuickNotes.App.Services.NoteProtection;
using QuickNotes.App.Services.Sync;
using Xunit;
using Xunit.Abstractions;

namespace QuickNotes.Tests;

/// <summary>
/// Opt-in two-device live acceptance against real Yandex Object Storage using production
/// <see cref="SyncEngine"/> and conflict resolution. Skips all network unless
/// QUICKNOTES_LIVE_S3_REQUIRED=1 and connection parameters are present.
/// </summary>
[TestCategory(TestCategories.LiveCloud)]
public sealed class YandexObjectStorageLiveAcceptanceTests
{
    private readonly ITestOutputHelper _output;

    public YandexObjectStorageLiveAcceptanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(Timeout = 180_000)]
    public async Task OptIn_TwoDevice_ProductionSync_KeepBothTombstoneAndProtectedScan()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("QUICKNOTES_LIVE_S3_REQUIRED"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        string? credentialsPath = Environment.GetEnvironmentVariable("QUICKNOTES_LIVE_S3_CREDENTIALS_PATH");
        string? endpoint = Environment.GetEnvironmentVariable("QUICKNOTES_LIVE_S3_ENDPOINT");
        string? bucket = Environment.GetEnvironmentVariable("QUICKNOTES_LIVE_S3_BUCKET");
        Assert.False(string.IsNullOrWhiteSpace(credentialsPath));
        Assert.False(string.IsNullOrWhiteSpace(endpoint));
        Assert.False(string.IsNullOrWhiteSpace(bucket));
        Assert.True(File.Exists(credentialsPath));

        string runPrefix = SyncCloudSettingsValidator.NormalizePrefix(
            $"quicknotes-live-acceptance/{DateTime.UtcNow:yyyyMMdd}/{Guid.NewGuid():D}");
        Assert.True(LiveCloudPrefixGuard.IsSafeLivePrefix(runPrefix));

        var settings = new SyncCloudSettings
        {
            Enabled = true,
            Endpoint = endpoint,
            Region = SyncCloudSettings.DefaultYandexRegion,
            Bucket = bucket,
            Prefix = runPrefix.TrimEnd('/'),
            RequestTimeoutSeconds = 20,
            MaxRetryAttempts = 2,
            SyncAttachments = true
        };

        string marker = "QN-LIVE-" + Guid.NewGuid().ToString("N");
        string protectedMarker = "QN-PROT-" + Guid.NewGuid().ToString("N");
        string syncPassword = "LiveAccept-" + Guid.NewGuid().ToString("N");
        string notePassword = "NotePw-" + Guid.NewGuid().ToString("N");
        byte[] attachmentBytes = Encoding.UTF8.GetBytes("att-" + marker);

        var createdKeys = new HashSet<string>(StringComparer.Ordinal);
        var probe = new LiveRunProbe();
        var credentials = new DpapiS3CredentialsStorage(credentialsPath);
        using var innerTransport = new S3ObjectStoreTransport(settings, credentials);
        using var transport = new TrackingCloudTransport(innerTransport, runPrefix, createdKeys, probe);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var sw = Stopwatch.StartNew();
        Exception? testFailure = null;

        LiveDevice? deviceA = null;
        LiveDevice? deviceB = null;
        try
        {
            probe.Phase = "create-devices";
            deviceA = LiveDevice.Create(transport, settings, credentials);
            deviceB = LiveDevice.Create(transport, settings, credentials);

            await RunScenarioAsync(
                deviceA,
                deviceB,
                marker,
                protectedMarker,
                syncPassword,
                notePassword,
                attachmentBytes,
                transport,
                runPrefix,
                probe,
                deadline.Token);

            _output.WriteLine(
                $"Live acceptance succeeded in {sw.ElapsedMilliseconds} ms; tracked objects={createdKeys.Count}; prefix-root=quicknotes-live-acceptance; last={probe.Format(sw.Elapsed)}.");
        }
        catch (Exception ex)
        {
            testFailure = WrapFailure(ex, probe, sw.Elapsed);
        }
        finally
        {
            string previousPhase = probe.Phase;
            probe.Phase = "cleanup";
            using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            try
            {
                await CleanupPrefixAsync(transport, runPrefix, createdKeys, cleanupCts.Token);
            }
            catch (Exception cleanupEx)
            {
                _output.WriteLine(
                    $"Cleanup finished with {cleanupEx.GetType().Name}; originalFailure={(testFailure != null)}; {probe.Format(sw.Elapsed)}.");
                if (testFailure == null)
                {
                    testFailure = WrapFailure(cleanupEx, probe, sw.Elapsed);
                }
            }
            finally
            {
                if (testFailure != null && previousPhase != "cleanup")
                {
                    probe.Phase = previousPhase;
                }
            }

            deviceA?.Dispose();
            deviceB?.Dispose();
            CryptographicOperations.ZeroMemory(attachmentBytes);
        }

        if (testFailure != null)
        {
            throw testFailure;
        }
    }

    private static async Task RunScenarioAsync(
        LiveDevice deviceA,
        LiveDevice deviceB,
        string marker,
        string protectedMarker,
        string syncPassword,
        string notePassword,
        byte[] attachmentBytes,
        TrackingCloudTransport transport,
        string runPrefix,
        LiveRunProbe probe,
        CancellationToken ct)
    {
        var options = new SyncCycleOptions { WaitIfBusy = true };

        var note = new Note
        {
            Title = "live-first",
            Text = "first-exchange " + marker,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        deviceA.Db.Notes.Add(note);
        await deviceA.Db.SaveChangesAsync(ct).ConfigureAwait(false);
        AddLocalAttachment(deviceA, note.Id, attachmentBytes, "live-small.bin");
        Guid sharedSyncId = note.SyncId;

        probe.Phase = "first-push-A";
        var firstPush = AssertSync(await deviceA.Engine.RunSyncCycleAsync(deviceA.Db, syncPassword, options, ct: ct).ConfigureAwait(false));
        Assert.True(firstPush.PackageUploaded);
        probe.Phase = "first-pull-B";
        var pullB1 = AssertSync(await deviceB.Engine.RunSyncCycleAsync(deviceB.Db, syncPassword, options, ct: ct).ConfigureAwait(false));
        Assert.True(pullB1.RemotePackagesPulled >= 1);
        await ConvergeAsync(deviceA, deviceB, syncPassword, options, probe, ct).ConfigureAwait(false);

        probe.Phase = "assert-first-exchange";
        Reload(deviceB);
        var noteOnB = await deviceB.Db.Notes.SingleAsync(n => n.SyncId == sharedSyncId, ct).ConfigureAwait(false);
        Assert.Equal("first-exchange " + marker, noteOnB.Text);
        var attB = await deviceB.Db.NoteAttachments.SingleAsync(a => a.NoteId == noteOnB.Id, ct).ConfigureAwait(false);
        Assert.Equal(attachmentBytes, File.ReadAllBytes(deviceB.Attachments.GetFullPath(attB.RelativePath)));

        Reload(deviceA);
        int notesAfterFirstA = await deviceA.Db.Notes.CountAsync(ct).ConfigureAwait(false);
        int notesAfterFirstB = await deviceB.Db.Notes.CountAsync(ct).ConfigureAwait(false);
        int attachmentsAfterFirstB = await deviceB.Db.NoteAttachments.CountAsync(ct).ConfigureAwait(false);
        Assert.Equal(1, notesAfterFirstA);
        Assert.Equal(1, notesAfterFirstB);

        probe.Phase = "repeat-noop";
        var repeatA = AssertSync(await deviceA.Engine.RunSyncCycleAsync(deviceA.Db, syncPassword, options, ct: ct).ConfigureAwait(false));
        var repeatB = AssertSync(await deviceB.Engine.RunSyncCycleAsync(deviceB.Db, syncPassword, options, ct: ct).ConfigureAwait(false));
        Assert.False(repeatA.PackageUploaded);
        Assert.False(repeatB.PackageUploaded);
        Assert.Equal(0, repeatA.ConflictsDetected);
        Assert.Equal(0, repeatB.ConflictsDetected);
        Reload(deviceA);
        Reload(deviceB);
        Assert.Equal(notesAfterFirstA, await deviceA.Db.Notes.CountAsync(ct).ConfigureAwait(false));
        Assert.Equal(notesAfterFirstB, await deviceB.Db.Notes.CountAsync(ct).ConfigureAwait(false));
        Assert.Equal(attachmentsAfterFirstB, await deviceB.Db.NoteAttachments.CountAsync(ct).ConfigureAwait(false));

        probe.Phase = "edit-A-push";
        var noteAEdit = await deviceA.Db.Notes.SingleAsync(n => n.SyncId == sharedSyncId, ct).ConfigureAwait(false);
        noteAEdit.Text = "edited-on-A " + marker;
        noteAEdit.UpdatedAt = DateTime.UtcNow;
        await deviceA.Db.SaveChangesAsync(ct).ConfigureAwait(false);
        var pushAEdit = AssertSync(await deviceA.Engine.RunSyncCycleAsync(deviceA.Db, syncPassword, options, ct: ct).ConfigureAwait(false));
        Assert.True(pushAEdit.PackageUploaded);
        probe.Phase = "edit-A-pull-B";
        AssertSync(await deviceB.Engine.RunSyncCycleAsync(deviceB.Db, syncPassword, options, ct: ct).ConfigureAwait(false));
        await ConvergeAsync(deviceA, deviceB, syncPassword, options, probe, ct).ConfigureAwait(false);
        Reload(deviceB);
        Assert.Equal("edited-on-A " + marker, (await deviceB.Db.Notes.SingleAsync(n => n.SyncId == sharedSyncId, ct).ConfigureAwait(false)).Text);

        probe.Phase = "edit-B-push";
        Reload(deviceB);
        var noteBEdit = await deviceB.Db.Notes.SingleAsync(n => n.SyncId == sharedSyncId, ct).ConfigureAwait(false);
        noteBEdit.Text = "edited-on-B " + marker;
        noteBEdit.UpdatedAt = DateTime.UtcNow;
        await deviceB.Db.SaveChangesAsync(ct).ConfigureAwait(false);
        var pushBEdit = AssertSync(await deviceB.Engine.RunSyncCycleAsync(deviceB.Db, syncPassword, options, ct: ct).ConfigureAwait(false));
        Assert.True(pushBEdit.PackageUploaded);
        probe.Phase = "edit-B-pull-A";
        AssertSync(await deviceA.Engine.RunSyncCycleAsync(deviceA.Db, syncPassword, options, ct: ct).ConfigureAwait(false));
        await ConvergeAsync(deviceA, deviceB, syncPassword, options, probe, ct).ConfigureAwait(false);
        Reload(deviceA);
        Assert.Equal("edited-on-B " + marker, (await deviceA.Db.Notes.SingleAsync(n => n.SyncId == sharedSyncId, ct).ConfigureAwait(false)).Text);

        probe.Phase = "conflict-A-push";
        Reload(deviceA);
        var noteAConflict = await deviceA.Db.Notes.SingleAsync(n => n.SyncId == sharedSyncId, ct).ConfigureAwait(false);
        noteAConflict.Text = "conflict-A " + marker;
        noteAConflict.UpdatedAt = DateTime.UtcNow;
        await deviceA.Db.SaveChangesAsync(ct).ConfigureAwait(false);
        AssertSync(await deviceA.Engine.RunSyncCycleAsync(deviceA.Db, syncPassword, options, ct: ct).ConfigureAwait(false));

        probe.Phase = "conflict-B-pull";
        Reload(deviceB);
        var noteBConflict = await deviceB.Db.Notes.SingleAsync(n => n.SyncId == sharedSyncId, ct).ConfigureAwait(false);
        noteBConflict.Text = "conflict-B " + marker;
        noteBConflict.UpdatedAt = DateTime.UtcNow;
        await deviceB.Db.SaveChangesAsync(ct).ConfigureAwait(false);
        var conflictPull = AssertSync(await deviceB.Engine.RunSyncCycleAsync(deviceB.Db, syncPassword, options, ct: ct).ConfigureAwait(false));
        Assert.Equal(1, conflictPull.ConflictsDetected);

        Reload(deviceB);
        var conflict = await deviceB.Db.SyncConflicts.SingleAsync(c => !c.IsResolved, ct).ConfigureAwait(false);
        Assert.Equal("Note", conflict.EntityType);
        Assert.Equal(sharedSyncId, conflict.SyncId);
        Guid expectedCopySyncId = SyncConflictIdentity.DeriveKeepBothSyncId(conflict.SyncId, conflict.RemoteRevisionId);

        probe.Phase = "keep-both";
        var conflictService = new SyncConflictService(
            () => SqliteTestUtil.CreateContext(deviceB.DbPath),
            deviceB.DeviceIdProvider,
            new NoteHistoryService());
        var keepBoth = await conflictService.ResolveKeepBothAsync(conflict.Id, ct).ConfigureAwait(false);
        Assert.True(keepBoth.Success, keepBoth.ErrorMessage ?? "KeepBoth failed.");

        Reload(deviceB);
        var localKept = await deviceB.Db.Notes.SingleAsync(n => n.SyncId == sharedSyncId, ct).ConfigureAwait(false);
        var copyOnB = await deviceB.Db.Notes.SingleAsync(n => n.SyncId == expectedCopySyncId, ct).ConfigureAwait(false);
        Assert.Equal("conflict-B " + marker, localKept.Text);
        Assert.Equal("conflict-A " + marker, copyOnB.Text);
        Assert.NotEqual(localKept.Id, copyOnB.Id);
        Assert.NotEqual(localKept.SyncId, copyOnB.SyncId);

        probe.Phase = "keep-both-sync";
        AssertSync(await deviceB.Engine.RunSyncCycleAsync(deviceB.Db, syncPassword, options, ct: ct).ConfigureAwait(false));
        AssertSync(await deviceA.Engine.RunSyncCycleAsync(deviceA.Db, syncPassword, options, ct: ct).ConfigureAwait(false));
        Reload(deviceA);
        Assert.NotNull(await deviceA.Db.Notes.SingleOrDefaultAsync(n => n.SyncId == sharedSyncId, ct).ConfigureAwait(false));
        Assert.NotNull(await deviceA.Db.Notes.SingleOrDefaultAsync(n => n.SyncId == expectedCopySyncId, ct).ConfigureAwait(false));

        probe.Phase = "accept-remote-A";
        var unresolvedOnA = await deviceA.Db.SyncConflicts.Where(c => !c.IsResolved).ToListAsync(ct).ConfigureAwait(false);
        if (unresolvedOnA.Count > 0)
        {
            var conflictsA = new SyncConflictService(
                () => SqliteTestUtil.CreateContext(deviceA.DbPath),
                deviceA.DeviceIdProvider,
                new NoteHistoryService());
            foreach (var pending in unresolvedOnA)
            {
                var acceptRemote = await conflictsA.ResolveAcceptRemoteAsync(pending.Id, ct).ConfigureAwait(false);
                Assert.True(acceptRemote.Success, acceptRemote.ErrorMessage ?? "AcceptRemote failed after KeepBoth copy arrived.");
            }

            Reload(deviceA);
        }

        Reload(deviceA);
        Reload(deviceB);
        var aOriginal = await deviceA.Db.Notes.SingleAsync(n => n.SyncId == sharedSyncId, ct).ConfigureAwait(false);
        var aCopy = await deviceA.Db.Notes.SingleAsync(n => n.SyncId == expectedCopySyncId, ct).ConfigureAwait(false);
        var bOriginal = await deviceB.Db.Notes.SingleAsync(n => n.SyncId == sharedSyncId, ct).ConfigureAwait(false);
        var bCopy = await deviceB.Db.Notes.SingleAsync(n => n.SyncId == expectedCopySyncId, ct).ConfigureAwait(false);
        Assert.Equal("conflict-B " + marker, aOriginal.Text);
        Assert.Equal("conflict-B " + marker, bOriginal.Text);
        Assert.Equal("conflict-A " + marker, aCopy.Text);
        Assert.Equal("conflict-A " + marker, bCopy.Text);
        Assert.Equal(2, await deviceA.Db.Notes.CountAsync(n => n.DeletedAt == null, ct).ConfigureAwait(false));
        Assert.Equal(2, await deviceB.Db.Notes.CountAsync(n => n.DeletedAt == null, ct).ConfigureAwait(false));

        probe.Phase = "tombstone-create";
        var tombstone = new Note
        {
            Title = "live-tombstone",
            Text = "tombstone " + marker,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        deviceA.Db.Notes.Add(tombstone);
        await deviceA.Db.SaveChangesAsync(ct).ConfigureAwait(false);
        Guid tombstoneSyncId = tombstone.SyncId;
        AssertSync(await deviceA.Engine.RunSyncCycleAsync(deviceA.Db, syncPassword, options, ct: ct).ConfigureAwait(false));
        AssertSync(await deviceB.Engine.RunSyncCycleAsync(deviceB.Db, syncPassword, options, ct: ct).ConfigureAwait(false));
        Reload(deviceB);
        Assert.Null((await deviceB.Db.Notes.SingleAsync(n => n.SyncId == tombstoneSyncId, ct).ConfigureAwait(false)).DeletedAt);

        probe.Phase = "tombstone-delete";
        Reload(deviceA);
        var tombstoneLocal = await deviceA.Db.Notes.SingleAsync(n => n.SyncId == tombstoneSyncId, ct).ConfigureAwait(false);
        tombstoneLocal.DeletedAt = DateTime.UtcNow;
        tombstoneLocal.UpdatedAt = DateTime.UtcNow;
        await deviceA.Db.SaveChangesAsync(ct).ConfigureAwait(false);
        AssertSync(await deviceA.Engine.RunSyncCycleAsync(deviceA.Db, syncPassword, options, ct: ct).ConfigureAwait(false));
        AssertSync(await deviceB.Engine.RunSyncCycleAsync(deviceB.Db, syncPassword, options, ct: ct).ConfigureAwait(false));
        Reload(deviceB);
        Assert.NotNull((await deviceB.Db.Notes.SingleAsync(n => n.SyncId == tombstoneSyncId, ct).ConfigureAwait(false)).DeletedAt);
        AssertSync(await deviceB.Engine.RunSyncCycleAsync(deviceB.Db, syncPassword, options, ct: ct).ConfigureAwait(false));
        Reload(deviceB);
        Assert.NotNull((await deviceB.Db.Notes.SingleAsync(n => n.SyncId == tombstoneSyncId, ct).ConfigureAwait(false)).DeletedAt);

        probe.Phase = "protected-note-sync";
        var protectedNote = new Note
        {
            Title = "protected-live",
            Text = "protected-body " + protectedMarker,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        deviceA.Db.Notes.Add(protectedNote);
        await deviceA.Db.SaveChangesAsync(ct).ConfigureAwait(false);
        var protectionA = new NoteProtectionService(attachmentStorage: deviceA.Attachments);
        Assert.True(protectionA.ProtectNote(deviceA.Db, protectedNote.Id, notePassword).Success);
        Assert.Equal(string.Empty, protectedNote.Text);
        Guid protectedSyncId = protectedNote.SyncId;
        AssertSync(await deviceA.Engine.RunSyncCycleAsync(deviceA.Db, syncPassword, options, ct: ct).ConfigureAwait(false));
        AssertSync(await deviceB.Engine.RunSyncCycleAsync(deviceB.Db, syncPassword, options, ct: ct).ConfigureAwait(false));
        Reload(deviceB);
        var protectedOnB = await deviceB.Db.Notes.SingleAsync(n => n.SyncId == protectedSyncId, ct).ConfigureAwait(false);
        Assert.True(protectedOnB.IsProtected);
        Assert.Equal(string.Empty, protectedOnB.Text);
        var unlocked = new NoteProtectionService(attachmentStorage: deviceB.Attachments)
            .UnlockNote(deviceB.Db, protectedOnB.Id, notePassword);
        Assert.Contains(protectedMarker, unlocked.Text, StringComparison.Ordinal);

        probe.Phase = "protected-plaintext-scan";
        await AssertPlaintextAbsentFromCloudAsync(transport, runPrefix, protectedMarker, ct).ConfigureAwait(false);
    }

    private static async Task AssertPlaintextAbsentFromCloudAsync(
        TrackingCloudTransport transport,
        string prefix,
        string plaintextMarker,
        CancellationToken ct)
    {
        byte[] markerBytes = Encoding.UTF8.GetBytes(plaintextMarker);
        var listed = await ListAllUnderPrefixAsync(transport, prefix, ct).ConfigureAwait(false);
        Assert.NotEmpty(listed);

        foreach (string key in listed)
        {
            Assert.DoesNotContain(plaintextMarker, key, StringComparison.Ordinal);
            StorageObjectMetadata? head = await transport.HeadObjectAsync(key, ct).ConfigureAwait(false);
            Assert.NotNull(head);
            AssertNoPlaintextInPublicMetadata(head!, plaintextMarker);

            StorageObjectResult? body = await transport.GetObjectAsync(key, ct).ConfigureAwait(false);
            Assert.NotNull(body);
            AssertNoPlaintextInPublicMetadata(body!.Metadata, plaintextMarker);
            Assert.True(
                body.Content.AsSpan().IndexOf(markerBytes) < 0,
                "Protected plaintext marker must not appear in uploaded object bodies.");
        }
    }

    private static void AssertNoPlaintextInPublicMetadata(StorageObjectMetadata metadata, string marker)
    {
        Assert.DoesNotContain(marker, metadata.Key ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(marker, metadata.ETag ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(marker, metadata.ContentType ?? string.Empty, StringComparison.Ordinal);
    }

    private static async Task<List<string>> ListAllUnderPrefixAsync(
        ICloudObjectStoreTransport transport,
        string prefix,
        CancellationToken ct)
    {
        var keys = new List<string>();
        string? token = null;
        do
        {
            var page = await transport.ListObjectsV2Async(
                new StorageListRequest
                {
                    Prefix = prefix,
                    ContinuationToken = token,
                    MaxKeys = 1000
                },
                ct).ConfigureAwait(false);
            foreach (var obj in page.Objects)
            {
                if (LiveCloudPrefixGuard.IsKeyInsidePrefix(prefix, obj.Key))
                {
                    keys.Add(obj.Key);
                }
            }

            token = page.IsTruncated ? page.NextContinuationToken : null;
        }
        while (!string.IsNullOrEmpty(token));

        return keys;
    }

    private static async Task CleanupPrefixAsync(
        TrackingCloudTransport transport,
        string prefix,
        ISet<string> registered,
        CancellationToken ct)
    {
        var listed = await ListAllUnderPrefixAsync(transport, prefix, ct).ConfigureAwait(false);
        foreach (string key in LiveCloudPrefixGuard.FilterDeletableKeys(prefix, registered.Concat(listed)).Distinct(StringComparer.Ordinal))
        {
            await transport.DeleteObjectAsync(key, ct).ConfigureAwait(false);
        }
    }

    private static async Task ConvergeAsync(
        LiveDevice deviceA,
        LiveDevice deviceB,
        string syncPassword,
        SyncCycleOptions options,
        LiveRunProbe probe,
        CancellationToken ct)
    {
        string previous = probe.Phase;
        for (int i = 0; i < 3; i++)
        {
            probe.Phase = $"{previous}/converge-{i}-A";
            var resA = AssertSync(await deviceA.Engine.RunSyncCycleAsync(deviceA.Db, syncPassword, options, ct: ct).ConfigureAwait(false));
            probe.Phase = $"{previous}/converge-{i}-B";
            var resB = AssertSync(await deviceB.Engine.RunSyncCycleAsync(deviceB.Db, syncPassword, options, ct: ct).ConfigureAwait(false));
            if (!resA.PackageUploaded && !resB.PackageUploaded && resA.ConflictsDetected == 0 && resB.ConflictsDetected == 0)
            {
                probe.Phase = previous;
                return;
            }
        }

        probe.Phase = previous;
    }

    private static SyncCycleResult AssertSync(SyncCycleResult result)
    {
        Assert.NotNull(result);
        Assert.True(result.Success, result.ErrorMessage ?? "Sync cycle failed.");
        Assert.False(result.IsAuthError);
        Assert.False(result.IsOffline);
        Assert.False(result.IsTimeout);
        return result;
    }

    private static Exception WrapFailure(Exception ex, LiveRunProbe probe, TimeSpan elapsed)
    {
        string cloud = ex is CloudStorageException cse
            ? $"cloudError={cse.ErrorCode}; http={(cse.StatusCode.HasValue ? cse.StatusCode.Value.ToString() : "none")}"
            : $"ex={ex.GetType().Name}";
        string message = $"Live acceptance failed ({probe.Format(elapsed)}; {cloud})";
        if (ex is not CloudStorageException)
        {
            message += $": {ex.Message}";
        }

        return new InvalidOperationException(LiveCloudPrefixGuard.RedactLiveDiagnostics(message));
    }

    private static void Reload(LiveDevice device)
    {
        device.Db.ChangeTracker.Clear();
    }

    private static NoteAttachment AddLocalAttachment(LiveDevice device, int noteId, byte[] content, string originalName)
    {
        string source = Path.Combine(device.AttachmentsRoot, "src-" + Guid.NewGuid().ToString("N") + Path.GetExtension(originalName));
        File.WriteAllBytes(source, content);
        var saved = device.Attachments.SaveAttachment(source, 10 * 1024 * 1024);
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

    private sealed class LiveDevice : IDisposable
    {
        public string DbPath { get; }
        public QuickNotesDbContext Db { get; }
        public FixedDeviceIdProvider DeviceIdProvider { get; }
        public AttachmentStorageService Attachments { get; }
        public string AttachmentsRoot { get; }
        public SyncEngine Engine { get; }

        private LiveDevice(
            string dbPath,
            QuickNotesDbContext db,
            FixedDeviceIdProvider deviceIdProvider,
            AttachmentStorageService attachments,
            string attachmentsRoot,
            SyncEngine engine)
        {
            DbPath = dbPath;
            Db = db;
            DeviceIdProvider = deviceIdProvider;
            Attachments = attachments;
            AttachmentsRoot = attachmentsRoot;
            Engine = engine;
        }

        public static LiveDevice Create(
            ICloudObjectStoreTransport transport,
            SyncCloudSettings settings,
            IS3CredentialsStorage credentials)
        {
            string dbPath = Path.Combine(Path.GetTempPath(), $"qn_live_accept_{Guid.NewGuid():N}.db");
            var db = SqliteTestUtil.CreateContext(dbPath);
            DbInitializer.Initialize(db);

            var crypto = new SyncCryptoService();
            var deviceId = new FixedDeviceIdProvider(Guid.NewGuid());
            var keyHelper = new SyncObjectKeyHelper(settings);
            string attachmentsRoot = Path.Combine(Path.GetTempPath(), $"qn_live_att_{Guid.NewGuid():N}");
            var attachments = new AttachmentStorageService(attachmentsRoot);
            var blobService = new SyncAttachmentBlobService(transport, crypto, attachments, keyHelper, settings);
            var engine = new SyncEngine(
                transport,
                new SyncPackageExporter(crypto, deviceId),
                new SyncPackageImporter(crypto),
                deviceId,
                settings,
                credentials,
                keyHelper,
                dbFactory: () => SqliteTestUtil.CreateContext(dbPath),
                blobService: blobService,
                exclusiveLock: new SyncCloudExclusiveLock());

            return new LiveDevice(dbPath, db, deviceId, attachments, attachmentsRoot, engine);
        }

        public void Dispose()
        {
            Db.Dispose();
            SqliteTestUtil.TryDeleteFileAndSiblings(DbPath);
            SqliteTestUtil.TryDeleteDirectory(AttachmentsRoot);
        }
    }

    private sealed class LiveRunProbe
    {
        public string Phase { get; set; } = "start";
        public string LastOp { get; set; } = "none";
        public string LastKind { get; set; } = "none";

        public void Record(string op, string? key = null)
        {
            LastOp = op;
            LastKind = string.Equals(op, "ListObjectsV2", StringComparison.Ordinal)
                || string.Equals(op, "ListObjects", StringComparison.Ordinal)
                ? "listing"
                : LiveCloudPrefixGuard.ClassifyObjectKind(key);
        }

        public string Format(TimeSpan elapsed)
            => $"phase={Phase}; op={LastOp}; objectKind={LastKind}; elapsedMs={(int)elapsed.TotalMilliseconds}";
    }

    private sealed class TrackingCloudTransport : ICloudObjectStoreTransport
    {
        private readonly ICloudObjectStoreTransport _inner;
        private readonly string _prefix;
        private readonly ISet<string> _createdKeys;
        private readonly LiveRunProbe _probe;

        public TrackingCloudTransport(
            ICloudObjectStoreTransport inner,
            string prefix,
            ISet<string> createdKeys,
            LiveRunProbe probe)
        {
            _inner = inner;
            _prefix = prefix;
            _createdKeys = createdKeys;
            _probe = probe;
        }

        public Task<bool> TestConnectionAsync(CancellationToken ct = default)
        {
            _probe.Record("TestConnection");
            return _inner.TestConnectionAsync(ct);
        }

        public Task<StorageObjectMetadata?> HeadObjectAsync(string key, CancellationToken ct = default)
        {
            _probe.Record("HeadObject", key);
            return _inner.HeadObjectAsync(key, ct);
        }

        public Task<StorageObjectResult?> GetObjectAsync(string key, CancellationToken ct = default)
        {
            _probe.Record("GetObject", key);
            return _inner.GetObjectAsync(key, ct);
        }

        public async Task<StorageObjectMetadata> PutImmutableObjectAsync(
            string key,
            byte[] content,
            string contentType = "application/json",
            CancellationToken ct = default)
        {
            _probe.Record("PutImmutable", key);
            try
            {
                var meta = await _inner.PutImmutableObjectAsync(key, content, contentType, ct).ConfigureAwait(false);
                LiveCloudPrefixGuard.RegisterCreatedKey(_createdKeys, _prefix, meta.Key);
                return meta;
            }
            catch (CloudConflictException)
            {
                LiveCloudPrefixGuard.RegisterCreatedKey(_createdKeys, _prefix, key);
                throw;
            }
        }

        public async Task<StorageObjectMetadata> PutConditionalPointerAsync(
            string key,
            byte[] content,
            string? expectedETag,
            string contentType = "application/json",
            CancellationToken ct = default)
        {
            _probe.Record("PutConditionalPointer", key);
            var meta = await _inner.PutConditionalPointerAsync(key, content, expectedETag, contentType, ct)
                .ConfigureAwait(false);
            LiveCloudPrefixGuard.RegisterCreatedKey(_createdKeys, _prefix, meta.Key);
            return meta;
        }

        public Task<IReadOnlyList<StorageObjectSummary>> ListObjectsAsync(
            string? prefix = null,
            int maxKeys = 1000,
            CancellationToken ct = default)
        {
            _probe.Record("ListObjects");
            return _inner.ListObjectsAsync(prefix, maxKeys, ct);
        }

        public Task<StorageListResult> ListObjectsV2Async(StorageListRequest request, CancellationToken ct = default)
        {
            _probe.Record("ListObjectsV2");
            return _inner.ListObjectsV2Async(request, ct);
        }

        public Task<bool> DeleteObjectAsync(string key, CancellationToken ct = default)
        {
            if (!LiveCloudPrefixGuard.IsKeyInsidePrefix(_prefix, key))
            {
                throw new InvalidOperationException("Refusing to delete a cloud object outside the unique live-test prefix.");
            }

            _probe.Record("DeleteObject", key);
            return _inner.DeleteObjectAsync(key, ct);
        }

        public void Dispose()
        {
            // Inner transport is owned by the test method.
        }
    }
}
