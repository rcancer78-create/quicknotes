using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
public class SyncConflictServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly QuickNotesDbContext _context;
    private readonly NoteHistoryService _historyService;
    private readonly Guid _localDeviceId = Guid.NewGuid();

    public SyncConflictServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"quicknotes_conflict_test_{Guid.NewGuid():N}.db");
        _context = new QuickNotesDbContext(_dbPath);
        DbInitializer.Initialize(_context);
        _historyService = new NoteHistoryService();
    }

    public void Dispose()
    {
        _context.Dispose();
        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch { }
        }
    }

    private SyncConflictService CreateService(ILocalMutationCoordinator? coordinator = null)
    {
        return new SyncConflictService(
            () => new QuickNotesDbContext(_dbPath),
            new FixedDeviceIdProvider(_localDeviceId),
            _historyService,
            coordinator);
    }

    private SyncConflictRecord CreateNoteConflict(
        Guid syncId,
        string localText,
        string remoteText,
        List<string>? localTags = null,
        List<string>? remoteTags = null)
    {
        var localTagDtos = new List<SyncNoteTagDto>();
        var note = _context.Notes.Include(n => n.NoteTags).FirstOrDefault(n => n.SyncId == syncId);
        if (note == null)
        {
            note = new Note
            {
                SyncId = syncId,
                Text = localText,
                CreatedAt = DateTime.UtcNow.AddDays(-1),
                UpdatedAt = DateTime.UtcNow
            };
            _context.Notes.Add(note);
        }

        var state = _context.SyncEntityStates.FirstOrDefault(s => s.SyncId == syncId);
        if (state == null)
        {
            state = new SyncEntityState
            {
                SyncId = syncId,
                EntityType = "Note",
                RevisionId = Guid.NewGuid(),
                DeviceId = _localDeviceId,
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-30),
                IsDeleted = false,
                ContentHash = SyncFingerprintHelper.ComputeNoteFingerprint(
                    localText, false, false, false, null, null, null, null, false, Enumerable.Empty<(Guid, TagOrigin, bool)>())
            };
            _context.SyncEntityStates.Add(state);
        }
        if (localTags != null)
        {
            foreach (var tagName in localTags)
            {
                var tag = _context.Tags.FirstOrDefault(t => t.Name == tagName);
                if (tag == null)
                {
                    tag = new Tag { Name = tagName, SyncId = Guid.NewGuid() };
                    _context.Tags.Add(tag);
                }
                localTagDtos.Add(new SyncNoteTagDto { TagSyncId = tag.SyncId });
                if (note != null && !note.NoteTags.Any(nt => nt.TagId == tag.Id))
                {
                    note.NoteTags.Add(new NoteTag { Tag = tag });
                }
            }
        }

        var remoteTagDtos = new List<SyncNoteTagDto>();
        if (remoteTags != null)
        {
            foreach (var tagName in remoteTags)
            {
                var tag = _context.Tags.FirstOrDefault(t => t.Name == tagName);
                if (tag == null)
                {
                    tag = new Tag { Name = tagName, SyncId = Guid.NewGuid() };
                    _context.Tags.Add(tag);
                }
                remoteTagDtos.Add(new SyncNoteTagDto { TagSyncId = tag.SyncId });
            }
        }

        var localSnap = new SyncNoteDto
        {
            SyncId = syncId,
            Text = localText,
            Tags = localTagDtos,
            CreatedAtUtc = DateTime.UtcNow.AddHours(-2),
            UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-30),
            IsPinned = false,
            IsFavorite = true
        };

        var remoteSnap = new SyncNoteDto
        {
            SyncId = syncId,
            Text = remoteText,
            Tags = remoteTagDtos,
            CreatedAtUtc = DateTime.UtcNow.AddHours(-2),
            UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-10),
            IsPinned = true,
            IsFavorite = false
        };

        var conflict = new SyncConflictRecord
        {
            SyncId = syncId,
            EntityType = "Note",
            DetectedAtUtc = DateTime.UtcNow,
            SourceDeviceId = Guid.NewGuid(),
            Reason = "Concurrent updates on different devices",
            LocalDataJson = JsonSerializer.Serialize(localSnap),
            RemoteDataJson = JsonSerializer.Serialize(remoteSnap),
            LocalRevisionId = state.RevisionId,
            RemoteRevisionId = Guid.NewGuid(),
            IsResolved = false
        };

        _context.SyncConflicts.Add(conflict);
        _context.SaveChanges();
        return conflict;
    }

    [Fact]
    public async Task GetUnresolvedConflicts_ReturnsOnlyUnresolved()
    {
        var service = CreateService();
        var c1 = CreateNoteConflict(Guid.NewGuid(), "L1", "T2");
        var c2 = CreateNoteConflict(Guid.NewGuid(), "L2", "T2");

        c2.IsResolved = true;
        c2.ResolvedAtUtc = DateTime.UtcNow;
        _context.SaveChanges();

        var list = await service.GetUnresolvedConflictsAsync();
        Assert.Single(list);
        Assert.Equal(c1.Id, list[0].Id);

        int count = await service.GetUnresolvedConflictsCountAsync();
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task GetConflictDetail_ParsesLocalAndRemoteFields()
    {
        var service = CreateService();
        var syncId = Guid.NewGuid();
        var note = new Note
        {
            SyncId = syncId,
            Text = "Local Body Line 1\nLine 2",
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            UpdatedAt = DateTime.UtcNow
        };
        _context.Notes.Add(note);
        _context.SaveChanges();

        var c = CreateNoteConflict(syncId, "Local Body Line 1\nLine 2", "Remote Body Line 1\nLine 2",
            new List<string> { "work", "ideas" }, new List<string> { "cloud", "sync" });

        var detail = await service.GetConflictDetailAsync(c.Id);
        Assert.NotNull(detail);
        Assert.Equal("Local Body Line 1", detail.LocalTitle);
        Assert.Equal("Local Body Line 1\nLine 2", detail.LocalText);
        Assert.Equal("Remote Body Line 1", detail.RemoteTitle);
        Assert.Equal("Remote Body Line 1\nLine 2", detail.RemoteText);
        Assert.Contains("work", detail.LocalTags);
        Assert.Contains("cloud", detail.RemoteTags);
        Assert.False(detail.IsLocalCorrupted);
        Assert.False(detail.IsRemoteCorrupted);
        Assert.True(detail.CanMerge);
        Assert.True(detail.CanKeepBoth);
    }

    [Fact]
    public async Task GetConflictDetail_CorruptedJson_DoesNotThrowAndFlagsCorruption()
    {
        var service = CreateService();
        var conflict = new SyncConflictRecord
        {
            SyncId = Guid.NewGuid(),
            EntityType = "Note",
            DetectedAtUtc = DateTime.UtcNow,
            SourceDeviceId = Guid.NewGuid(),
            Reason = "Corrupted package",
            LocalDataJson = "{ invalid json string...",
            RemoteDataJson = "not json at all",
            IsResolved = false
        };
        _context.SyncConflicts.Add(conflict);
        _context.SaveChanges();

        var detail = await service.GetConflictDetailAsync(conflict.Id);
        Assert.NotNull(detail);
        Assert.True(detail.IsLocalCorrupted);
        Assert.True(detail.IsRemoteCorrupted);
        Assert.NotNull(detail.LocalCorruptionError);
        Assert.NotNull(detail.RemoteCorruptionError);
        Assert.False(detail.CanMerge);
    }

    [Fact]
    public async Task ResolveKeepBoth_CreatesNewNoteWithSuffix_AndMarksConflictResolved()
    {
        var service = CreateService();
        var syncId = Guid.NewGuid();

        // Seed local note
        var localNote = new Note
        {
            SyncId = syncId,
            Text = "Локальный текст",
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            UpdatedAt = DateTime.UtcNow
        };
        _context.Notes.Add(localNote);
        _context.SaveChanges();

        var c = CreateNoteConflict(syncId, "Локальный текст", "Облачный текст",
            new List<string> { "local" }, new List<string> { "remote" });

        var res = await service.ResolveKeepBothAsync(c.Id);
        Assert.True(res.Success);

        // Verify conflict resolved
        using var db = new QuickNotesDbContext(_dbPath);
        var updatedConflict = db.SyncConflicts.Find(c.Id);
        Assert.NotNull(updatedConflict);
        Assert.True(updatedConflict.IsResolved);
        Assert.Equal("KeepBoth", updatedConflict.ResolutionAction);
        Assert.NotNull(updatedConflict.ResolvedAtUtc);

        // Local note remains unchanged
        var currentLocal = db.Notes.FirstOrDefault(n => n.SyncId == syncId);
        Assert.NotNull(currentLocal);
        Assert.Equal("Локальный текст", currentLocal.Text);

        var copyNote = db.Notes.FirstOrDefault(n => n.SyncId != syncId);
        Assert.NotNull(copyNote);
        Assert.Equal(SyncConflictIdentity.DeriveKeepBothSyncId(syncId, c.RemoteRevisionId), copyNote.SyncId);
        Assert.Equal("Облачный текст", copyNote.Text);
        Assert.Contains("(облако)", copyNote.Title);
    }

    [Fact]
    public async Task ResolveKeepLocal_RetainsLocal_AndMarksConflictResolved()
    {
        var service = CreateService();
        var syncId = Guid.NewGuid();

        var localNote = new Note
        {
            SyncId = syncId,
            Text = "Локальное содержимое",
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            UpdatedAt = DateTime.UtcNow
        };
        _context.Notes.Add(localNote);
        _context.SaveChanges();

        var c = CreateNoteConflict(syncId, "Локальное содержимое", "Облачный контент");

        var res = await service.ResolveKeepLocalAsync(c.Id);
        Assert.True(res.Success);

        using var db = new QuickNotesDbContext(_dbPath);
        var updatedConflict = db.SyncConflicts.Find(c.Id);
        Assert.NotNull(updatedConflict);
        Assert.True(updatedConflict.IsResolved);
        Assert.Equal("KeepLocal", updatedConflict.ResolutionAction);

        var current = db.Notes.FirstOrDefault(n => n.SyncId == syncId);
        Assert.NotNull(current);
        Assert.Equal("Локальное содержимое", current.Text);
    }

    [Fact]
    public async Task ResolveAcceptRemote_AppliesCloudVersion_AndRecordsHistoryRevision()
    {
        var service = CreateService();
        var syncId = Guid.NewGuid();

        var localNote = new Note
        {
            SyncId = syncId,
            Text = "Старый текст",
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            UpdatedAt = DateTime.UtcNow
        };
        _context.Notes.Add(localNote);
        _context.SaveChanges();

        var c = CreateNoteConflict(syncId, "Старый текст", "Новый текст из облака");

        var res = await service.ResolveAcceptRemoteAsync(c.Id);
        Assert.True(res.Success);

        using var db = new QuickNotesDbContext(_dbPath);
        var current = db.Notes.FirstOrDefault(n => n.SyncId == syncId);
        Assert.NotNull(current);
        Assert.Equal("Новый текст из облака", current.Text);

        // History snapshot recorded
        var history = db.NoteRevisions.Where(h => h.NoteId == current.Id).ToList();
        Assert.NotEmpty(history);

        // Conflict marked resolved
        var updatedConflict = db.SyncConflicts.Find(c.Id);
        Assert.NotNull(updatedConflict);
        Assert.True(updatedConflict.IsResolved);
        Assert.Equal("AcceptRemote", updatedConflict.ResolutionAction);
    }

    [Fact]
    public async Task ResolveMergeNote_AppliesMergedDraft_AndRecordsRevision()
    {
        var service = CreateService();
        var syncId = Guid.NewGuid();

        var localNote = new Note
        {
            SyncId = syncId,
            Text = "Локальная строка",
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            UpdatedAt = DateTime.UtcNow
        };
        _context.Notes.Add(localNote);
        _context.SaveChanges();

        var c = CreateNoteConflict(syncId, "Локальная строка", "Облачная строка");

        string mergedContent = "Локальная строка\n---\nОблачная строка (объединено вручную)";
        var res = await service.ResolveMergeNoteAsync(c.Id, mergedContent);
        Assert.True(res.Success);

        using var db = new QuickNotesDbContext(_dbPath);
        var current = db.Notes.FirstOrDefault(n => n.SyncId == syncId);
        Assert.NotNull(current);
        Assert.Equal(mergedContent, current.Text);

        var updatedConflict = db.SyncConflicts.Find(c.Id);
        Assert.NotNull(updatedConflict);
        Assert.True(updatedConflict.IsResolved);
        Assert.Equal("Merge", updatedConflict.ResolutionAction);
    }

    [Fact]
    public async Task Idempotence_ResolvingAlreadyResolvedConflict_SucceedsWithoutChanges()
    {
        var service = CreateService();
        var syncId = Guid.NewGuid();
        var c = CreateNoteConflict(syncId, "L", "T2");

        var first = await service.ResolveKeepLocalAsync(c.Id);
        Assert.True(first.Success);

        var second = await service.ResolveKeepLocalAsync(c.Id);
        Assert.True(second.Success);
    }

    [Fact]
    public async Task FailureInjection_RollsBackTransaction_LeavesConflictUnresolved()
    {
        var service = CreateService();
        var syncId = Guid.NewGuid();
        var c = CreateNoteConflict(syncId, "L", "T2");

        service.FailureInjectionHook = step => throw new InvalidOperationException("Simulated crash during resolution");

        var result = await service.ResolveKeepBothAsync(c.Id);
        Assert.False(result.Success);
        Assert.Contains("Simulated crash", result.ErrorMessage);

        using var db = new QuickNotesDbContext(_dbPath);
        var conflict = db.SyncConflicts.Find(c.Id);
        Assert.NotNull(conflict);
        Assert.False(conflict.IsResolved);
    }

    [Fact]
    public async Task ResolveAcceptRemote_Note_SetsCanonicalContentHash_NoPendingLocalModifications()
    {
        var service = CreateService();
        var syncId = Guid.NewGuid();
        var note = new Note
        {
            SyncId = syncId,
            Text = "Local Version",
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            UpdatedAt = DateTime.UtcNow
        };
        _context.Notes.Add(note);
        _context.SaveChanges();

        var c = CreateNoteConflict(syncId, "Local Version", "Remote Cloud Version");

        var res = await service.ResolveAcceptRemoteAsync(c.Id);
        Assert.True(res.Success);

        using var db = new QuickNotesDbContext(_dbPath);
        var updatedNote = db.Notes.FirstOrDefault(n => n.SyncId == syncId);
        Assert.NotNull(updatedNote);
        Assert.Equal("Remote Cloud Version", updatedNote.Text);

        var state = db.SyncEntityStates.FirstOrDefault(s => s.SyncId == syncId);
        Assert.NotNull(state);
        Assert.Equal(c.RemoteRevisionId, state.RevisionId);
        Assert.Equal(c.SourceDeviceId, state.DeviceId);
        Assert.False(state.IsDeleted);
        Assert.NotNull(state.ContentHash);

        // Verification: No pending modifications detected on immediate sync/export
        bool hasPending = SyncSnapshotHelper.HasPendingLocalModifications(db, _localDeviceId);
        Assert.False(hasPending, "AcceptRemote must set exact canonical ContentHash so no false revision is created.");
    }

    [Fact]
    public async Task ResolveAcceptRemote_Tag_ReconcilesParentAndSynonyms_NoPendingLocalModifications()
    {
        var service = CreateService();
        var parentSyncId = Guid.NewGuid();
        var parentTag = new Tag { SyncId = parentSyncId, Name = "RemoteParent" };
        _context.Tags.Add(parentTag);
        _context.SyncEntityStates.Add(new SyncEntityState
        {
            SyncId = parentSyncId,
            EntityType = "Tag",
            RevisionId = Guid.NewGuid(),
            DeviceId = _localDeviceId,
            UpdatedAtUtc = DateTime.UtcNow,
            IsDeleted = false,
            ContentHash = SyncFingerprintHelper.ComputeTagFingerprint(parentTag.Name, null, Enumerable.Empty<string>())
        });

        var tagSyncId = Guid.NewGuid();
        var localTag = new Tag { SyncId = tagSyncId, Name = "OldTagName" };
        localTag.Synonyms.Add(new TagSynonym { Value = "old-synonym", Tag = localTag });
        _context.Tags.Add(localTag);
        _context.SaveChanges();

        var remoteTagDto = new SyncTagDto
        {
            SyncId = tagSyncId,
            Name = "NewTagName",
            ParentTagSyncId = parentSyncId,
            Synonyms = new List<string> { "new-syn1", "new-syn2" },
            UpdatedAtUtc = DateTime.UtcNow
        };

        var conflict = new SyncConflictRecord
        {
            SyncId = tagSyncId,
            EntityType = "Tag",
            DetectedAtUtc = DateTime.UtcNow,
            SourceDeviceId = Guid.NewGuid(),
            Reason = "Tag conflict",
            LocalDataJson = "{}",
            RemoteDataJson = JsonSerializer.Serialize(remoteTagDto),
            RemoteRevisionId = Guid.NewGuid(),
            ParentRevisionId = Guid.NewGuid(),
            IsResolved = false
        };
        _context.SyncConflicts.Add(conflict);
        _context.SaveChanges();

        var res = await service.ResolveAcceptRemoteAsync(conflict.Id);
        Assert.True(res.Success);

        using var db = new QuickNotesDbContext(_dbPath);
        var updatedTag = db.Tags.Include(t => t.Synonyms).Include(t => t.ParentTag).FirstOrDefault(t => t.SyncId == tagSyncId);
        Assert.NotNull(updatedTag);
        Assert.Equal("NewTagName", updatedTag.Name);
        Assert.NotNull(updatedTag.ParentTag);
        Assert.Equal("RemoteParent", updatedTag.ParentTag!.Name);
        Assert.Equal(2, updatedTag.Synonyms.Count);
        Assert.Contains(updatedTag.Synonyms, s => s.Value == "new-syn1");
        Assert.Contains(updatedTag.Synonyms, s => s.Value == "new-syn2");

        var state = db.SyncEntityStates.FirstOrDefault(s => s.SyncId == tagSyncId);
        Assert.NotNull(state);
        Assert.Equal(conflict.RemoteRevisionId, state.RevisionId);

        // Verification: canonical ContentHash matches, no false revision
        bool hasPending = SyncSnapshotHelper.HasPendingLocalModifications(db, _localDeviceId);
        Assert.False(hasPending, "AcceptRemote must set exact canonical ContentHash for Tag.");
    }

    [Fact]
    public async Task ResolveAcceptRemote_NoteTemplate_ReconcilesTags_NoPendingLocalModifications()
    {
        var service = CreateService();
        var tag1 = new Tag { SyncId = Guid.NewGuid(), Name = "TmplTag1" };
        var tag2 = new Tag { SyncId = Guid.NewGuid(), Name = "TmplTag2" };
        _context.Tags.AddRange(tag1, tag2);
        _context.SyncEntityStates.AddRange(
            new SyncEntityState
            {
                SyncId = tag1.SyncId,
                EntityType = "Tag",
                RevisionId = Guid.NewGuid(),
                DeviceId = _localDeviceId,
                UpdatedAtUtc = DateTime.UtcNow,
                IsDeleted = false,
                ContentHash = SyncFingerprintHelper.ComputeTagFingerprint(tag1.Name, null, Enumerable.Empty<string>())
            },
            new SyncEntityState
            {
                SyncId = tag2.SyncId,
                EntityType = "Tag",
                RevisionId = Guid.NewGuid(),
                DeviceId = _localDeviceId,
                UpdatedAtUtc = DateTime.UtcNow,
                IsDeleted = false,
                ContentHash = SyncFingerprintHelper.ComputeTagFingerprint(tag2.Name, null, Enumerable.Empty<string>())
            });

        var tmplSyncId = Guid.NewGuid();
        var localTmpl = new NoteTemplate { SyncId = tmplSyncId, Title = "Old Title", Text = "Old Text" };
        _context.NoteTemplates.Add(localTmpl);
        _context.SaveChanges();

        var remoteTmplDto = new SyncTemplateDto
        {
            SyncId = tmplSyncId,
            Title = "New Remote Title",
            Text = "New Remote Text",
            TagSyncIds = new List<Guid> { tag1.SyncId, tag2.SyncId },
            UpdatedAtUtc = DateTime.UtcNow
        };

        var conflict = new SyncConflictRecord
        {
            SyncId = tmplSyncId,
            EntityType = "NoteTemplate",
            DetectedAtUtc = DateTime.UtcNow,
            SourceDeviceId = Guid.NewGuid(),
            Reason = "Template conflict",
            LocalDataJson = "{}",
            RemoteDataJson = JsonSerializer.Serialize(remoteTmplDto),
            RemoteRevisionId = Guid.NewGuid(),
            ParentRevisionId = Guid.NewGuid(),
            IsResolved = false
        };
        _context.SyncConflicts.Add(conflict);
        _context.SaveChanges();

        var res = await service.ResolveAcceptRemoteAsync(conflict.Id);
        Assert.True(res.Success);

        using var db = new QuickNotesDbContext(_dbPath);
        var updatedTmpl = db.NoteTemplates.Include(t => t.TemplateTags).ThenInclude(tt => tt.Tag).FirstOrDefault(t => t.SyncId == tmplSyncId);
        Assert.NotNull(updatedTmpl);
        Assert.Equal("New Remote Title", updatedTmpl.Title);
        Assert.Equal("New Remote Text", updatedTmpl.Text);
        Assert.Equal(2, updatedTmpl.TemplateTags.Count);

        var state = db.SyncEntityStates.FirstOrDefault(s => s.SyncId == tmplSyncId);
        Assert.NotNull(state);
        Assert.Equal(conflict.RemoteRevisionId, state.RevisionId);

        bool hasPending = SyncSnapshotHelper.HasPendingLocalModifications(db, _localDeviceId);
        Assert.False(hasPending, "AcceptRemote must set exact canonical ContentHash for NoteTemplate.");
    }

    [Fact]
    public async Task ResolveAcceptRemote_NoteAttachment_ValidatesParentNote_SetsCanonicalHash_NoPendingLocalModifications()
    {
        var service = CreateService();
        var parentNoteSyncId = Guid.NewGuid();
        var parentNote = new Note
        {
            SyncId = parentNoteSyncId,
            Text = "Parent Note",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _context.Notes.Add(parentNote);
        _context.SaveChanges();

        // Seed state for parent note so it's not marked modified
        var parentState = new SyncEntityState
        {
            SyncId = parentNoteSyncId,
            EntityType = "Note",
            RevisionId = Guid.NewGuid(),
            DeviceId = _localDeviceId,
            UpdatedAtUtc = DateTime.UtcNow,
            IsDeleted = false,
            ContentHash = SyncFingerprintHelper.ComputeNoteFingerprint("Parent Note", false, false, false, null, null, null, null, false, Enumerable.Empty<(Guid, TagOrigin, bool)>())
        };
        _context.SyncEntityStates.Add(parentState);
        _context.SaveChanges();

        var attSyncId = Guid.NewGuid();
        var remoteAttDto = new SyncAttachmentDto
        {
            SyncId = attSyncId,
            NoteSyncId = parentNoteSyncId,
            OriginalFileName = "document.pdf",
            ContentType = "application/pdf",
            Size = 2048,
            Sha256 = "dummy-sha256-hash",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        var conflict = new SyncConflictRecord
        {
            SyncId = attSyncId,
            EntityType = "NoteAttachment",
            DetectedAtUtc = DateTime.UtcNow,
            SourceDeviceId = Guid.NewGuid(),
            Reason = "Attachment conflict",
            LocalDataJson = "{}",
            RemoteDataJson = JsonSerializer.Serialize(remoteAttDto),
            RemoteRevisionId = Guid.NewGuid(),
            ParentRevisionId = Guid.NewGuid(),
            IsResolved = false
        };
        _context.SyncConflicts.Add(conflict);
        _context.SaveChanges();

        var res = await service.ResolveAcceptRemoteAsync(conflict.Id);
        Assert.True(res.Success);

        using var db = new QuickNotesDbContext(_dbPath);
        var att = db.NoteAttachments.FirstOrDefault(a => a.SyncId == attSyncId);
        Assert.NotNull(att);
        Assert.Equal("document.pdf", att.OriginalFileName);
        Assert.Equal(parentNote.Id, att.NoteId);

        var state = db.SyncEntityStates.FirstOrDefault(s => s.SyncId == attSyncId);
        Assert.NotNull(state);
        Assert.Equal(conflict.RemoteRevisionId, state.RevisionId);

        bool hasPending = SyncSnapshotHelper.HasPendingLocalModifications(db, _localDeviceId);
        Assert.False(hasPending, "AcceptRemote must set exact canonical ContentHash for NoteAttachment.");
    }

    [Fact]
    public async Task ResolveAcceptRemote_NoteAttachment_MissingParentNote_RollsBack_LeavesConflictUnresolved()
    {
        var service = CreateService();
        var missingParentSyncId = Guid.NewGuid();
        var attSyncId = Guid.NewGuid();
        var remoteAttDto = new SyncAttachmentDto
        {
            SyncId = attSyncId,
            NoteSyncId = missingParentSyncId,
            OriginalFileName = "orphan.pdf",
            ContentType = "application/pdf",
            Size = 100,
            Sha256 = "hash100",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

        var conflict = new SyncConflictRecord
        {
            SyncId = attSyncId,
            EntityType = "NoteAttachment",
            DetectedAtUtc = DateTime.UtcNow,
            SourceDeviceId = Guid.NewGuid(),
            Reason = "Attachment conflict",
            LocalDataJson = "{}",
            RemoteDataJson = JsonSerializer.Serialize(remoteAttDto),
            RemoteRevisionId = Guid.NewGuid(),
            IsResolved = false
        };
        _context.SyncConflicts.Add(conflict);
        _context.SaveChanges();

        var res = await service.ResolveAcceptRemoteAsync(conflict.Id);
        Assert.False(res.Success);
        Assert.Contains("не найдена", res.ErrorMessage);

        using var db = new QuickNotesDbContext(_dbPath);
        var updatedConflict = db.SyncConflicts.Find(conflict.Id);
        Assert.NotNull(updatedConflict);
        Assert.False(updatedConflict.IsResolved);
        Assert.Null(db.NoteAttachments.FirstOrDefault(a => a.SyncId == attSyncId));
    }

    [Fact]
    public async Task ResolveAcceptRemote_Tombstone_RemovesEntity_SetsTombstoneState_NoPendingLocalModifications()
    {
        var service = CreateService();
        var noteSyncId = Guid.NewGuid();
        var localNote = new Note { SyncId = noteSyncId, Text = "To be deleted note", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _context.Notes.Add(localNote);
        _context.SaveChanges();

        var remoteDto = new SyncNoteDto
        {
            SyncId = noteSyncId,
            Operation = SyncOperationType.Delete,
            UpdatedAtUtc = DateTime.UtcNow
        };

        var conflict = new SyncConflictRecord
        {
            SyncId = noteSyncId,
            EntityType = "Note",
            DetectedAtUtc = DateTime.UtcNow,
            SourceDeviceId = Guid.NewGuid(),
            Reason = "Delete conflict",
            LocalDataJson = "{}",
            RemoteDataJson = JsonSerializer.Serialize(remoteDto),
            RemoteRevisionId = Guid.NewGuid(),
            IsResolved = false
        };
        _context.SyncConflicts.Add(conflict);
        _context.SaveChanges();

        var res = await service.ResolveAcceptRemoteAsync(conflict.Id);
        Assert.True(res.Success);

        using var db = new QuickNotesDbContext(_dbPath);
        Assert.NotNull(db.Notes.FirstOrDefault(n => n.SyncId == noteSyncId)?.DeletedAt);

        var state = db.SyncEntityStates.FirstOrDefault(s => s.SyncId == noteSyncId);
        Assert.NotNull(state);
        Assert.True(state.IsDeleted);
        Assert.NotNull(state.DeletedAtUtc);
        Assert.Equal(SyncFingerprintHelper.ComputeTombstoneFingerprint("Note"), state.ContentHash);

        bool hasPending = SyncSnapshotHelper.HasPendingLocalModifications(db, _localDeviceId);
        Assert.False(hasPending, "Tombstone must set canonical tombstone fingerprint with IsDeleted=true.");
    }

    [Fact]
    public async Task ResolveKeepBoth_Tag_CreatesIndependentCopyWithUniqueNameAndSynonyms()
    {
        var service = CreateService();
        var syncId = Guid.NewGuid();
        var localTag = new Tag { SyncId = syncId, Name = "Finance" };
        localTag.Synonyms.Add(new TagSynonym { Value = "money", Tag = localTag });
        _context.Tags.Add(localTag);
        _context.SaveChanges();

        var remoteTagDto = new SyncTagDto
        {
            SyncId = syncId,
            Name = "Finance",
            Synonyms = new List<string> { "budget", "money" },
            UpdatedAtUtc = DateTime.UtcNow
        };

        var conflict = new SyncConflictRecord
        {
            SyncId = syncId,
            EntityType = "Tag",
            DetectedAtUtc = DateTime.UtcNow,
            SourceDeviceId = Guid.NewGuid(),
            Reason = "Tag conflict",
            LocalDataJson = "{}",
            RemoteDataJson = JsonSerializer.Serialize(remoteTagDto),
            RemoteRevisionId = Guid.NewGuid(),
            IsResolved = false
        };
        _context.SyncConflicts.Add(conflict);
        _context.SaveChanges();

        var res = await service.ResolveKeepBothAsync(conflict.Id);
        Assert.True(res.Success);

        using var db = new QuickNotesDbContext(_dbPath);
        var tags = db.Tags.Include(t => t.Synonyms).ToList();
        Assert.Equal(2, tags.Count);
        var original = tags.FirstOrDefault(t => t.SyncId == syncId);
        var copy = tags.FirstOrDefault(t => t.SyncId != syncId);
        Assert.NotNull(original);
        Assert.NotNull(copy);
        Assert.Equal("Finance", original.Name);
        Assert.Equal("Finance (облако)", copy.Name);
        Assert.Equal(2, copy.Synonyms.Count);
    }

    [Fact]
    public async Task ResolveKeepBoth_NoteTemplate_CreatesIndependentCopyWithUniqueTitleAndTags()
    {
        var service = CreateService();
        var tag = new Tag { SyncId = Guid.NewGuid(), Name = "Work" };
        _context.Tags.Add(tag);

        var syncId = Guid.NewGuid();
        var localTmpl = new NoteTemplate { SyncId = syncId, Title = "Daily Meeting", Text = "Local meeting notes" };
        _context.NoteTemplates.Add(localTmpl);
        _context.SaveChanges();

        var remoteDto = new SyncTemplateDto
        {
            SyncId = syncId,
            Title = "Daily Meeting",
            Text = "Remote meeting notes",
            TagSyncIds = new List<Guid> { tag.SyncId },
            UpdatedAtUtc = DateTime.UtcNow
        };

        var conflict = new SyncConflictRecord
        {
            SyncId = syncId,
            EntityType = "NoteTemplate",
            DetectedAtUtc = DateTime.UtcNow,
            SourceDeviceId = Guid.NewGuid(),
            Reason = "Template conflict",
            LocalDataJson = "{}",
            RemoteDataJson = JsonSerializer.Serialize(remoteDto),
            RemoteRevisionId = Guid.NewGuid(),
            IsResolved = false
        };
        _context.SyncConflicts.Add(conflict);
        _context.SaveChanges();

        var res = await service.ResolveKeepBothAsync(conflict.Id);
        Assert.True(res.Success);

        using var db = new QuickNotesDbContext(_dbPath);
        var templates = db.NoteTemplates.Include(t => t.TemplateTags).ToList();
        Assert.Equal(2, templates.Count);
        var original = templates.FirstOrDefault(t => t.SyncId == syncId);
        var copy = templates.FirstOrDefault(t => t.SyncId != syncId);
        Assert.NotNull(original);
        Assert.NotNull(copy);
        Assert.Equal("Daily Meeting", original.Title);
        Assert.Equal("Daily Meeting (облако)", copy.Title);
        Assert.Equal("Remote meeting notes", copy.Text);
        Assert.Single(copy.TemplateTags);
    }

    [Fact]
    public async Task ResolveKeepBoth_NoteAttachment_RejectsWithoutResolving()
    {
        var service = CreateService();
        var conflict = new SyncConflictRecord
        {
            SyncId = Guid.NewGuid(),
            EntityType = "NoteAttachment",
            DetectedAtUtc = DateTime.UtcNow,
            SourceDeviceId = Guid.NewGuid(),
            Reason = "Attachment conflict",
            LocalDataJson = "{}",
            RemoteDataJson = "{}",
            IsResolved = false
        };
        _context.SyncConflicts.Add(conflict);
        _context.SaveChanges();

        var res = await service.ResolveKeepBothAsync(conflict.Id);
        Assert.False(res.Success);
        Assert.Contains("не поддерживается", res.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        using var db = new QuickNotesDbContext(_dbPath);
        var updatedConflict = db.SyncConflicts.Find(conflict.Id);
        Assert.NotNull(updatedConflict);
        Assert.False(updatedConflict.IsResolved);
    }

    [Fact]
    public async Task Resolve_CorruptedRemoteJson_RollsBack_LeavesConflictUnresolved()
    {
        var service = CreateService();
        var conflict = new SyncConflictRecord
        {
            SyncId = Guid.NewGuid(),
            EntityType = "Note",
            DetectedAtUtc = DateTime.UtcNow,
            SourceDeviceId = Guid.NewGuid(),
            Reason = "Corrupted",
            LocalDataJson = "{}",
            RemoteDataJson = "{ corrupted json...",
            IsResolved = false
        };
        _context.SyncConflicts.Add(conflict);
        _context.SaveChanges();

        var resAccept = await service.ResolveAcceptRemoteAsync(conflict.Id);
        Assert.False(resAccept.Success);

        var resKeepBoth = await service.ResolveKeepBothAsync(conflict.Id);
        Assert.False(resKeepBoth.Success);

        var resMerge = await service.ResolveMergeNoteAsync(conflict.Id, "Merged");
        Assert.False(resMerge.Success);

        using var db = new QuickNotesDbContext(_dbPath);
        var updated = db.SyncConflicts.Find(conflict.Id);
        Assert.NotNull(updated);
        Assert.False(updated.IsResolved);
    }

    [Fact]
    public async Task Resolve_UnknownEntityType_RollsBack_LeavesConflictUnresolved()
    {
        var service = CreateService();
        var conflict = new SyncConflictRecord
        {
            SyncId = Guid.NewGuid(),
            EntityType = "UnknownEntity",
            DetectedAtUtc = DateTime.UtcNow,
            SourceDeviceId = Guid.NewGuid(),
            Reason = "Unknown",
            LocalDataJson = "{}",
            RemoteDataJson = "{}",
            IsResolved = false
        };
        _context.SyncConflicts.Add(conflict);
        _context.SaveChanges();

        var res = await service.ResolveAcceptRemoteAsync(conflict.Id);
        Assert.False(res.Success);
        Assert.Contains("Неподдерживаемый тип", res.ErrorMessage);

        using var db = new QuickNotesDbContext(_dbPath);
        var updated = db.SyncConflicts.Find(conflict.Id);
        Assert.NotNull(updated);
        Assert.False(updated.IsResolved);
    }

    [Fact]
    public async Task Resolve_AllActions_IdempotentOnAlreadyResolvedConflict()
    {
        var service = CreateService();
        var syncId = Guid.NewGuid();
        var c = CreateNoteConflict(syncId, "Local Original", "Remote Version");

        var first = await service.ResolveAcceptRemoteAsync(c.Id);
        Assert.True(first.Success);

        using (var db = new QuickNotesDbContext(_dbPath))
        {
            var conf = db.SyncConflicts.Find(c.Id);
            Assert.True(conf!.IsResolved);
        }

        // Call AcceptRemote again
        var second = await service.ResolveAcceptRemoteAsync(c.Id);
        Assert.True(second.Success);

        // Call KeepLocal on already resolved conflict
        var keepLocal = await service.ResolveKeepLocalAsync(c.Id);
        Assert.True(keepLocal.Success);

        // Call KeepBoth on already resolved conflict - must NOT duplicate notes
        var keepBoth = await service.ResolveKeepBothAsync(c.Id);
        Assert.True(keepBoth.Success);

        // Call Merge on already resolved conflict
        var merge = await service.ResolveMergeNoteAsync(c.Id, "New Merged");
        Assert.True(merge.Success);

        using (var db = new QuickNotesDbContext(_dbPath))
        {
            // Note count should not have increased by KeepBoth call
            Assert.Equal(1, db.Notes.Count(n => n.SyncId == syncId));
        }
    }

    [Fact]
    public async Task GetConflictDetail_ShowsDeviceTimeReason_AndProtectedHidesPlaintext()
    {
        var service = CreateService();
        var syncId = Guid.NewGuid();
        var secret = "secret-lock-body-should-never-appear";
        var note = new Note
        {
            SyncId = syncId,
            Title = string.Empty,
            Text = string.Empty,
            IsProtected = true,
            ProtectedCiphertextBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("cipher")),
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            UpdatedAt = DateTime.UtcNow
        };
        _context.Notes.Add(note);
        _context.SaveChanges();

        var remote = new SyncNoteDto
        {
            SyncId = syncId,
            IsProtected = true,
            Text = secret,
            DeviceId = Guid.Empty,
            UpdatedAtUtc = default,
            ProtectedCiphertextBase64 = "abc"
        };
        var conflict = new SyncConflictRecord
        {
            SyncId = syncId,
            EntityType = "Note",
            DetectedAtUtc = DateTime.UtcNow,
            SourceDeviceId = Guid.Empty,
            Reason = "",
            LocalDataJson = JsonSerializer.Serialize(new SyncNoteDto { SyncId = syncId, Text = secret, IsProtected = true }),
            RemoteDataJson = JsonSerializer.Serialize(remote),
            LocalRevisionId = Guid.NewGuid(),
            RemoteRevisionId = Guid.NewGuid()
        };
        _context.SyncConflicts.Add(conflict);
        _context.SaveChanges();

        var detail = await service.GetConflictDetailAsync(conflict.Id);
        Assert.NotNull(detail);
        Assert.True(detail.LocalIsProtected);
        Assert.True(detail.RemoteIsProtected);
        Assert.Equal(string.Empty, detail.LocalText);
        Assert.Equal(string.Empty, detail.RemoteText);
        Assert.DoesNotContain(secret, detail.LocalText);
        Assert.DoesNotContain(secret, detail.LocalTitle);
        Assert.DoesNotContain(secret, detail.RemoteTitle);
        Assert.Contains("скрыт", detail.LocalProtectionNotice);
        Assert.False(detail.CanMerge);
        Assert.Equal("устройство неизвестно", detail.RemoteDeviceDisplay);
        Assert.Equal("время версии неизвестно", detail.RemoteUpdatedDisplay);
        Assert.Equal("Причина конфликта не указана.", detail.ReasonDisplay);
    }

    [Fact]
    public async Task ResolveKeepLocal_StaleConflict_FailsWithoutWrite()
    {
        var service = CreateService();
        var syncId = Guid.NewGuid();
        var c = CreateNoteConflict(syncId, "L", "R");
        var state = _context.SyncEntityStates.First(s => s.SyncId == syncId);
        state.RevisionId = Guid.NewGuid();
        _context.SaveChanges();

        var res = await service.ResolveKeepLocalAsync(c.Id);
        Assert.False(res.Success);
        Assert.Contains("изменилась", res.ErrorMessage);

        using var db = new QuickNotesDbContext(_dbPath);
        Assert.False(db.SyncConflicts.Find(c.Id)!.IsResolved);
    }

    [Fact]
    public async Task ResolveMerge_RemoteTitleAndTags_DoesNotUnionTags()
    {
        var service = CreateService();
        var syncId = Guid.NewGuid();
        var c = CreateNoteConflict(syncId, "Local body", "Remote body",
            new List<string> { "local-only" }, new List<string> { "remote-only" });

        var res = await service.ResolveMergeNoteAsync(c.Id, "Merged markdown", new SyncConflictMergeChoices
        {
            Title = SyncConflictFieldChoice.Remote,
            Tags = SyncConflictFieldChoice.Remote
        });
        Assert.True(res.Success, res.ErrorMessage);

        using var db = new QuickNotesDbContext(_dbPath);
        var note = db.Notes.Include(n => n.NoteTags).ThenInclude(nt => nt.Tag).First(n => n.SyncId == syncId);
        Assert.Equal("Merged markdown", note.Text);
        Assert.DoesNotContain(note.NoteTags, nt => nt.Tag!.Name == "local-only");
        Assert.Contains(note.NoteTags, nt => nt.Tag!.Name == "remote-only");
        Assert.Null(note.DeletedAt);
        Assert.NotEmpty(db.NoteRevisions.Where(r => r.NoteId == note.Id).ToList());
        var fts = db.Database.SqlQueryRaw<int>("SELECT NoteId AS Value FROM NotesFts WHERE NotesFts MATCH {0}", "Merged").ToList();
        Assert.Contains(note.Id, fts);
    }

    private void PlantCompatibleKeepBothNoteCopy(SyncConflictRecord conflict, string remoteText)
    {
        Guid copySyncId = SyncConflictIdentity.DeriveKeepBothSyncId(conflict.SyncId, conflict.RemoteRevisionId);
        Guid copyRevisionId = SyncConflictIdentity.DeriveRevisionId(copySyncId, conflict.RemoteRevisionId, "KeepBothCopy");
        _context.Notes.Add(new Note
        {
            SyncId = copySyncId,
            Title = "Remote copy body (облако)",
            Text = remoteText,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            IsPinned = true,
            IsFavorite = false
        });
        _context.SyncEntityStates.Add(new SyncEntityState
        {
            SyncId = copySyncId,
            EntityType = "Note",
            RevisionId = copyRevisionId,
            DeviceId = _localDeviceId,
            UpdatedAtUtc = DateTime.UtcNow,
            IsDeleted = false,
            ContentHash = "keep-both-copy"
        });
        _context.SaveChanges();
    }

    [Fact]
    public async Task ResolveKeepBoth_IsDeterministic_AndDoesNotDuplicateOnRetryOfExistingCopy()
    {
        var service = CreateService();
        var syncId = Guid.NewGuid();
        var c = CreateNoteConflict(syncId, "Local", "Remote copy body");
        var expectedCopy = SyncConflictIdentity.DeriveKeepBothSyncId(syncId, c.RemoteRevisionId);
        PlantCompatibleKeepBothNoteCopy(c, "Remote copy body");

        Assert.True((await service.ResolveKeepBothAsync(c.Id)).Success);
        using (var db = new QuickNotesDbContext(_dbPath))
        {
            Assert.Equal(2, db.Notes.Count());
            Assert.NotNull(db.Notes.FirstOrDefault(n => n.SyncId == expectedCopy));
            Assert.True(db.SyncConflicts.Find(c.Id)!.IsResolved);
        }

        var retry = await service.ResolveKeepBothAsync(c.Id);
        Assert.True(retry.Success, retry.ErrorMessage);
        using (var db = new QuickNotesDbContext(_dbPath))
        {
            Assert.Equal(2, db.Notes.Count());
            Assert.True(db.SyncConflicts.Find(c.Id)!.IsResolved);
        }
    }

    [Fact]
    public async Task ResolveKeepBoth_ExistingCopy_StaleConflict_RemainsUnresolved()
    {
        var service = CreateService();
        var syncId = Guid.NewGuid();
        var c = CreateNoteConflict(syncId, "Local", "Remote copy body");
        PlantCompatibleKeepBothNoteCopy(c, "Remote copy body");

        var state = _context.SyncEntityStates.First(s => s.SyncId == syncId);
        state.RevisionId = Guid.NewGuid();
        _context.SaveChanges();

        var res = await service.ResolveKeepBothAsync(c.Id);
        Assert.False(res.Success);
        Assert.Contains("изменилась", res.ErrorMessage);

        using var db = new QuickNotesDbContext(_dbPath);
        Assert.False(db.SyncConflicts.Find(c.Id)!.IsResolved);
        Assert.Equal(2, db.Notes.Count());
        Assert.Null(db.SyncConflicts.Find(c.Id)!.ResolutionAction);
    }

    [Fact]
    public async Task ResolveKeepBoth_ExistingCopy_IncompatiblePayload_DoesNotResolve()
    {
        var service = CreateService();
        var syncId = Guid.NewGuid();
        var c = CreateNoteConflict(syncId, "Local", "Remote copy body");
        Guid copySyncId = SyncConflictIdentity.DeriveKeepBothSyncId(syncId, c.RemoteRevisionId);
        _context.Notes.Add(new Note
        {
            SyncId = copySyncId,
            Title = "unrelated",
            Text = "not the remote snapshot",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        _context.SyncEntityStates.Add(new SyncEntityState
        {
            SyncId = copySyncId,
            EntityType = "Note",
            RevisionId = Guid.NewGuid(),
            DeviceId = _localDeviceId,
            UpdatedAtUtc = DateTime.UtcNow,
            ContentHash = "unrelated"
        });
        _context.SaveChanges();

        var res = await service.ResolveKeepBothAsync(c.Id);
        Assert.False(res.Success);
        Assert.Contains("не соответствует", res.ErrorMessage);

        using var db = new QuickNotesDbContext(_dbPath);
        Assert.False(db.SyncConflicts.Find(c.Id)!.IsResolved);
        Assert.Equal(2, db.Notes.Count());
        Assert.Single(db.Notes.Where(n => n.SyncId == copySyncId));
    }

    [Fact]
    public async Task ResolveKeepBoth_IdempotentExistingCopy_FailureBeforeCommit_RollsBackConflict()
    {
        var service = CreateService();
        var syncId = Guid.NewGuid();
        var c = CreateNoteConflict(syncId, "Local", "Remote copy body");
        PlantCompatibleKeepBothNoteCopy(c, "Remote copy body");
        service.FailureInjectionHook = phase =>
        {
            if (phase == "before_commit") throw new InvalidOperationException("injected before_commit");
        };

        var res = await service.ResolveKeepBothAsync(c.Id);
        Assert.False(res.Success);

        using var db = new QuickNotesDbContext(_dbPath);
        Assert.False(db.SyncConflicts.Find(c.Id)!.IsResolved);
        Assert.Null(db.SyncConflicts.Find(c.Id)!.ResolutionAction);
        Assert.Equal(2, db.Notes.Count());
        Assert.Equal(1, db.Notes.Count(n => n.SyncId == syncId));
    }

    [Fact]
    public async Task FailureInjection_OnEachPhase_RollsBackConflictAndNotes()
    {
        foreach (var phase in new[] { "entity", "history", "sync_state", "conflict_row", "before_commit" })
        {
            string dbPath = Path.Combine(Path.GetTempPath(), $"qn_conf_phase_{Guid.NewGuid():N}.db");
            using var ctx = new QuickNotesDbContext(dbPath);
            DbInitializer.Initialize(ctx);
            var history = new NoteHistoryService();
            var service = new SyncConflictService(() => new QuickNotesDbContext(dbPath), new FixedDeviceIdProvider(_localDeviceId), history);
            var syncId = Guid.NewGuid();
            var note = new Note { SyncId = syncId, Text = "phase-local", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            ctx.Notes.Add(note);
            ctx.SaveChanges();
            var state = new SyncEntityState
            {
                SyncId = syncId,
                EntityType = "Note",
                RevisionId = Guid.NewGuid(),
                DeviceId = _localDeviceId,
                UpdatedAtUtc = DateTime.UtcNow,
                ContentHash = "x"
            };
            ctx.SyncEntityStates.Add(state);
            var conflict = new SyncConflictRecord
            {
                SyncId = syncId,
                EntityType = "Note",
                LocalRevisionId = state.RevisionId,
                RemoteRevisionId = Guid.NewGuid(),
                SourceDeviceId = Guid.NewGuid(),
                Reason = "phase",
                LocalDataJson = JsonSerializer.Serialize(new SyncNoteDto { SyncId = syncId, Text = "phase-local" }),
                RemoteDataJson = JsonSerializer.Serialize(new SyncNoteDto { SyncId = syncId, Text = "phase-remote", Title = "R" })
            };
            ctx.SyncConflicts.Add(conflict);
            ctx.SaveChanges();
            int conflictId = conflict.Id;
            service.FailureInjectionHook = p =>
            {
                if (p == phase) throw new InvalidOperationException("injected " + phase);
            };

            var res = await service.ResolveKeepBothAsync(conflictId);
            Assert.False(res.Success, phase);
            using var verify = new QuickNotesDbContext(dbPath);
            Assert.False(verify.SyncConflicts.Find(conflictId)!.IsResolved);
            Assert.Single(verify.Notes);
            ctx.Dispose();
            try { File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public async Task NestedMutationCoordinator_ThrowsWithoutPartialResolution()
    {
        var coordinator = new LocalMutationCoordinator();
        var service = CreateService(coordinator);
        var syncId = Guid.NewGuid();
        var c = CreateNoteConflict(syncId, "L", "R");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ExecuteBulkMutationAsync(async () =>
            {
                await service.ResolveKeepLocalAsync(c.Id);
                return true;
            }));

        using var db = new QuickNotesDbContext(_dbPath);
        Assert.False(db.SyncConflicts.Find(c.Id)!.IsResolved);
    }

    [Fact]
    public async Task ResolveKeepBoth_TombstoneRemote_Rejected()
    {
        var service = CreateService();
        var syncId = Guid.NewGuid();
        var note = new Note { SyncId = syncId, Text = "live", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _context.Notes.Add(note);
        _context.SaveChanges();
        var remote = new SyncNoteDto { SyncId = syncId, Operation = SyncOperationType.Delete, UpdatedAtUtc = DateTime.UtcNow };
        var conflict = new SyncConflictRecord
        {
            SyncId = syncId,
            EntityType = "Note",
            SourceDeviceId = Guid.NewGuid(),
            Reason = "tombstone vs edit",
            RemoteDataJson = JsonSerializer.Serialize(remote),
            LocalDataJson = "{}",
            RemoteRevisionId = Guid.NewGuid()
        };
        _context.SyncConflicts.Add(conflict);
        _context.SaveChanges();

        var keepBoth = await service.ResolveKeepBothAsync(conflict.Id);
        Assert.False(keepBoth.Success);
        var keepLocal = await service.ResolveKeepLocalAsync(conflict.Id);
        Assert.True(keepLocal.Success, keepLocal.ErrorMessage);
        using var db = new QuickNotesDbContext(_dbPath);
        Assert.Equal("live", db.Notes.First(n => n.SyncId == syncId).Text);
        Assert.Null(db.Notes.First(n => n.SyncId == syncId).DeletedAt);
    }

    [Fact]
    public async Task ResolveMerge_ProtectedNote_RejectedWithoutPlaintext()
    {
        var service = CreateService();
        var syncId = Guid.NewGuid();
        var note = new Note { SyncId = syncId, Text = string.Empty, IsProtected = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _context.Notes.Add(note);
        var remote = new SyncNoteDto { SyncId = syncId, Text = "plain-should-not-apply", IsProtected = true };
        var conflict = new SyncConflictRecord
        {
            SyncId = syncId,
            EntityType = "Note",
            SourceDeviceId = Guid.NewGuid(),
            Reason = "prot",
            RemoteDataJson = JsonSerializer.Serialize(remote),
            LocalRevisionId = Guid.NewGuid(),
            RemoteRevisionId = Guid.NewGuid()
        };
        _context.SyncConflicts.Add(conflict);
        _context.SaveChanges();

        var res = await service.ResolveMergeNoteAsync(conflict.Id, "fake merge plaintext");
        Assert.False(res.Success);
        using var db = new QuickNotesDbContext(_dbPath);
        var stored = db.Notes.First(n => n.SyncId == syncId);
        Assert.Equal(string.Empty, stored.Text);
        Assert.DoesNotContain("fake merge", stored.Text);
        Assert.False(db.SyncConflicts.Find(conflict.Id)!.IsResolved);
    }
}
