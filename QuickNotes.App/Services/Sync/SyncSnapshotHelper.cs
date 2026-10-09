using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.Services;

namespace QuickNotes.App.Services.Sync;

/// <summary>
/// Helper for detecting un-exported local modifications, computing deterministic revision vectors,
/// and registering local revisions before pull to protect concurrent local edits from silent overwrites.
/// </summary>
public static class SyncSnapshotHelper
{
    public static string ComputeLocalRevisionVector(QuickNotesDbContext db, Guid localDeviceId)
    {
        var states = db.SyncEntityStates
            .AsNoTracking()
            .Where(s => s.DeviceId == localDeviceId)
            .OrderBy(s => s.SyncId)
            .ToList();

        if (states.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var s in states)
        {
            sb.Append(s.SyncId.ToString("D")).Append(':')
              .Append(s.RevisionId.ToString("D")).Append(':')
              .Append(s.ContentHash ?? string.Empty).Append(':')
              .Append(s.IsDeleted ? '1' : '0').Append(';');
        }

        return SyncFingerprintHelper.ComputeSha256(sb.ToString());
    }

    public static bool HasPendingLocalModifications(QuickNotesDbContext db, Guid localDeviceId)
    {
        var syncStates = db.SyncEntityStates
            .AsNoTracking()
            .ToDictionary(s => s.SyncId);

        // 1. Notes
        var notes = db.Notes
            .AsNoTracking()
            .Include(n => n.NoteTags)
            .ThenInclude(nt => nt.Tag)
            .ToList();

        var activeNoteSyncIds = new HashSet<Guid>();
        foreach (var note in notes)
        {
            activeNoteSyncIds.Add(note.SyncId);
            if (!syncStates.TryGetValue(note.SyncId, out var state))
            {
                return true; // New note
            }

            bool noteDeleted = note.DeletedAt != null;
            if (state.IsDeleted != noteDeleted)
            {
                return true; // Soft-delete state mismatch
            }

            if (!noteDeleted)
            {
                bool isProt = note.IsProtected;
                string fpTitle = isProt ? string.Empty : (note.Title ?? string.Empty);
                string fpText = isProt ? string.Empty : (note.Text ?? string.Empty);
                string? fpProc = isProt ? null : note.SourceProcessName;
                string? fpWin = isProt ? null : note.SourceWindowTitle;
                string? fpUrl = isProt ? null : note.SourceUrl;

                string noteHash = SyncFingerprintHelper.ComputeNoteFingerprint(
                    fpTitle,
                    fpText,
                    note.IsPinned,
                    note.IsFavorite,
                    note.IsInbox,
                    fpProc,
                    fpWin,
                    fpUrl,
                    note.CapturedAt.HasValue ? SyncPackageExporter.NormalizeToUtc(note.CapturedAt.Value) : null,
                    false,
                    note.NoteTags.Where(nt => nt.Tag != null).Select(nt => (nt.Tag!.SyncId, nt.Origin, nt.IsSuppressed)),
                    isProt,
                    note.ProtectedFormatVersion,
                    note.ProtectedKdfIterations,
                    note.ProtectedSaltBase64,
                    note.ProtectedNonceBase64,
                    note.ProtectedTagBase64,
                    note.ProtectedCiphertextBase64,
                    isProt ? note.ProtectedKdfDescriptor : null,
                    isProt ? note.ProtectedOriginalSyncId : null);

                if (string.IsNullOrEmpty(state.ContentHash) || !string.Equals(state.ContentHash, noteHash, StringComparison.Ordinal))
                {
                    return true; // Note content modified
                }
            }
        }

        foreach (var state in syncStates.Values.Where(s => s.EntityType == "Note" && !s.IsDeleted))
        {
            if (!activeNoteSyncIds.Contains(state.SyncId))
            {
                return true; // Hard-deleted note needing tombstone
            }
        }

        // 2. Tags
        var tags = db.Tags
            .AsNoTracking()
            .Include(t => t.Synonyms)
            .Include(t => t.ParentTag)
            .ToList();

        var activeTagSyncIds = new HashSet<Guid>();
        foreach (var tag in tags)
        {
            activeTagSyncIds.Add(tag.SyncId);
            if (!syncStates.TryGetValue(tag.SyncId, out var state))
            {
                return true; // New tag
            }

            if (state.IsDeleted)
            {
                return true; // Tag was deleted in state but active in DB
            }

            Guid? parentTagSyncId = tag.ParentTag?.SyncId ?? (tag.ParentTagId.HasValue ? tags.FirstOrDefault(t => t.Id == tag.ParentTagId.Value)?.SyncId : null);
            string tagHash = SyncFingerprintHelper.ComputeTagFingerprint(tag.Name, parentTagSyncId, tag.Synonyms.Select(s => s.Value));

            if (string.IsNullOrEmpty(state.ContentHash) || !string.Equals(state.ContentHash, tagHash, StringComparison.Ordinal))
            {
                return true; // Tag modified
            }
        }

        foreach (var state in syncStates.Values.Where(s => s.EntityType == "Tag" && !s.IsDeleted))
        {
            if (!activeTagSyncIds.Contains(state.SyncId))
            {
                return true; // Deleted tag needing tombstone
            }
        }

        // 3. Templates
        var templates = db.NoteTemplates
            .AsNoTracking()
            .Include(t => t.TemplateTags)
            .ThenInclude(tt => tt.Tag)
            .ToList();

        var activeTmplSyncIds = new HashSet<Guid>();
        foreach (var tmpl in templates)
        {
            activeTmplSyncIds.Add(tmpl.SyncId);
            if (!syncStates.TryGetValue(tmpl.SyncId, out var state))
            {
                return true; // New template
            }

            if (state.IsDeleted)
            {
                return true;
            }

            string tmplHash = SyncFingerprintHelper.ComputeTemplateFingerprint(
                tmpl.Title,
                tmpl.Text,
                tmpl.TemplateTags.Where(tt => tt.Tag != null).Select(tt => tt.Tag!.SyncId));

            if (string.IsNullOrEmpty(state.ContentHash) || !string.Equals(state.ContentHash, tmplHash, StringComparison.Ordinal))
            {
                return true; // Template modified
            }
        }

        foreach (var state in syncStates.Values.Where(s => s.EntityType == "NoteTemplate" && !s.IsDeleted))
        {
            if (!activeTmplSyncIds.Contains(state.SyncId))
            {
                return true; // Deleted template
            }
        }

        // 4. Attachments
        var attachments = db.NoteAttachments
            .AsNoTracking()
            .Include(a => a.Note)
            .ToList();

        var activeAttSyncIds = new HashSet<Guid>();
        foreach (var att in attachments)
        {
            activeAttSyncIds.Add(att.SyncId);
            if (!syncStates.TryGetValue(att.SyncId, out var state))
            {
                return true;
            }

            if (state.IsDeleted)
            {
                return true;
            }

            string attHash = SyncFingerprintHelper.ComputeAttachmentFingerprint(
                att.Note?.SyncId ?? Guid.Empty,
                att.OriginalFileName,
                att.ContentType,
                att.Size,
                att.Sha256,
                att.IsProtected,
                att.ProtectedFormatVersion,
                att.ProtectedKdfIterations,
                att.ProtectedSaltBase64,
                att.ProtectedNonceBase64,
                att.ProtectedTagBase64,
                att.ProtectedCiphertextBase64,
                att.IsProtected ? att.ProtectedKdfDescriptor : null);

            if (string.IsNullOrEmpty(state.ContentHash) || !string.Equals(state.ContentHash, attHash, StringComparison.Ordinal))
            {
                return true;
            }
        }

        foreach (var state in syncStates.Values.Where(s => s.EntityType == "NoteAttachment" && !s.IsDeleted))
        {
            if (!activeAttSyncIds.Contains(state.SyncId))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks whether any committed local modifications or interrupted pending uploads
    /// exist in the durable SQLite state waiting to be synchronized to the cloud.
    /// </summary>
    public static bool HasDurablePendingWork(QuickNotesDbContext db, Guid localDeviceId)
    {
        if (localDeviceId == Guid.Empty)
        {
            return false;
        }

        var localState = db.SyncLocalStates.AsNoTracking().FirstOrDefault(s => s.DeviceId == localDeviceId);
        if (localState != null && localState.PendingPackageId.HasValue && localState.PendingPackageId.Value != Guid.Empty)
        {
            return true;
        }

        return HasPendingLocalModifications(db, localDeviceId);
    }

    /// <summary>
    /// Returns the set of Note SyncIds that currently have un-uploaded local modifications
    /// (new note, edited content, or deletion state mismatch) compared to SyncEntityStates.
    /// </summary>
    public static HashSet<Guid> GetPendingNoteSyncIds(QuickNotesDbContext db, Guid localDeviceId)
    {
        var pendingIds = new HashSet<Guid>();
        var syncStates = db.SyncEntityStates
            .AsNoTracking()
            .Where(s => s.EntityType == "Note")
            .ToDictionary(s => s.SyncId);

        var notes = db.Notes
            .AsNoTracking()
            .Include(n => n.NoteTags)
            .ThenInclude(nt => nt.Tag)
            .ToList();

        foreach (var note in notes)
        {
            if (!syncStates.TryGetValue(note.SyncId, out var state))
            {
                pendingIds.Add(note.SyncId);
                continue;
            }

            bool noteDeleted = note.DeletedAt != null;
            if (state.IsDeleted != noteDeleted)
            {
                pendingIds.Add(note.SyncId);
                continue;
            }

            if (!noteDeleted)
            {
                bool isProt = note.IsProtected;
                string fpTitle = isProt ? string.Empty : (note.Title ?? string.Empty);
                string fpText = isProt ? string.Empty : (note.Text ?? string.Empty);
                string? fpProc = isProt ? null : note.SourceProcessName;
                string? fpWin = isProt ? null : note.SourceWindowTitle;
                string? fpUrl = isProt ? null : note.SourceUrl;

                string noteHash = SyncFingerprintHelper.ComputeNoteFingerprint(
                    fpTitle,
                    fpText,
                    note.IsPinned,
                    note.IsFavorite,
                    note.IsInbox,
                    fpProc,
                    fpWin,
                    fpUrl,
                    note.CapturedAt.HasValue ? SyncPackageExporter.NormalizeToUtc(note.CapturedAt.Value) : null,
                    false,
                    note.NoteTags.Where(nt => nt.Tag != null).Select(nt => (nt.Tag!.SyncId, nt.Origin, nt.IsSuppressed)),
                    isProt,
                    note.ProtectedFormatVersion,
                    note.ProtectedKdfIterations,
                    note.ProtectedSaltBase64,
                    note.ProtectedNonceBase64,
                    note.ProtectedTagBase64,
                    note.ProtectedCiphertextBase64,
                    isProt ? note.ProtectedKdfDescriptor : null,
                    isProt ? note.ProtectedOriginalSyncId : null);

                if (string.IsNullOrEmpty(state.ContentHash) || !string.Equals(state.ContentHash, noteHash, StringComparison.Ordinal))
                {
                    pendingIds.Add(note.SyncId);
                }
            }
        }

        return pendingIds;
    }

    /// <summary>
    /// Checks whether a specific note has un-uploaded local modifications compared to SyncEntityStates.
    /// </summary>
    public static bool IsNoteModifiedLocally(QuickNotesDbContext db, Note note, Guid localDeviceId)
    {
        var state = db.SyncEntityStates.AsNoTracking().FirstOrDefault(s => s.SyncId == note.SyncId);
        if (state == null)
        {
            return true;
        }

        bool noteDeleted = note.DeletedAt != null;
        if (state.IsDeleted != noteDeleted)
        {
            return true;
        }

        if (noteDeleted)
        {
            return false;
        }

        bool isProt = note.IsProtected;
        string fpTitle = isProt ? string.Empty : (note.Title ?? string.Empty);
        string fpText = isProt ? string.Empty : (note.Text ?? string.Empty);
        string? fpProc = isProt ? null : note.SourceProcessName;
        string? fpWin = isProt ? null : note.SourceWindowTitle;
        string? fpUrl = isProt ? null : note.SourceUrl;

        var noteTags = db.Entry(note).Collection(n => n.NoteTags).IsLoaded
            ? note.NoteTags
            : db.NoteTags.Include(nt => nt.Tag).Where(nt => nt.NoteId == note.Id).ToList();

        string noteHash = SyncFingerprintHelper.ComputeNoteFingerprint(
            fpTitle,
            fpText,
            note.IsPinned,
            note.IsFavorite,
            note.IsInbox,
            fpProc,
            fpWin,
            fpUrl,
            note.CapturedAt.HasValue ? SyncPackageExporter.NormalizeToUtc(note.CapturedAt.Value) : null,
            false,
            noteTags.Where(nt => nt.Tag != null).Select(nt => (nt.Tag!.SyncId, nt.Origin, nt.IsSuppressed)),
            isProt,
            note.ProtectedFormatVersion,
            note.ProtectedKdfIterations,
            note.ProtectedSaltBase64,
            note.ProtectedNonceBase64,
            note.ProtectedTagBase64,
            note.ProtectedCiphertextBase64,
            isProt ? note.ProtectedKdfDescriptor : null,
            isProt ? note.ProtectedOriginalSyncId : null);

        return string.IsNullOrEmpty(state.ContentHash) || !string.Equals(state.ContentHash, noteHash, StringComparison.Ordinal);
    }

    /// <summary>
    /// Evaluates the compact publication status for a specific note.
    /// </summary>
    public static LocalCommitSyncStatus GetNotePublicationStatus(
        QuickNotesDbContext db,
        Note note,
        Guid localDeviceId,
        SyncCloudSettings? settings,
        bool hasConflict = false,
        bool isSyncing = false,
        bool isError = false)
    {
        if (settings == null || !settings.Enabled || string.IsNullOrWhiteSpace(settings.Bucket))
        {
            return LocalCommitSyncStatus.SavedLocally;
        }

        if (hasConflict)
        {
            return LocalCommitSyncStatus.Conflict;
        }

        if (isSyncing)
        {
            return LocalCommitSyncStatus.Syncing;
        }

        bool isModified = IsNoteModifiedLocally(db, note, localDeviceId);
        if (isModified)
        {
            return isError ? LocalCommitSyncStatus.Error : LocalCommitSyncStatus.PendingUpload;
        }

        if (isError)
        {
            return LocalCommitSyncStatus.Error;
        }

        return LocalCommitSyncStatus.Synchronized;
    }

    /// <summary>
    /// Registers local modifications into SyncEntityStates with new local revisions.
    /// Executed before pull so that if remote packages contain concurrent modifications to the same
    /// entities, branch conflicts are detected and local changes are never overwritten.
    /// </summary>
    public static bool RegisterLocalModifications(QuickNotesDbContext db, Guid localDeviceId, IDateTimeProvider? dateTimeProvider = null)
    {
        var nowUtc = SyncPackageExporter.EnsureUtcKind((dateTimeProvider ?? new SystemDateTimeProvider()).UtcNow);
        var syncStates = db.SyncEntityStates.ToDictionary(s => s.SyncId);
        bool hasChanges = false;

        // 1. Tags
        var tags = db.Tags
            .Include(t => t.Synonyms)
            .Include(t => t.ParentTag)
            .ToList();

        var activeTagSyncIds = new HashSet<Guid>();
        foreach (var tag in tags)
        {
            activeTagSyncIds.Add(tag.SyncId);
            Guid? parentTagSyncId = tag.ParentTag?.SyncId ?? (tag.ParentTagId.HasValue ? tags.FirstOrDefault(t => t.Id == tag.ParentTagId.Value)?.SyncId : null);
            string currentHash = SyncFingerprintHelper.ComputeTagFingerprint(tag.Name, parentTagSyncId, tag.Synonyms.Select(s => s.Value));

            if (!syncStates.TryGetValue(tag.SyncId, out var state))
            {
                state = new SyncEntityState
                {
                    SyncId = tag.SyncId,
                    EntityType = "Tag",
                    RevisionId = Guid.NewGuid(),
                    ParentRevisionId = null,
                    DeviceId = localDeviceId,
                    UpdatedAtUtc = nowUtc,
                    IsDeleted = false,
                    ContentHash = currentHash
                };
                db.SyncEntityStates.Add(state);
                syncStates[tag.SyncId] = state;
                hasChanges = true;
            }
            else
            {
                bool changed = string.IsNullOrEmpty(state.ContentHash) ||
                               !string.Equals(state.ContentHash, currentHash, StringComparison.Ordinal) ||
                               state.IsDeleted;

                if (changed)
                {
                    state.ParentRevisionId = state.RevisionId;
                    state.RevisionId = Guid.NewGuid();
                    state.DeviceId = localDeviceId;
                    state.UpdatedAtUtc = nowUtc;
                    state.ContentHash = currentHash;
                    state.IsDeleted = false;
                    state.DeletedAtUtc = null;
                    hasChanges = true;
                }
            }
        }

        string tagTombstoneHash = SyncFingerprintHelper.ComputeTombstoneFingerprint("Tag");
        foreach (var state in syncStates.Values.Where(s => s.EntityType == "Tag"))
        {
            if (!activeTagSyncIds.Contains(state.SyncId))
            {
                bool changed = !state.IsDeleted || !string.Equals(state.ContentHash, tagTombstoneHash, StringComparison.Ordinal);
                if (changed)
                {
                    state.ParentRevisionId = state.RevisionId;
                    state.RevisionId = Guid.NewGuid();
                    state.IsDeleted = true;
                    state.DeletedAtUtc = state.DeletedAtUtc ?? nowUtc;
                    state.UpdatedAtUtc = nowUtc;
                    state.DeviceId = localDeviceId;
                    state.ContentHash = tagTombstoneHash;
                    hasChanges = true;
                }
            }
        }

        // 2. Notes
        var notes = db.Notes
            .Include(n => n.NoteTags)
            .ThenInclude(nt => nt.Tag)
            .ToList();

        var activeNoteSyncIds = new HashSet<Guid>();
        string noteTombstoneHash = SyncFingerprintHelper.ComputeTombstoneFingerprint("Note");

        foreach (var note in notes)
        {
            activeNoteSyncIds.Add(note.SyncId);
            bool isNoteDeleted = note.DeletedAt != null;
            bool isProt = note.IsProtected;
            string fpTitle = isProt ? string.Empty : (note.Title ?? string.Empty);
            string fpText = isProt ? string.Empty : (note.Text ?? string.Empty);
            string? fpProc = isProt ? null : note.SourceProcessName;
            string? fpWin = isProt ? null : note.SourceWindowTitle;
            string? fpUrl = isProt ? null : note.SourceUrl;

            string currentHash = isNoteDeleted
                ? noteTombstoneHash
                : SyncFingerprintHelper.ComputeNoteFingerprint(
                    fpTitle,
                    fpText,
                    note.IsPinned,
                    note.IsFavorite,
                    note.IsInbox,
                    fpProc,
                    fpWin,
                    fpUrl,
                    note.CapturedAt.HasValue ? SyncPackageExporter.NormalizeToUtc(note.CapturedAt.Value) : null,
                    false,
                    note.NoteTags.Where(nt => nt.Tag != null).Select(nt => (nt.Tag!.SyncId, nt.Origin, nt.IsSuppressed)),
                    isProt,
                    note.ProtectedFormatVersion,
                    note.ProtectedKdfIterations,
                    note.ProtectedSaltBase64,
                    note.ProtectedNonceBase64,
                    note.ProtectedTagBase64,
                    note.ProtectedCiphertextBase64,
                    isProt ? note.ProtectedKdfDescriptor : null,
                    isProt ? note.ProtectedOriginalSyncId : null);

            if (!syncStates.TryGetValue(note.SyncId, out var state))
            {
                state = new SyncEntityState
                {
                    SyncId = note.SyncId,
                    EntityType = "Note",
                    RevisionId = Guid.NewGuid(),
                    ParentRevisionId = null,
                    DeviceId = localDeviceId,
                    UpdatedAtUtc = nowUtc,
                    IsDeleted = isNoteDeleted,
                    DeletedAtUtc = isNoteDeleted ? (note.DeletedAt.HasValue ? SyncPackageExporter.NormalizeToUtc(note.DeletedAt.Value) : nowUtc) : null,
                    ContentHash = currentHash
                };
                db.SyncEntityStates.Add(state);
                syncStates[note.SyncId] = state;
                hasChanges = true;
            }
            else
            {
                bool changed = string.IsNullOrEmpty(state.ContentHash) ||
                               !string.Equals(state.ContentHash, currentHash, StringComparison.Ordinal) ||
                               state.IsDeleted != isNoteDeleted;

                if (changed)
                {
                    state.ParentRevisionId = state.RevisionId;
                    state.RevisionId = Guid.NewGuid();
                    state.DeviceId = localDeviceId;
                    state.UpdatedAtUtc = nowUtc;
                    state.ContentHash = currentHash;
                    state.IsDeleted = isNoteDeleted;
                    state.DeletedAtUtc = isNoteDeleted ? (note.DeletedAt.HasValue ? SyncPackageExporter.NormalizeToUtc(note.DeletedAt.Value) : nowUtc) : null;
                    hasChanges = true;
                }
            }
        }

        foreach (var state in syncStates.Values.Where(s => s.EntityType == "Note"))
        {
            if (!activeNoteSyncIds.Contains(state.SyncId))
            {
                bool changed = !state.IsDeleted || !string.Equals(state.ContentHash, noteTombstoneHash, StringComparison.Ordinal);
                if (changed)
                {
                    state.ParentRevisionId = state.RevisionId;
                    state.RevisionId = Guid.NewGuid();
                    state.IsDeleted = true;
                    state.DeletedAtUtc = state.DeletedAtUtc ?? nowUtc;
                    state.UpdatedAtUtc = nowUtc;
                    state.DeviceId = localDeviceId;
                    state.ContentHash = noteTombstoneHash;
                    hasChanges = true;
                }
            }
        }

        // 3. Templates
        var templates = db.NoteTemplates
            .Include(t => t.TemplateTags)
            .ThenInclude(tt => tt.Tag)
            .ToList();

        var activeTmplSyncIds = new HashSet<Guid>();
        string tmplTombstoneHash = SyncFingerprintHelper.ComputeTombstoneFingerprint("NoteTemplate");

        foreach (var tmpl in templates)
        {
            activeTmplSyncIds.Add(tmpl.SyncId);
            string currentHash = SyncFingerprintHelper.ComputeTemplateFingerprint(
                tmpl.Title,
                tmpl.Text,
                tmpl.TemplateTags.Where(tt => tt.Tag != null).Select(tt => tt.Tag!.SyncId));

            if (!syncStates.TryGetValue(tmpl.SyncId, out var state))
            {
                state = new SyncEntityState
                {
                    SyncId = tmpl.SyncId,
                    EntityType = "NoteTemplate",
                    RevisionId = Guid.NewGuid(),
                    ParentRevisionId = null,
                    DeviceId = localDeviceId,
                    UpdatedAtUtc = nowUtc,
                    IsDeleted = false,
                    ContentHash = currentHash
                };
                db.SyncEntityStates.Add(state);
                syncStates[tmpl.SyncId] = state;
                hasChanges = true;
            }
            else
            {
                bool changed = string.IsNullOrEmpty(state.ContentHash) ||
                               !string.Equals(state.ContentHash, currentHash, StringComparison.Ordinal) ||
                               state.IsDeleted;

                if (changed)
                {
                    state.ParentRevisionId = state.RevisionId;
                    state.RevisionId = Guid.NewGuid();
                    state.DeviceId = localDeviceId;
                    state.UpdatedAtUtc = nowUtc;
                    state.ContentHash = currentHash;
                    state.IsDeleted = false;
                    state.DeletedAtUtc = null;
                    hasChanges = true;
                }
            }
        }

        foreach (var state in syncStates.Values.Where(s => s.EntityType == "NoteTemplate"))
        {
            if (!activeTmplSyncIds.Contains(state.SyncId))
            {
                bool changed = !state.IsDeleted || !string.Equals(state.ContentHash, tmplTombstoneHash, StringComparison.Ordinal);
                if (changed)
                {
                    state.ParentRevisionId = state.RevisionId;
                    state.RevisionId = Guid.NewGuid();
                    state.IsDeleted = true;
                    state.DeletedAtUtc = state.DeletedAtUtc ?? nowUtc;
                    state.UpdatedAtUtc = nowUtc;
                    state.DeviceId = localDeviceId;
                    state.ContentHash = tmplTombstoneHash;
                    hasChanges = true;
                }
            }
        }

        // 4. Attachments
        var attachments = db.NoteAttachments
            .Include(a => a.Note)
            .ToList();

        var activeAttSyncIds = new HashSet<Guid>();
        string attTombstoneHash = SyncFingerprintHelper.ComputeTombstoneFingerprint("NoteAttachment");

        foreach (var att in attachments)
        {
            activeAttSyncIds.Add(att.SyncId);
            string currentHash = SyncFingerprintHelper.ComputeAttachmentFingerprint(
                att.Note?.SyncId ?? Guid.Empty,
                att.OriginalFileName,
                att.ContentType,
                att.Size,
                att.Sha256,
                att.IsProtected,
                att.ProtectedFormatVersion,
                att.ProtectedKdfIterations,
                att.ProtectedSaltBase64,
                att.ProtectedNonceBase64,
                att.ProtectedTagBase64,
                att.ProtectedCiphertextBase64,
                att.IsProtected ? att.ProtectedKdfDescriptor : null);

            if (!syncStates.TryGetValue(att.SyncId, out var state))
            {
                state = new SyncEntityState
                {
                    SyncId = att.SyncId,
                    EntityType = "NoteAttachment",
                    RevisionId = Guid.NewGuid(),
                    ParentRevisionId = null,
                    DeviceId = localDeviceId,
                    UpdatedAtUtc = nowUtc,
                    IsDeleted = false,
                    ContentHash = currentHash
                };
                db.SyncEntityStates.Add(state);
                syncStates[att.SyncId] = state;
                hasChanges = true;
            }
            else
            {
                bool changed = string.IsNullOrEmpty(state.ContentHash) ||
                               !string.Equals(state.ContentHash, currentHash, StringComparison.Ordinal) ||
                               state.IsDeleted;

                if (changed)
                {
                    state.ParentRevisionId = state.RevisionId;
                    state.RevisionId = Guid.NewGuid();
                    state.DeviceId = localDeviceId;
                    state.UpdatedAtUtc = nowUtc;
                    state.ContentHash = currentHash;
                    state.IsDeleted = false;
                    state.DeletedAtUtc = null;
                    hasChanges = true;
                }
            }
        }

        foreach (var state in syncStates.Values.Where(s => s.EntityType == "NoteAttachment"))
        {
            if (!activeAttSyncIds.Contains(state.SyncId))
            {
                bool changed = !state.IsDeleted || !string.Equals(state.ContentHash, attTombstoneHash, StringComparison.Ordinal);
                if (changed)
                {
                    state.ParentRevisionId = state.RevisionId;
                    state.RevisionId = Guid.NewGuid();
                    state.IsDeleted = true;
                    state.DeletedAtUtc = state.DeletedAtUtc ?? nowUtc;
                    state.UpdatedAtUtc = nowUtc;
                    state.DeviceId = localDeviceId;
                    state.ContentHash = attTombstoneHash;
                    hasChanges = true;
                }
            }
        }

        if (hasChanges)
        {
            db.SaveChanges();
        }

        return hasChanges;
    }
}
