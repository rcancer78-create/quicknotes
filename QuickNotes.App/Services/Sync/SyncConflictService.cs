using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Helpers;
using QuickNotes.App.Models;
using QuickNotes.App.Models.Sync;

namespace QuickNotes.App.Services.Sync;

public class SyncConflictService : ISyncConflictService
{
    private readonly Func<QuickNotesDbContext> _contextFactory;
    private readonly IDeviceIdProvider _deviceIdProvider;
    private readonly INoteHistoryService? _historyService;
    private readonly ILocalMutationCoordinator _mutationCoordinator;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public Action<string>? FailureInjectionHook { get; set; }

    public SyncConflictService(
        Func<QuickNotesDbContext> contextFactory,
        IDeviceIdProvider deviceIdProvider,
        INoteHistoryService? historyService = null,
        ILocalMutationCoordinator? mutationCoordinator = null)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _deviceIdProvider = deviceIdProvider ?? throw new ArgumentNullException(nameof(deviceIdProvider));
        _historyService = historyService;
        _mutationCoordinator = mutationCoordinator ?? new LocalMutationCoordinator();
    }

    internal ILocalMutationCoordinator MutationCoordinator => _mutationCoordinator;

    public async Task<List<SyncConflictRecord>> GetUnresolvedConflictsAsync(CancellationToken ct = default)
    {
        using var db = _contextFactory();
        return await db.SyncConflicts
            .AsNoTracking()
            .Where(c => !c.IsResolved)
            .OrderByDescending(c => c.DetectedAtUtc)
            .ToListAsync(ct);
    }

    public async Task<int> GetUnresolvedConflictsCountAsync(CancellationToken ct = default)
    {
        using var db = _contextFactory();
        return await db.SyncConflicts
            .AsNoTracking()
            .CountAsync(c => !c.IsResolved, ct);
    }

    public async Task<SyncConflictDetail?> GetConflictDetailAsync(int conflictId, CancellationToken ct = default)
    {
        using var db = _contextFactory();
        var conflict = await db.SyncConflicts
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == conflictId, ct);

        if (conflict == null) return null;

        Guid localDeviceId = _deviceIdProvider.GetDeviceId();
        var detail = new SyncConflictDetail
        {
            ConflictId = conflict.Id,
            SyncId = conflict.SyncId,
            EntityType = conflict.EntityType,
            DetectedAtUtc = conflict.DetectedAtUtc,
            SourceDeviceId = conflict.SourceDeviceId,
            SourcePackageId = conflict.SourcePackageId,
            Reason = conflict.Reason,
            ReasonDisplay = string.IsNullOrWhiteSpace(conflict.Reason) ? "Причина конфликта не указана." : conflict.Reason,
            IsResolved = conflict.IsResolved,
            LocalDeviceId = localDeviceId,
            RemoteDeviceId = conflict.SourceDeviceId,
            LocalDeviceDisplay = FormatDeviceDisplay(localDeviceId, isLocal: true),
            RemoteDeviceDisplay = FormatDeviceDisplay(conflict.SourceDeviceId, isLocal: false)
        };

        // 1. Parse Remote snapshot safely
        if (!string.IsNullOrWhiteSpace(conflict.RemoteDataJson))
        {
            try
            {
                if (conflict.EntityType == "Note")
                {
                    var remoteDto = JsonSerializer.Deserialize<SyncNoteDto>(conflict.RemoteDataJson, JsonOptions);
                    if (remoteDto != null)
                    {
                        detail.RemoteIsProtected = remoteDto.IsProtected;
                        detail.RemoteIsPinned = remoteDto.IsPinned;
                        detail.RemoteIsFavorite = remoteDto.IsFavorite;
                        detail.RemoteIsInbox = remoteDto.IsInbox;
                        detail.RemoteIsDeleted = remoteDto.Operation == SyncOperationType.Delete;
                        detail.RemoteUpdatedAtUtc = remoteDto.UpdatedAtUtc == default ? null : remoteDto.UpdatedAtUtc;
                        if (remoteDto.DeviceId != Guid.Empty)
                        {
                            detail.RemoteDeviceId = remoteDto.DeviceId;
                            detail.RemoteDeviceDisplay = FormatDeviceDisplay(remoteDto.DeviceId, isLocal: false);
                        }

                        if (remoteDto.IsProtected)
                        {
                            detail.RemoteTitle = string.Empty;
                            detail.RemoteText = string.Empty;
                            detail.RemoteProtectionNotice = "Защищённая заметка: текст скрыт. Нужен штатный unlock, фиктивный plaintext не создаётся.";
                        }
                        else
                        {
                            detail.RemoteText = remoteDto.Text;
                            detail.RemoteTitle = NoteTitleHelper.GetDisplayTitle(remoteDto.Title, remoteDto.Text);
                        }

                        if (remoteDto.Tags != null && remoteDto.Tags.Count > 0 && !remoteDto.IsProtected)
                        {
                            var tagSyncIds = remoteDto.Tags.Select(t => t.TagSyncId).ToList();
                            var tags = await db.Tags.AsNoTracking().Where(t => tagSyncIds.Contains(t.SyncId)).ToListAsync(ct);
                            detail.RemoteTags = tags.Select(t => t.Name).ToList();
                        }
                    }
                }
                else if (conflict.EntityType == "Tag")
                {
                    var remoteTag = JsonSerializer.Deserialize<SyncTagDto>(conflict.RemoteDataJson, JsonOptions);
                    if (remoteTag != null)
                    {
                        detail.RemoteTitle = remoteTag.Name;
                        detail.RemoteText = $"Тег: {remoteTag.Name}" + (remoteTag.Synonyms.Count > 0 ? $"\nСинонимы: {string.Join(", ", remoteTag.Synonyms)}" : "");
                        detail.RemoteIsDeleted = remoteTag.Operation == SyncOperationType.Delete;
                        detail.RemoteUpdatedAtUtc = remoteTag.UpdatedAtUtc;
                    }
                }
                else if (conflict.EntityType == "NoteTemplate")
                {
                    var remoteTmpl = JsonSerializer.Deserialize<SyncTemplateDto>(conflict.RemoteDataJson, JsonOptions);
                    if (remoteTmpl != null)
                    {
                        detail.RemoteTitle = remoteTmpl.Title;
                        detail.RemoteText = remoteTmpl.Text;
                        detail.RemoteIsDeleted = remoteTmpl.Operation == SyncOperationType.Delete;
                        detail.RemoteUpdatedAtUtc = remoteTmpl.UpdatedAtUtc;
                    }
                }
                else if (conflict.EntityType == "NoteAttachment")
                {
                    var remoteAtt = JsonSerializer.Deserialize<SyncAttachmentDto>(conflict.RemoteDataJson, JsonOptions);
                    if (remoteAtt != null)
                    {
                        detail.RemoteTitle = remoteAtt.OriginalFileName;
                        detail.RemoteText = $"Файл: {remoteAtt.OriginalFileName}\nТип: {remoteAtt.ContentType}\nРазмер: {remoteAtt.Size} байт\nSHA-256: {remoteAtt.Sha256}";
                        detail.RemoteIsDeleted = remoteAtt.Operation == SyncOperationType.Delete;
                        detail.RemoteUpdatedAtUtc = remoteAtt.UpdatedAtUtc;
                    }
                }
                else
                {
                    detail.RemoteText = conflict.RemoteDataJson;
                }
            }
            catch (Exception ex)
            {
                detail.IsRemoteCorrupted = true;
                detail.RemoteCorruptionError = ex.Message;
                detail.RemoteText = string.Empty;
                detail.RemoteTitle = string.Empty;
            }
        }
        else
        {
            detail.IsRemoteCorrupted = true;
            detail.RemoteCorruptionError = "Снимок удалённой версии отсутствует.";
            detail.RemoteText = string.Empty;
        }

        // 2. Parse Local snapshot safely (with live DB augmentation)
        var localNote = conflict.EntityType == "Note"
            ? await db.Notes.AsNoTracking()
                .Include(n => n.NoteTags).ThenInclude(nt => nt.Tag)
                .Include(n => n.Attachments)
                .FirstOrDefaultAsync(n => n.SyncId == conflict.SyncId, ct)
            : null;

        if (localNote != null)
        {
            detail.LocalIsProtected = localNote.IsProtected;
            detail.LocalIsPinned = localNote.IsPinned;
            detail.LocalIsFavorite = localNote.IsFavorite;
            detail.LocalIsInbox = localNote.IsInbox;
            detail.LocalIsDeleted = localNote.DeletedAt.HasValue;
            detail.LocalUpdatedAtUtc = SyncPackageExporter.NormalizeToUtc(localNote.UpdatedAt);
            if (localNote.IsProtected)
            {
                detail.LocalTitle = string.Empty;
                detail.LocalText = string.Empty;
                detail.LocalTags = new List<string>();
                detail.LocalProtectionNotice = "Защищённая заметка: текст скрыт. Нужен штатный unlock, фиктивный plaintext не создаётся.";
                detail.LocalAttachmentNames = localNote.Attachments.Count == 0
                    ? new List<string>()
                    : new List<string> { $"{localNote.Attachments.Count} защищённых вложений (имена скрыты)" };
            }
            else
            {
                detail.LocalText = localNote.Text;
                detail.LocalTitle = NoteTitleHelper.GetDisplayTitle(localNote.Title, localNote.Text);
                detail.LocalTags = localNote.NoteTags.Where(nt => nt.Tag != null).Select(nt => nt.Tag!.Name).ToList();
                detail.LocalAttachmentNames = localNote.Attachments.Select(a => a.OriginalFileName).ToList();
            }
        }
        else if (!string.IsNullOrWhiteSpace(conflict.LocalDataJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(conflict.LocalDataJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("Text", out var textEl)) detail.LocalText = textEl.GetString();
                if (root.TryGetProperty("IsPinned", out var pinEl)) detail.LocalIsPinned = pinEl.GetBoolean();
                if (root.TryGetProperty("IsFavorite", out var favEl)) detail.LocalIsFavorite = favEl.GetBoolean();
                if (root.TryGetProperty("IsInbox", out var inbEl)) detail.LocalIsInbox = inbEl.GetBoolean();
                if (root.TryGetProperty("IsDeleted", out var delEl)) detail.LocalIsDeleted = delEl.GetBoolean();
                string? parsedLocalTitle = null;
                if (root.TryGetProperty("Title", out var titleEl)) parsedLocalTitle = titleEl.GetString();
                if (root.TryGetProperty("IsProtected", out var protEl) && protEl.GetBoolean())
                {
                    detail.LocalIsProtected = true;
                    detail.LocalTitle = string.Empty;
                    detail.LocalText = string.Empty;
                    detail.LocalProtectionNotice = "Защищённая заметка: текст скрыт. Нужен штатный unlock, фиктивный plaintext не создаётся.";
                }
                else
                {
                    detail.LocalTitle = NoteTitleHelper.GetDisplayTitle(parsedLocalTitle, detail.LocalText ?? string.Empty);
                }
                if (root.TryGetProperty("UpdatedAtUtc", out var updEl) && updEl.TryGetDateTime(out var upd))
                {
                    detail.LocalUpdatedAtUtc = upd;
                }
            }
            catch (Exception ex)
            {
                detail.IsLocalCorrupted = true;
                detail.LocalCorruptionError = ex.Message;
                detail.LocalText = string.Empty;
                detail.LocalTitle = string.Empty;
            }
        }
        else
        {
            detail.LocalText = "(локальная запись отсутствует)";
        }

        if (detail.IsRemoteCorrupted)
        {
            detail.RemoteText = string.Empty;
        }

        if (detail.IsLocalCorrupted)
        {
            detail.LocalText = string.Empty;
        }

        detail.LocalUpdatedDisplay = FormatTimeDisplay(detail.LocalUpdatedAtUtc);
        detail.RemoteUpdatedDisplay = FormatTimeDisplay(detail.RemoteUpdatedAtUtc);
        detail.ReasonDisplay = BuildReasonDisplay(detail);

        // 3. Draft initial merged text for Note
        if (detail.CanMerge)
        {
            detail.InitialMergedText = BuildInitialMergedDraft(detail.LocalText ?? string.Empty, detail.RemoteText ?? string.Empty);
        }

        return detail;
    }

    public Task<SyncConflictResolutionResult> ResolveKeepBothAsync(int conflictId, CancellationToken ct = default)
        => _mutationCoordinator.ExecuteBulkMutationAsync(() => ResolveKeepBothCoreAsync(conflictId, ct), ct);

    private async Task<SyncConflictResolutionResult> ResolveKeepBothCoreAsync(int conflictId, CancellationToken ct)
    {
        using var db = _contextFactory();
        var conflict = await db.SyncConflicts.FirstOrDefaultAsync(c => c.Id == conflictId, ct);
        if (conflict == null) return SyncConflictResolutionResult.Failure(conflictId, "Конфликт не найден.");
        if (conflict.IsResolved) return SyncConflictResolutionResult.Succeeded(conflictId, "KeepBoth");

        try
        {
            InvokePhase("validation");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return SyncConflictResolutionResult.Failure(conflictId, $"Сбой при разрешении конфликта: {ex.Message}");
        }

        var stale = await GetStaleReasonAsync(db, conflict, ct);
        if (stale != null) return SyncConflictResolutionResult.Failure(conflictId, stale);

        if (conflict.EntityType == "NoteAttachment")
        {
            return SyncConflictResolutionResult.Failure(conflictId, "Действие «Сохранить обе версии» не поддерживается для вложений.");
        }

        if (conflict.EntityType != "Note" && conflict.EntityType != "Tag" && conflict.EntityType != "NoteTemplate")
        {
            return SyncConflictResolutionResult.Failure(conflictId, $"Неподдерживаемый тип сущности: {conflict.EntityType}");
        }

        if (string.IsNullOrWhiteSpace(conflict.RemoteDataJson))
        {
            return SyncConflictResolutionResult.Failure(conflictId, "Данные удалённой версии отсутствуют.");
        }

        using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            Guid localDeviceId = _deviceIdProvider.GetDeviceId();

            if (conflict.EntityType == "Note")
            {
                SyncNoteDto? remoteDto;
                try
                {
                    remoteDto = JsonSerializer.Deserialize<SyncNoteDto>(conflict.RemoteDataJson, JsonOptions);
                }
                catch (Exception ex)
                {
                    await tx.RollbackAsync(ct);
                    return SyncConflictResolutionResult.Failure(conflictId, $"Не удалось разобрать данные удалённой версии заметки: {ex.Message}");
                }

                if (remoteDto == null)
                {
                    await tx.RollbackAsync(ct);
                    return SyncConflictResolutionResult.Failure(conflictId, "Данные удалённой версии заметки пусты.");
                }

                if (remoteDto.Operation == SyncOperationType.Delete)
                {
                    await tx.RollbackAsync(ct);
                    return SyncConflictResolutionResult.Failure(conflictId, "Невозможно сохранить обе версии: удалённая операция является удалением.");
                }

                var localNote = await db.Notes.Include(n => n.NoteTags).ThenInclude(nt => nt.Tag).FirstOrDefaultAsync(n => n.SyncId == conflict.SyncId, ct);
                if (localNote == null)
                {
                    await tx.RollbackAsync(ct);
                    return SyncConflictResolutionResult.Failure(conflictId, "Локальная заметка не найдена.");
                }

                string remoteText = remoteDto.IsProtected
                    ? string.Empty
                    : (remoteDto.Text ?? string.Empty);

                Guid copySyncId = SyncConflictIdentity.DeriveKeepBothSyncId(conflict.SyncId, conflict.RemoteRevisionId);
                Guid expectedCopyRevisionId = SyncConflictIdentity.DeriveRevisionId(copySyncId, conflict.RemoteRevisionId, "KeepBothCopy");
                var existingCopy = await db.Notes.FirstOrDefaultAsync(n => n.SyncId == copySyncId, ct);
                if (existingCopy != null)
                {
                    var copyState = await db.SyncEntityStates.AsNoTracking().FirstOrDefaultAsync(s => s.SyncId == copySyncId, ct);
                    if (!IsCompatibleKeepBothNoteCopy(existingCopy, remoteDto, copyState, expectedCopyRevisionId))
                    {
                        await tx.RollbackAsync(ct);
                        return SyncConflictResolutionResult.Failure(conflictId, "Существующая копия Keep Both не соответствует текущему конфликту. Обновите список и повторите.");
                    }

                    return await CompleteKeepBothFromExistingCopyAsync(db, tx, conflict, ct);
                }

                InvokePhase("entity");
                string copyTitle = string.Empty;
                if (!remoteDto.IsProtected)
                {
                    string baseTitle = string.IsNullOrWhiteSpace(remoteDto.Title)
                        ? NoteTitleHelper.DeriveTitleFromText(remoteText)
                        : remoteDto.Title.Trim();
                    if (string.IsNullOrWhiteSpace(baseTitle))
                    {
                        baseTitle = "Заметка";
                    }

                    copyTitle = await AllocateUniqueNoteTitleAsync(db, baseTitle + " (облако)", ct);
                }

                var newNote = new Note
                {
                    SyncId = copySyncId,
                    Title = copyTitle,
                    Text = remoteText,
                    CreatedAt = remoteDto.CreatedAtUtc.ToLocalTime(),
                    UpdatedAt = DateTime.Now,
                    IsPinned = remoteDto.IsPinned,
                    IsFavorite = remoteDto.IsFavorite,
                    IsInbox = remoteDto.IsInbox,
                    SourceProcessName = remoteDto.IsProtected ? null : remoteDto.SourceProcessName,
                    SourceWindowTitle = remoteDto.IsProtected ? null : remoteDto.SourceWindowTitle,
                    SourceUrl = remoteDto.IsProtected ? null : remoteDto.SourceUrl,
                    CapturedAt = remoteDto.CapturedAtUtc?.ToLocalTime(),
                    IsProtected = remoteDto.IsProtected,
                    ProtectedFormatVersion = remoteDto.IsProtected ? remoteDto.ProtectedFormatVersion : 0,
                    ProtectedKdfIterations = remoteDto.IsProtected ? remoteDto.ProtectedKdfIterations : 0,
                    ProtectedKdfDescriptor = remoteDto.IsProtected ? remoteDto.ProtectedKdfDescriptor : null,
                    ProtectedSaltBase64 = remoteDto.IsProtected ? remoteDto.ProtectedSaltBase64 : string.Empty,
                    ProtectedNonceBase64 = remoteDto.IsProtected ? remoteDto.ProtectedNonceBase64 : string.Empty,
                    ProtectedTagBase64 = remoteDto.IsProtected ? remoteDto.ProtectedTagBase64 : string.Empty,
                    ProtectedCiphertextBase64 = remoteDto.IsProtected ? remoteDto.ProtectedCiphertextBase64 : string.Empty,
                    ProtectedOriginalSyncId = remoteDto.IsProtected ? (remoteDto.ProtectedOriginalSyncId ?? remoteDto.SyncId) : null
                };

                if (remoteDto.Tags != null)
                {
                    foreach (var t in remoteDto.Tags)
                    {
                        var localTag = await db.Tags.FirstOrDefaultAsync(lt => lt.SyncId == t.TagSyncId, ct);
                        if (localTag != null)
                        {
                            newNote.NoteTags.Add(new NoteTag
                            {
                                Note = newNote,
                                Tag = localTag,
                                Origin = t.Origin,
                                IsSuppressed = t.IsSuppressed
                            });
                        }
                    }
                }

                db.Notes.Add(newNote);
                await db.SaveChangesAsync(ct);

                InvokePhase("history");
                if (_historyService != null)
                {
                    _historyService.SaveSnapshot(db, newNote);
                }

                InvokePhase("sync_state");
                Guid copyRevisionId = SyncConflictIdentity.DeriveRevisionId(newNote.SyncId, conflict.RemoteRevisionId, "KeepBothCopy");
                var newState = new SyncEntityState
                {
                    SyncId = newNote.SyncId,
                    EntityType = "Note",
                    RevisionId = copyRevisionId,
                    DeviceId = localDeviceId,
                    UpdatedAtUtc = DateTime.UtcNow,
                    IsDeleted = false,
                    ContentHash = SyncFingerprintHelper.ComputeNoteFingerprint(
                        newNote.IsProtected ? string.Empty : (newNote.Title ?? string.Empty),
                        newNote.Text,
                        newNote.IsPinned,
                        newNote.IsFavorite,
                        newNote.IsInbox,
                        newNote.SourceProcessName,
                        newNote.SourceWindowTitle,
                        newNote.SourceUrl,
                        newNote.CapturedAt.HasValue ? SyncPackageExporter.NormalizeToUtc(newNote.CapturedAt.Value) : null,
                        false,
                        newNote.NoteTags.Where(nt => nt.Tag != null).Select(nt => (nt.Tag!.SyncId, nt.Origin, nt.IsSuppressed)),
                        newNote.IsProtected,
                        newNote.ProtectedFormatVersion,
                        newNote.ProtectedKdfIterations,
                        newNote.ProtectedSaltBase64,
                        newNote.ProtectedNonceBase64,
                        newNote.ProtectedTagBase64,
                        newNote.ProtectedCiphertextBase64,
                        newNote.IsProtected ? newNote.ProtectedKdfDescriptor : null,
                        newNote.IsProtected ? newNote.ProtectedOriginalSyncId : null)
                };
                db.SyncEntityStates.Add(newState);

                // Update original note's revision lineage and ContentHash so local version is retained
                var state = await db.SyncEntityStates.FirstOrDefaultAsync(s => s.SyncId == conflict.SyncId, ct);
                if (state == null)
                {
                    state = new SyncEntityState { SyncId = conflict.SyncId, EntityType = "Note" };
                    db.SyncEntityStates.Add(state);
                }

                state.ParentRevisionId = conflict.RemoteRevisionId;
                state.RevisionId = Guid.NewGuid();
                state.DeviceId = localDeviceId;
                state.UpdatedAtUtc = DateTime.UtcNow;
                state.ContentHash = SyncFingerprintHelper.ComputeNoteFingerprint(
                    localNote.IsProtected ? string.Empty : (localNote.Title ?? string.Empty),
                    localNote.Text,
                    localNote.IsPinned,
                    localNote.IsFavorite,
                    localNote.IsInbox,
                    localNote.SourceProcessName,
                    localNote.SourceWindowTitle,
                    localNote.SourceUrl,
                    localNote.CapturedAt.HasValue ? SyncPackageExporter.NormalizeToUtc(localNote.CapturedAt.Value) : null,
                    localNote.DeletedAt.HasValue,
                    localNote.NoteTags.Where(nt => nt.Tag != null).Select(nt => (nt.Tag!.SyncId, nt.Origin, nt.IsSuppressed)),
                    localNote.IsProtected,
                    localNote.ProtectedFormatVersion,
                    localNote.ProtectedKdfIterations,
                    localNote.ProtectedSaltBase64,
                    localNote.ProtectedNonceBase64,
                    localNote.ProtectedTagBase64,
                    localNote.ProtectedCiphertextBase64,
                    localNote.IsProtected ? localNote.ProtectedKdfDescriptor : null,
                    localNote.IsProtected ? localNote.ProtectedOriginalSyncId : null);
            }
            else if (conflict.EntityType == "Tag")
            {
                SyncTagDto? remoteTag;
                try
                {
                    remoteTag = JsonSerializer.Deserialize<SyncTagDto>(conflict.RemoteDataJson, JsonOptions);
                }
                catch (Exception ex)
                {
                    await tx.RollbackAsync(ct);
                    return SyncConflictResolutionResult.Failure(conflictId, $"Не удалось разобрать данные удалённой версии тега: {ex.Message}");
                }

                if (remoteTag == null)
                {
                    await tx.RollbackAsync(ct);
                    return SyncConflictResolutionResult.Failure(conflictId, "Данные удалённой версии тега пусты.");
                }

                if (remoteTag.Operation == SyncOperationType.Delete)
                {
                    await tx.RollbackAsync(ct);
                    return SyncConflictResolutionResult.Failure(conflictId, "Невозможно сохранить обе версии: удалённая операция является удалением.");
                }

                var localTag = await db.Tags.Include(t => t.Synonyms).Include(t => t.ParentTag).FirstOrDefaultAsync(t => t.SyncId == conflict.SyncId, ct);
                if (localTag == null)
                {
                    await tx.RollbackAsync(ct);
                    return SyncConflictResolutionResult.Failure(conflictId, "Локальный тег не найден.");
                }

                Tag? parentTag = null;
                if (remoteTag.ParentTagSyncId.HasValue)
                {
                    parentTag = await db.Tags.FirstOrDefaultAsync(t => t.SyncId == remoteTag.ParentTagSyncId.Value, ct);
                    if (parentTag == null)
                    {
                        await tx.RollbackAsync(ct);
                        return SyncConflictResolutionResult.Failure(conflictId, $"Родительский тег {remoteTag.ParentTagSyncId.Value} не найден.");
                    }
                }

                Guid copyTagSyncId = SyncConflictIdentity.DeriveKeepBothSyncId(conflict.SyncId, conflict.RemoteRevisionId);
                Guid expectedTagRevisionId = SyncConflictIdentity.DeriveRevisionId(copyTagSyncId, conflict.RemoteRevisionId, "KeepBothCopy");
                var existingTagCopy = await db.Tags.Include(t => t.Synonyms).FirstOrDefaultAsync(t => t.SyncId == copyTagSyncId, ct);
                if (existingTagCopy != null)
                {
                    var copyState = await db.SyncEntityStates.AsNoTracking().FirstOrDefaultAsync(s => s.SyncId == copyTagSyncId, ct);
                    if (!IsCompatibleKeepBothTagCopy(existingTagCopy, remoteTag, copyState, expectedTagRevisionId))
                    {
                        await tx.RollbackAsync(ct);
                        return SyncConflictResolutionResult.Failure(conflictId, "Существующая копия Keep Both не соответствует текущему конфликту. Обновите список и повторите.");
                    }

                    return await CompleteKeepBothFromExistingCopyAsync(db, tx, conflict, ct);
                }

                string baseName = (remoteTag.Name ?? "Тег") + " (облако)";
                string uniqueName = baseName;
                int counter = 2;
                while (await db.Tags.AnyAsync(t => t.Name == uniqueName, ct))
                {
                    uniqueName = $"{baseName} {counter++}";
                }

                var newTag = new Tag
                {
                    SyncId = copyTagSyncId,
                    Name = uniqueName,
                    ParentTag = parentTag,
                    ParentTagId = parentTag?.Id
                };

                if (remoteTag.Synonyms != null)
                {
                    foreach (var syn in remoteTag.Synonyms)
                    {
                        newTag.Synonyms.Add(new TagSynonym { Value = syn, Tag = newTag });
                    }
                }

                db.Tags.Add(newTag);

                var newTagState = new SyncEntityState
                {
                    SyncId = newTag.SyncId,
                    EntityType = "Tag",
                    RevisionId = SyncConflictIdentity.DeriveRevisionId(newTag.SyncId, conflict.RemoteRevisionId, "KeepBothCopy"),
                    DeviceId = localDeviceId,
                    UpdatedAtUtc = DateTime.UtcNow,
                    IsDeleted = false,
                    ContentHash = SyncFingerprintHelper.ComputeTagFingerprint(newTag.Name, remoteTag.ParentTagSyncId, remoteTag.Synonyms)
                };
                db.SyncEntityStates.Add(newTagState);

                var state = await db.SyncEntityStates.FirstOrDefaultAsync(s => s.SyncId == conflict.SyncId, ct);
                if (state == null)
                {
                    state = new SyncEntityState { SyncId = conflict.SyncId, EntityType = "Tag" };
                    db.SyncEntityStates.Add(state);
                }

                state.ParentRevisionId = conflict.RemoteRevisionId;
                state.RevisionId = Guid.NewGuid();
                state.DeviceId = localDeviceId;
                state.UpdatedAtUtc = DateTime.UtcNow;
                Guid? pSyncId = localTag.ParentTag?.SyncId ?? (localTag.ParentTagId.HasValue ? (await db.Tags.FirstOrDefaultAsync(t => t.Id == localTag.ParentTagId.Value, ct))?.SyncId : null);
                state.ContentHash = SyncFingerprintHelper.ComputeTagFingerprint(localTag.Name, pSyncId, localTag.Synonyms.Select(s => s.Value));
            }
            else if (conflict.EntityType == "NoteTemplate")
            {
                SyncTemplateDto? remoteTmpl;
                try
                {
                    remoteTmpl = JsonSerializer.Deserialize<SyncTemplateDto>(conflict.RemoteDataJson, JsonOptions);
                }
                catch (Exception ex)
                {
                    await tx.RollbackAsync(ct);
                    return SyncConflictResolutionResult.Failure(conflictId, $"Не удалось разобрать данные удалённой версии шаблона: {ex.Message}");
                }

                if (remoteTmpl == null)
                {
                    await tx.RollbackAsync(ct);
                    return SyncConflictResolutionResult.Failure(conflictId, "Данные удалённой версии шаблона пусты.");
                }

                if (remoteTmpl.Operation == SyncOperationType.Delete)
                {
                    await tx.RollbackAsync(ct);
                    return SyncConflictResolutionResult.Failure(conflictId, "Невозможно сохранить обе версии: удалённая операция является удалением.");
                }

                var localTmpl = await db.NoteTemplates.Include(t => t.TemplateTags).ThenInclude(tt => tt.Tag).FirstOrDefaultAsync(t => t.SyncId == conflict.SyncId, ct);
                if (localTmpl == null)
                {
                    await tx.RollbackAsync(ct);
                    return SyncConflictResolutionResult.Failure(conflictId, "Локальный шаблон не найден.");
                }

                Guid copyTmplSyncId = SyncConflictIdentity.DeriveKeepBothSyncId(conflict.SyncId, conflict.RemoteRevisionId);
                Guid expectedTmplRevisionId = SyncConflictIdentity.DeriveRevisionId(copyTmplSyncId, conflict.RemoteRevisionId, "KeepBothCopy");
                var existingTmplCopy = await db.NoteTemplates.FirstOrDefaultAsync(t => t.SyncId == copyTmplSyncId, ct);
                if (existingTmplCopy != null)
                {
                    var copyState = await db.SyncEntityStates.AsNoTracking().FirstOrDefaultAsync(s => s.SyncId == copyTmplSyncId, ct);
                    if (!IsCompatibleKeepBothTemplateCopy(existingTmplCopy, remoteTmpl, copyState, expectedTmplRevisionId))
                    {
                        await tx.RollbackAsync(ct);
                        return SyncConflictResolutionResult.Failure(conflictId, "Существующая копия Keep Both не соответствует текущему конфликту. Обновите список и повторите.");
                    }

                    return await CompleteKeepBothFromExistingCopyAsync(db, tx, conflict, ct);
                }

                string baseTitle = (remoteTmpl.Title ?? "Шаблон") + " (облако)";
                string uniqueTitle = baseTitle;
                int counter = 2;
                while (await db.NoteTemplates.AnyAsync(t => t.Title == uniqueTitle, ct))
                {
                    uniqueTitle = $"{baseTitle} {counter++}";
                }

                var newTmpl = new NoteTemplate
                {
                    SyncId = copyTmplSyncId,
                    Title = uniqueTitle,
                    Text = remoteTmpl.Text ?? string.Empty,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                };

                if (remoteTmpl.TagSyncIds != null)
                {
                    foreach (var tagSyncId in remoteTmpl.TagSyncIds)
                    {
                        var tag = await db.Tags.FirstOrDefaultAsync(t => t.SyncId == tagSyncId, ct);
                        if (tag != null)
                        {
                            newTmpl.TemplateTags.Add(new NoteTemplateTag
                            {
                                Template = newTmpl,
                                Tag = tag
                            });
                        }
                    }
                }

                db.NoteTemplates.Add(newTmpl);

                var newTmplState = new SyncEntityState
                {
                    SyncId = newTmpl.SyncId,
                    EntityType = "NoteTemplate",
                    RevisionId = SyncConflictIdentity.DeriveRevisionId(newTmpl.SyncId, conflict.RemoteRevisionId, "KeepBothCopy"),
                    DeviceId = localDeviceId,
                    UpdatedAtUtc = DateTime.UtcNow,
                    IsDeleted = false,
                    ContentHash = SyncFingerprintHelper.ComputeTemplateFingerprint(
                        newTmpl.Title,
                        newTmpl.Text,
                        newTmpl.TemplateTags.Where(tt => tt.Tag != null).Select(tt => tt.Tag!.SyncId))
                };
                db.SyncEntityStates.Add(newTmplState);

                var state = await db.SyncEntityStates.FirstOrDefaultAsync(s => s.SyncId == conflict.SyncId, ct);
                if (state == null)
                {
                    state = new SyncEntityState { SyncId = conflict.SyncId, EntityType = "NoteTemplate" };
                    db.SyncEntityStates.Add(state);
                }

                state.ParentRevisionId = conflict.RemoteRevisionId;
                state.RevisionId = Guid.NewGuid();
                state.DeviceId = localDeviceId;
                state.UpdatedAtUtc = DateTime.UtcNow;
                state.ContentHash = SyncFingerprintHelper.ComputeTemplateFingerprint(
                    localTmpl.Title,
                    localTmpl.Text,
                    localTmpl.TemplateTags.Where(tt => tt.Tag != null).Select(tt => tt.Tag!.SyncId));
            }

            conflict.IsResolved = true;
            conflict.ResolvedAtUtc = DateTime.UtcNow;
            conflict.ResolutionAction = "KeepBoth";

            InvokePhase("conflict_row");
            InvokePhase("before_commit");

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return SyncConflictResolutionResult.Succeeded(conflictId, "KeepBoth");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await tx.RollbackAsync(ct);
            ErrorLogService.Write("SyncConflictService.ResolveKeepBoth", ex);
            return SyncConflictResolutionResult.Failure(conflictId, $"Сбой при разрешении конфликта: {ex.Message}");
        }
    }

    public Task<SyncConflictResolutionResult> ResolveKeepLocalAsync(int conflictId, CancellationToken ct = default)
        => _mutationCoordinator.ExecuteBulkMutationAsync(() => ResolveKeepLocalCoreAsync(conflictId, ct), ct);

    private async Task<SyncConflictResolutionResult> ResolveKeepLocalCoreAsync(int conflictId, CancellationToken ct)
    {
        using var db = _contextFactory();
        var conflict = await db.SyncConflicts.FirstOrDefaultAsync(c => c.Id == conflictId, ct);
        if (conflict == null) return SyncConflictResolutionResult.Failure(conflictId, "Конфликт не найден.");
        if (conflict.IsResolved) return SyncConflictResolutionResult.Succeeded(conflictId, "KeepLocal");

        try
        {
            InvokePhase("validation");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return SyncConflictResolutionResult.Failure(conflictId, $"Сбой при разрешении конфликта: {ex.Message}");
        }

        var staleKeepLocal = await GetStaleReasonAsync(db, conflict, ct);
        if (staleKeepLocal != null) return SyncConflictResolutionResult.Failure(conflictId, staleKeepLocal);

        using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            Guid localDeviceId = _deviceIdProvider.GetDeviceId();
            var state = await db.SyncEntityStates.FirstOrDefaultAsync(s => s.SyncId == conflict.SyncId, ct);

            if (conflict.EntityType == "Note")
            {
                var localNote = await db.Notes.Include(n => n.NoteTags).ThenInclude(nt => nt.Tag).FirstOrDefaultAsync(n => n.SyncId == conflict.SyncId, ct);
                if (localNote == null && (state == null || !state.IsDeleted))
                {
                    await tx.RollbackAsync(ct);
                    return SyncConflictResolutionResult.Failure(conflictId, "Локальная заметка не найдена.");
                }

                state ??= new SyncEntityState { SyncId = conflict.SyncId, EntityType = "Note" };
                if (db.Entry(state).State == EntityState.Detached) db.SyncEntityStates.Add(state);

                InvokePhase("entity");
                state.ParentRevisionId = conflict.RemoteRevisionId;
                state.RevisionId = SyncConflictIdentity.DeriveRevisionId(conflict.SyncId, conflict.RemoteRevisionId, "KeepLocal");
                state.DeviceId = localDeviceId;
                state.UpdatedAtUtc = DateTime.UtcNow;

                if (localNote == null || localNote.DeletedAt != null)
                {
                    state.IsDeleted = true;
                    state.DeletedAtUtc = localNote?.DeletedAt.HasValue == true ? SyncPackageExporter.NormalizeToUtc(localNote.DeletedAt.Value) : (state.DeletedAtUtc ?? DateTime.UtcNow);
                    state.ContentHash = SyncFingerprintHelper.ComputeTombstoneFingerprint("Note");
                }
                else
                {
                    state.IsDeleted = false;
                    state.DeletedAtUtc = null;
                    state.ContentHash = SyncFingerprintHelper.ComputeNoteFingerprint(
                        localNote.IsProtected ? string.Empty : (localNote.Title ?? string.Empty),
                        localNote.Text,
                        localNote.IsPinned,
                        localNote.IsFavorite,
                        localNote.IsInbox,
                        localNote.SourceProcessName,
                        localNote.SourceWindowTitle,
                        localNote.SourceUrl,
                        localNote.CapturedAt.HasValue ? SyncPackageExporter.NormalizeToUtc(localNote.CapturedAt.Value) : null,
                        false,
                        localNote.NoteTags.Where(nt => nt.Tag != null).Select(nt => (nt.Tag!.SyncId, nt.Origin, nt.IsSuppressed)),
                        localNote.IsProtected,
                        localNote.ProtectedFormatVersion,
                        localNote.ProtectedKdfIterations,
                        localNote.ProtectedSaltBase64,
                        localNote.ProtectedNonceBase64,
                        localNote.ProtectedTagBase64,
                        localNote.ProtectedCiphertextBase64,
                        localNote.IsProtected ? localNote.ProtectedKdfDescriptor : null,
                        localNote.IsProtected ? localNote.ProtectedOriginalSyncId : null);

                    InvokePhase("history");
                    _historyService?.SaveSnapshot(db, localNote);
                }
            }
            else if (conflict.EntityType == "Tag")
            {
                var localTag = await db.Tags.Include(t => t.Synonyms).Include(t => t.ParentTag).FirstOrDefaultAsync(t => t.SyncId == conflict.SyncId, ct);
                if (localTag == null && (state == null || !state.IsDeleted))
                {
                    await tx.RollbackAsync(ct);
                    return SyncConflictResolutionResult.Failure(conflictId, "Локальный тег не найден.");
                }

                state ??= new SyncEntityState { SyncId = conflict.SyncId, EntityType = "Tag" };
                if (db.Entry(state).State == EntityState.Detached) db.SyncEntityStates.Add(state);

                state.ParentRevisionId = conflict.RemoteRevisionId;
                state.RevisionId = Guid.NewGuid();
                state.DeviceId = localDeviceId;
                state.UpdatedAtUtc = DateTime.UtcNow;

                if (localTag == null)
                {
                    state.IsDeleted = true;
                    state.DeletedAtUtc = state.DeletedAtUtc ?? DateTime.UtcNow;
                    state.ContentHash = SyncFingerprintHelper.ComputeTombstoneFingerprint("Tag");
                }
                else
                {
                    state.IsDeleted = false;
                    state.DeletedAtUtc = null;
                    Guid? pSyncId = localTag.ParentTag?.SyncId ?? (localTag.ParentTagId.HasValue ? (await db.Tags.FirstOrDefaultAsync(t => t.Id == localTag.ParentTagId.Value, ct))?.SyncId : null);
                    state.ContentHash = SyncFingerprintHelper.ComputeTagFingerprint(localTag.Name, pSyncId, localTag.Synonyms.Select(s => s.Value));
                }
            }
            else if (conflict.EntityType == "NoteTemplate")
            {
                var localTmpl = await db.NoteTemplates.Include(t => t.TemplateTags).ThenInclude(tt => tt.Tag).FirstOrDefaultAsync(t => t.SyncId == conflict.SyncId, ct);
                if (localTmpl == null && (state == null || !state.IsDeleted))
                {
                    await tx.RollbackAsync(ct);
                    return SyncConflictResolutionResult.Failure(conflictId, "Локальный шаблон не найден.");
                }

                state ??= new SyncEntityState { SyncId = conflict.SyncId, EntityType = "NoteTemplate" };
                if (db.Entry(state).State == EntityState.Detached) db.SyncEntityStates.Add(state);

                state.ParentRevisionId = conflict.RemoteRevisionId;
                state.RevisionId = Guid.NewGuid();
                state.DeviceId = localDeviceId;
                state.UpdatedAtUtc = DateTime.UtcNow;

                if (localTmpl == null)
                {
                    state.IsDeleted = true;
                    state.DeletedAtUtc = state.DeletedAtUtc ?? DateTime.UtcNow;
                    state.ContentHash = SyncFingerprintHelper.ComputeTombstoneFingerprint("NoteTemplate");
                }
                else
                {
                    state.IsDeleted = false;
                    state.DeletedAtUtc = null;
                    state.ContentHash = SyncFingerprintHelper.ComputeTemplateFingerprint(
                        localTmpl.Title,
                        localTmpl.Text,
                        localTmpl.TemplateTags.Where(tt => tt.Tag != null).Select(tt => tt.Tag!.SyncId));
                }
            }
            else if (conflict.EntityType == "NoteAttachment")
            {
                var localAtt = await db.NoteAttachments.Include(a => a.Note).FirstOrDefaultAsync(a => a.SyncId == conflict.SyncId, ct);
                if (localAtt == null && (state == null || !state.IsDeleted))
                {
                    await tx.RollbackAsync(ct);
                    return SyncConflictResolutionResult.Failure(conflictId, "Локальное вложение не найдено.");
                }

                state ??= new SyncEntityState { SyncId = conflict.SyncId, EntityType = "NoteAttachment" };
                if (db.Entry(state).State == EntityState.Detached) db.SyncEntityStates.Add(state);

                state.ParentRevisionId = conflict.RemoteRevisionId;
                state.RevisionId = Guid.NewGuid();
                state.DeviceId = localDeviceId;
                state.UpdatedAtUtc = DateTime.UtcNow;

                if (localAtt == null)
                {
                    state.IsDeleted = true;
                    state.DeletedAtUtc = state.DeletedAtUtc ?? DateTime.UtcNow;
                    state.ContentHash = SyncFingerprintHelper.ComputeTombstoneFingerprint("NoteAttachment");
                }
                else
                {
                    state.IsDeleted = false;
                    state.DeletedAtUtc = null;
                    Guid nSyncId = localAtt.Note?.SyncId ?? (await db.Notes.FirstOrDefaultAsync(n => n.Id == localAtt.NoteId, ct))?.SyncId ?? Guid.Empty;
                    state.ContentHash = SyncFingerprintHelper.ComputeAttachmentFingerprint(
                        nSyncId,
                        localAtt.OriginalFileName,
                        localAtt.ContentType,
                        localAtt.Size,
                        localAtt.Sha256,
                        localAtt.IsProtected,
                        localAtt.ProtectedFormatVersion,
                        localAtt.ProtectedKdfIterations,
                        localAtt.ProtectedSaltBase64,
                        localAtt.ProtectedNonceBase64,
                        localAtt.ProtectedTagBase64,
                        localAtt.ProtectedCiphertextBase64,
                        localAtt.IsProtected ? localAtt.ProtectedKdfDescriptor : null);
                }
            }
            else
            {
                await tx.RollbackAsync(ct);
                return SyncConflictResolutionResult.Failure(conflictId, $"Неподдерживаемый тип сущности: {conflict.EntityType}");
            }

            conflict.IsResolved = true;
            conflict.ResolvedAtUtc = DateTime.UtcNow;
            conflict.ResolutionAction = "KeepLocal";

            InvokePhase("conflict_row");
            InvokePhase("before_commit");

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return SyncConflictResolutionResult.Succeeded(conflictId, "KeepLocal");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await tx.RollbackAsync(ct);
            ErrorLogService.Write("SyncConflictService.ResolveKeepLocal", ex);
            return SyncConflictResolutionResult.Failure(conflictId, $"Сбой при разрешении конфликта: {ex.Message}");
        }
    }

    public Task<SyncConflictResolutionResult> ResolveAcceptRemoteAsync(int conflictId, CancellationToken ct = default)
        => _mutationCoordinator.ExecuteBulkMutationAsync(() => ResolveAcceptRemoteCoreAsync(conflictId, ct), ct);

    private async Task<SyncConflictResolutionResult> ResolveAcceptRemoteCoreAsync(int conflictId, CancellationToken ct)
    {
        using var db = _contextFactory();
        var conflict = await db.SyncConflicts.FirstOrDefaultAsync(c => c.Id == conflictId, ct);
        if (conflict == null) return SyncConflictResolutionResult.Failure(conflictId, "Конфликт не найден.");
        if (conflict.IsResolved) return SyncConflictResolutionResult.Succeeded(conflictId, "AcceptRemote");

        try
        {
            InvokePhase("validation");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return SyncConflictResolutionResult.Failure(conflictId, $"Сбой при разрешении конфликта: {ex.Message}");
        }

        var staleRemote = await GetStaleReasonAsync(db, conflict, ct);
        if (staleRemote != null) return SyncConflictResolutionResult.Failure(conflictId, staleRemote);

        if (string.IsNullOrWhiteSpace(conflict.RemoteDataJson))
        {
            return SyncConflictResolutionResult.Failure(conflictId, "Данные удалённой версии отсутствуют.");
        }

        using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            if (conflict.EntityType == "Note")
            {
                SyncNoteDto? remoteDto;
                try
                {
                    remoteDto = JsonSerializer.Deserialize<SyncNoteDto>(conflict.RemoteDataJson, JsonOptions);
                }
                catch (Exception ex)
                {
                    await tx.RollbackAsync(ct);
                    return SyncConflictResolutionResult.Failure(conflictId, $"Не удалось разобрать данные удалённой версии заметки: {ex.Message}");
                }

                if (remoteDto == null)
                {
                    await tx.RollbackAsync(ct);
                    return SyncConflictResolutionResult.Failure(conflictId, "Данные удалённой версии заметки пусты.");
                }

                var localNote = await db.Notes.Include(n => n.NoteTags).FirstOrDefaultAsync(n => n.SyncId == conflict.SyncId, ct);
                var state = await db.SyncEntityStates.FirstOrDefaultAsync(s => s.SyncId == conflict.SyncId, ct);
                if (state == null)
                {
                    state = new SyncEntityState
                    {
                        SyncId = conflict.SyncId,
                        EntityType = "Note"
                    };
                    db.SyncEntityStates.Add(state);
                }

                if (remoteDto.Operation == SyncOperationType.Delete)
                {
                    if (localNote != null)
                    {
                        localNote.DeletedAt = remoteDto.UpdatedAtUtc.ToLocalTime();
                    }

                    state.RevisionId = conflict.RemoteRevisionId;
                    state.ParentRevisionId = conflict.ParentRevisionId;
                    state.DeviceId = conflict.SourceDeviceId;
                    state.UpdatedAtUtc = remoteDto.UpdatedAtUtc;
                    state.IsDeleted = true;
                    state.DeletedAtUtc = remoteDto.UpdatedAtUtc;
                    state.ContentHash = SyncFingerprintHelper.ComputeTombstoneFingerprint("Note");
                }
                else
                {
                    if (localNote == null)
                    {
                        localNote = new Note
                        {
                            SyncId = conflict.SyncId,
                            CreatedAt = remoteDto.CreatedAtUtc.ToLocalTime()
                        };
                        db.Notes.Add(localNote);
                    }

                    localNote.Title = remoteDto.IsProtected ? string.Empty : (remoteDto.Title ?? string.Empty);
                    localNote.Text = remoteDto.IsProtected ? string.Empty : remoteDto.Text;
                    localNote.IsPinned = remoteDto.IsPinned;
                    localNote.IsFavorite = remoteDto.IsFavorite;
                    localNote.IsInbox = remoteDto.IsInbox;
                    localNote.SourceProcessName = remoteDto.IsProtected ? null : remoteDto.SourceProcessName;
                    localNote.SourceWindowTitle = remoteDto.IsProtected ? null : remoteDto.SourceWindowTitle;
                    localNote.SourceUrl = remoteDto.IsProtected ? null : remoteDto.SourceUrl;
                    localNote.CapturedAt = remoteDto.CapturedAtUtc?.ToLocalTime();
                    localNote.UpdatedAt = remoteDto.UpdatedAtUtc.ToLocalTime();
                    localNote.DeletedAt = null;
                    localNote.IsProtected = remoteDto.IsProtected;
                    localNote.ProtectedFormatVersion = remoteDto.IsProtected ? remoteDto.ProtectedFormatVersion : 0;
                    localNote.ProtectedKdfIterations = remoteDto.IsProtected ? remoteDto.ProtectedKdfIterations : 0;
                    localNote.ProtectedKdfDescriptor = remoteDto.IsProtected ? remoteDto.ProtectedKdfDescriptor : null;
                    localNote.ProtectedSaltBase64 = remoteDto.IsProtected ? remoteDto.ProtectedSaltBase64 : string.Empty;
                    localNote.ProtectedNonceBase64 = remoteDto.IsProtected ? remoteDto.ProtectedNonceBase64 : string.Empty;
                    localNote.ProtectedTagBase64 = remoteDto.IsProtected ? remoteDto.ProtectedTagBase64 : string.Empty;
                    localNote.ProtectedCiphertextBase64 = remoteDto.IsProtected ? remoteDto.ProtectedCiphertextBase64 : string.Empty;
                    localNote.ProtectedOriginalSyncId = remoteDto.IsProtected ? remoteDto.ProtectedOriginalSyncId : null;

                    // Reconcile tags
                    localNote.NoteTags.Clear();
                    if (remoteDto.Tags != null)
                    {
                        foreach (var t in remoteDto.Tags)
                        {
                            var localTag = await db.Tags.FirstOrDefaultAsync(lt => lt.SyncId == t.TagSyncId, ct);
                            if (localTag != null)
                            {
                                localNote.NoteTags.Add(new NoteTag
                                {
                                    Note = localNote,
                                    Tag = localTag,
                                    Origin = t.Origin,
                                    IsSuppressed = t.IsSuppressed
                                });
                            }
                        }
                    }

                    if (_historyService != null)
                    {
                        _historyService.SaveSnapshot(db, localNote);
                    }

                    state.RevisionId = conflict.RemoteRevisionId;
                    state.ParentRevisionId = conflict.ParentRevisionId;
                    state.DeviceId = conflict.SourceDeviceId;
                    state.UpdatedAtUtc = remoteDto.UpdatedAtUtc;
                    state.IsDeleted = false;
                    state.DeletedAtUtc = null;
                    state.ContentHash = SyncFingerprintHelper.ComputeNoteFingerprint(
                        localNote.IsProtected ? string.Empty : (localNote.Title ?? string.Empty),
                        localNote.Text,
                        localNote.IsPinned,
                        localNote.IsFavorite,
                        localNote.IsInbox,
                        localNote.SourceProcessName,
                        localNote.SourceWindowTitle,
                        localNote.SourceUrl,
                        localNote.CapturedAt.HasValue ? SyncPackageExporter.NormalizeToUtc(localNote.CapturedAt.Value) : null,
                        false,
                        localNote.NoteTags.Where(nt => nt.Tag != null).Select(nt => (nt.Tag!.SyncId, nt.Origin, nt.IsSuppressed)),
                        localNote.IsProtected,
                        localNote.ProtectedFormatVersion,
                        localNote.ProtectedKdfIterations,
                        localNote.ProtectedSaltBase64,
                        localNote.ProtectedNonceBase64,
                        localNote.ProtectedTagBase64,
                        localNote.ProtectedCiphertextBase64,
                        localNote.IsProtected ? localNote.ProtectedKdfDescriptor : null,
                        localNote.IsProtected ? localNote.ProtectedOriginalSyncId : null);
                }
            }
            else if (conflict.EntityType == "Tag")
            {
                SyncTagDto? remoteTag;
                try
                {
                    remoteTag = JsonSerializer.Deserialize<SyncTagDto>(conflict.RemoteDataJson, JsonOptions);
                }
                catch (Exception ex)
                {
                    await tx.RollbackAsync(ct);
                    return SyncConflictResolutionResult.Failure(conflictId, $"Не удалось разобрать данные удалённой версии тега: {ex.Message}");
                }

                if (remoteTag == null)
                {
                    await tx.RollbackAsync(ct);
                    return SyncConflictResolutionResult.Failure(conflictId, "Данные удалённой версии тега пусты.");
                }

                var localTag = await db.Tags.Include(t => t.Synonyms).Include(t => t.ParentTag).FirstOrDefaultAsync(t => t.SyncId == conflict.SyncId, ct);
                var state = await db.SyncEntityStates.FirstOrDefaultAsync(s => s.SyncId == conflict.SyncId, ct);
                if (state == null)
                {
                    state = new SyncEntityState
                    {
                        SyncId = conflict.SyncId,
                        EntityType = "Tag"
                    };
                    db.SyncEntityStates.Add(state);
                }

                if (remoteTag.Operation == SyncOperationType.Delete)
                {
                    if (localTag != null)
                    {
                        db.Tags.Remove(localTag);
                    }

                    state.RevisionId = conflict.RemoteRevisionId;
                    state.ParentRevisionId = conflict.ParentRevisionId;
                    state.DeviceId = conflict.SourceDeviceId;
                    state.UpdatedAtUtc = remoteTag.UpdatedAtUtc;
                    state.IsDeleted = true;
                    state.DeletedAtUtc = remoteTag.UpdatedAtUtc;
                    state.ContentHash = SyncFingerprintHelper.ComputeTombstoneFingerprint("Tag");
                }
                else
                {
                    Tag? parentTag = null;
                    if (remoteTag.ParentTagSyncId.HasValue)
                    {
                        parentTag = await db.Tags.FirstOrDefaultAsync(t => t.SyncId == remoteTag.ParentTagSyncId.Value, ct);
                        if (parentTag == null)
                        {
                            await tx.RollbackAsync(ct);
                            return SyncConflictResolutionResult.Failure(conflictId, $"Родительский тег {remoteTag.ParentTagSyncId.Value} не найден.");
                        }
                    }

                    if (localTag == null)
                    {
                        localTag = new Tag
                        {
                            SyncId = conflict.SyncId
                        };
                        db.Tags.Add(localTag);
                    }

                    localTag.Name = remoteTag.Name;
                    localTag.ParentTag = parentTag;
                    localTag.ParentTagId = parentTag?.Id;

                    // Reconcile synonyms
                    var incSynonyms = (remoteTag.Synonyms ?? new List<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var toRemove = localTag.Synonyms.Where(s => !incSynonyms.Contains(s.Value)).ToList();
                    foreach (var s in toRemove)
                    {
                        db.TagSynonyms.Remove(s);
                        localTag.Synonyms.Remove(s);
                    }
                    var existingSyns = localTag.Synonyms.Select(s => s.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var syn in incSynonyms)
                    {
                        if (!existingSyns.Contains(syn))
                        {
                            localTag.Synonyms.Add(new TagSynonym { Value = syn, Tag = localTag });
                        }
                    }

                    state.RevisionId = conflict.RemoteRevisionId;
                    state.ParentRevisionId = conflict.ParentRevisionId;
                    state.DeviceId = conflict.SourceDeviceId;
                    state.UpdatedAtUtc = remoteTag.UpdatedAtUtc;
                    state.IsDeleted = false;
                    state.DeletedAtUtc = null;
                    state.ContentHash = SyncFingerprintHelper.ComputeTagFingerprint(localTag.Name, remoteTag.ParentTagSyncId, remoteTag.Synonyms);
                }
            }
            else if (conflict.EntityType == "NoteTemplate")
            {
                SyncTemplateDto? remoteTmpl;
                try
                {
                    remoteTmpl = JsonSerializer.Deserialize<SyncTemplateDto>(conflict.RemoteDataJson, JsonOptions);
                }
                catch (Exception ex)
                {
                    await tx.RollbackAsync(ct);
                    return SyncConflictResolutionResult.Failure(conflictId, $"Не удалось разобрать данные удалённой версии шаблона: {ex.Message}");
                }

                if (remoteTmpl == null)
                {
                    await tx.RollbackAsync(ct);
                    return SyncConflictResolutionResult.Failure(conflictId, "Данные удалённой версии шаблона пусты.");
                }

                var localTmpl = await db.NoteTemplates.Include(t => t.TemplateTags).ThenInclude(tt => tt.Tag).FirstOrDefaultAsync(t => t.SyncId == conflict.SyncId, ct);
                var state = await db.SyncEntityStates.FirstOrDefaultAsync(s => s.SyncId == conflict.SyncId, ct);
                if (state == null)
                {
                    state = new SyncEntityState
                    {
                        SyncId = conflict.SyncId,
                        EntityType = "NoteTemplate"
                    };
                    db.SyncEntityStates.Add(state);
                }

                if (remoteTmpl.Operation == SyncOperationType.Delete)
                {
                    if (localTmpl != null)
                    {
                        db.NoteTemplates.Remove(localTmpl);
                    }

                    state.RevisionId = conflict.RemoteRevisionId;
                    state.ParentRevisionId = conflict.ParentRevisionId;
                    state.DeviceId = conflict.SourceDeviceId;
                    state.UpdatedAtUtc = remoteTmpl.UpdatedAtUtc;
                    state.IsDeleted = true;
                    state.DeletedAtUtc = remoteTmpl.UpdatedAtUtc;
                    state.ContentHash = SyncFingerprintHelper.ComputeTombstoneFingerprint("NoteTemplate");
                }
                else
                {
                    if (localTmpl == null)
                    {
                        localTmpl = new NoteTemplate
                        {
                            SyncId = conflict.SyncId,
                            CreatedAt = remoteTmpl.CreatedAtUtc.ToLocalTime()
                        };
                        db.NoteTemplates.Add(localTmpl);
                    }

                    localTmpl.Title = remoteTmpl.Title;
                    localTmpl.Text = remoteTmpl.Text;
                    localTmpl.UpdatedAt = remoteTmpl.UpdatedAtUtc.ToLocalTime();

                    // Reconcile template tags
                    var incTagSyncIds = (remoteTmpl.TagSyncIds ?? new List<Guid>()).ToHashSet();
                    var toRemove = localTmpl.TemplateTags.Where(tt => tt.Tag != null && !incTagSyncIds.Contains(tt.Tag.SyncId)).ToList();
                    foreach (var tt in toRemove)
                    {
                        db.NoteTemplateTags.Remove(tt);
                        localTmpl.TemplateTags.Remove(tt);
                    }

                    foreach (var tagSyncId in incTagSyncIds)
                    {
                        var tag = await db.Tags.FirstOrDefaultAsync(t => t.SyncId == tagSyncId, ct);
                        if (tag != null && !localTmpl.TemplateTags.Any(tt => tt.TagId == tag.Id))
                        {
                            localTmpl.TemplateTags.Add(new NoteTemplateTag
                            {
                                Template = localTmpl,
                                Tag = tag
                            });
                        }
                    }

                    state.RevisionId = conflict.RemoteRevisionId;
                    state.ParentRevisionId = conflict.ParentRevisionId;
                    state.DeviceId = conflict.SourceDeviceId;
                    state.UpdatedAtUtc = remoteTmpl.UpdatedAtUtc;
                    state.IsDeleted = false;
                    state.DeletedAtUtc = null;
                    state.ContentHash = SyncFingerprintHelper.ComputeTemplateFingerprint(
                        localTmpl.Title,
                        localTmpl.Text,
                        localTmpl.TemplateTags.Where(tt => tt.Tag != null).Select(tt => tt.Tag!.SyncId));
                }
            }
            else if (conflict.EntityType == "NoteAttachment")
            {
                SyncAttachmentDto? remoteAtt;
                try
                {
                    remoteAtt = JsonSerializer.Deserialize<SyncAttachmentDto>(conflict.RemoteDataJson, JsonOptions);
                }
                catch (Exception ex)
                {
                    await tx.RollbackAsync(ct);
                    return SyncConflictResolutionResult.Failure(conflictId, $"Не удалось разобрать данные удалённой версии вложения: {ex.Message}");
                }

                if (remoteAtt == null)
                {
                    await tx.RollbackAsync(ct);
                    return SyncConflictResolutionResult.Failure(conflictId, "Данные удалённой версии вложения пусты.");
                }

                var localAtt = await db.NoteAttachments.Include(a => a.Note).FirstOrDefaultAsync(a => a.SyncId == conflict.SyncId, ct);
                var state = await db.SyncEntityStates.FirstOrDefaultAsync(s => s.SyncId == conflict.SyncId, ct);
                if (state == null)
                {
                    state = new SyncEntityState
                    {
                        SyncId = conflict.SyncId,
                        EntityType = "NoteAttachment"
                    };
                    db.SyncEntityStates.Add(state);
                }

                if (remoteAtt.Operation == SyncOperationType.Delete)
                {
                    if (localAtt != null)
                    {
                        db.NoteAttachments.Remove(localAtt);
                    }

                    state.RevisionId = conflict.RemoteRevisionId;
                    state.ParentRevisionId = conflict.ParentRevisionId;
                    state.DeviceId = conflict.SourceDeviceId;
                    state.UpdatedAtUtc = remoteAtt.UpdatedAtUtc;
                    state.IsDeleted = true;
                    state.DeletedAtUtc = remoteAtt.UpdatedAtUtc;
                    state.ContentHash = SyncFingerprintHelper.ComputeTombstoneFingerprint("NoteAttachment");
                }
                else
                {
                    var parentNote = await db.Notes.FirstOrDefaultAsync(n => n.SyncId == remoteAtt.NoteSyncId, ct);
                    if (parentNote == null)
                    {
                        await tx.RollbackAsync(ct);
                        return SyncConflictResolutionResult.Failure(conflictId, $"Родительская заметка {remoteAtt.NoteSyncId} для вложения не найдена.");
                    }

                    if (localAtt == null)
                    {
                        localAtt = new NoteAttachment
                        {
                            SyncId = conflict.SyncId,
                            NoteId = parentNote.Id,
                            Note = parentNote,
                            StoredFileName = string.Empty,
                            RelativePath = string.Empty
                        };
                        db.NoteAttachments.Add(localAtt);
                    }
                    else
                    {
                        localAtt.NoteId = parentNote.Id;
                        localAtt.Note = parentNote;
                    }

                    localAtt.OriginalFileName = remoteAtt.OriginalFileName;
                    localAtt.ContentType = remoteAtt.ContentType;
                    localAtt.Size = remoteAtt.Size;
                    localAtt.Sha256 = remoteAtt.Sha256;
                    localAtt.CreatedAt = remoteAtt.CreatedAtUtc.ToLocalTime();
                    localAtt.IsProtected = remoteAtt.IsProtected;
                    localAtt.ProtectedFormatVersion = remoteAtt.IsProtected ? remoteAtt.ProtectedFormatVersion : 0;
                    localAtt.ProtectedKdfIterations = remoteAtt.IsProtected ? remoteAtt.ProtectedKdfIterations : 0;
                    localAtt.ProtectedKdfDescriptor = remoteAtt.IsProtected ? remoteAtt.ProtectedKdfDescriptor : null;
                    localAtt.ProtectedSaltBase64 = remoteAtt.IsProtected ? remoteAtt.ProtectedSaltBase64 : null;
                    localAtt.ProtectedNonceBase64 = remoteAtt.IsProtected ? remoteAtt.ProtectedNonceBase64 : null;
                    localAtt.ProtectedTagBase64 = remoteAtt.IsProtected ? remoteAtt.ProtectedTagBase64 : null;
                    localAtt.ProtectedCiphertextBase64 = remoteAtt.IsProtected ? remoteAtt.ProtectedCiphertextBase64 : null;

                    state.RevisionId = conflict.RemoteRevisionId;
                    state.ParentRevisionId = conflict.ParentRevisionId;
                    state.DeviceId = conflict.SourceDeviceId;
                    state.UpdatedAtUtc = remoteAtt.UpdatedAtUtc;
                    state.IsDeleted = false;
                    state.DeletedAtUtc = null;
                    state.ContentHash = SyncFingerprintHelper.ComputeAttachmentFingerprint(
                        remoteAtt.NoteSyncId,
                        remoteAtt.OriginalFileName,
                        remoteAtt.ContentType,
                        remoteAtt.Size,
                        remoteAtt.Sha256,
                        remoteAtt.IsProtected,
                        remoteAtt.ProtectedFormatVersion,
                        remoteAtt.ProtectedKdfIterations,
                        remoteAtt.ProtectedSaltBase64,
                        remoteAtt.ProtectedNonceBase64,
                        remoteAtt.ProtectedTagBase64,
                        remoteAtt.ProtectedCiphertextBase64,
                        remoteAtt.IsProtected ? remoteAtt.ProtectedKdfDescriptor : null);
                }
            }
            else
            {
                await tx.RollbackAsync(ct);
                return SyncConflictResolutionResult.Failure(conflictId, $"Неподдерживаемый тип сущности: {conflict.EntityType}");
            }

            conflict.IsResolved = true;
            conflict.ResolvedAtUtc = DateTime.UtcNow;
            conflict.ResolutionAction = "AcceptRemote";

            InvokePhase("conflict_row");
            InvokePhase("before_commit");

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return SyncConflictResolutionResult.Succeeded(conflictId, "AcceptRemote");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await tx.RollbackAsync(ct);
            ErrorLogService.Write("SyncConflictService.ResolveAcceptRemote", ex);
            return SyncConflictResolutionResult.Failure(conflictId, $"Сбой при принятии удалённой версии: {ex.Message}");
        }
    }

    public Task<SyncConflictResolutionResult> ResolveMergeNoteAsync(int conflictId, string mergedText, CancellationToken ct = default)
        => ResolveMergeNoteAsync(conflictId, mergedText, SyncConflictMergeChoices.KeepLocalMetadata, ct);

    public Task<SyncConflictResolutionResult> ResolveMergeNoteAsync(
        int conflictId,
        string mergedText,
        SyncConflictMergeChoices choices,
        CancellationToken ct = default)
        => _mutationCoordinator.ExecuteBulkMutationAsync(() => ResolveMergeNoteCoreAsync(conflictId, mergedText, choices, ct), ct);

    private async Task<SyncConflictResolutionResult> ResolveMergeNoteCoreAsync(
        int conflictId,
        string mergedText,
        SyncConflictMergeChoices choices,
        CancellationToken ct)
    {
        using var db = _contextFactory();
        var conflict = await db.SyncConflicts.FirstOrDefaultAsync(c => c.Id == conflictId, ct);
        if (conflict == null) return SyncConflictResolutionResult.Failure(conflictId, "Конфликт не найден.");
        if (conflict.IsResolved) return SyncConflictResolutionResult.Succeeded(conflictId, "Merge");

        try
        {
            InvokePhase("validation");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return SyncConflictResolutionResult.Failure(conflictId, $"Сбой при разрешении конфликта: {ex.Message}");
        }

        var staleMerge = await GetStaleReasonAsync(db, conflict, ct);
        if (staleMerge != null) return SyncConflictResolutionResult.Failure(conflictId, staleMerge);

        if (conflict.EntityType != "Note")
        {
            return SyncConflictResolutionResult.Failure(conflictId, "Действие «Объединить» поддерживается только для заметок.");
        }

        if (string.IsNullOrWhiteSpace(conflict.RemoteDataJson))
        {
            return SyncConflictResolutionResult.Failure(conflictId, "Данные удалённой версии заметки отсутствуют.");
        }

        SyncNoteDto? testDto = null;
        try
        {
            testDto = JsonSerializer.Deserialize<SyncNoteDto>(conflict.RemoteDataJson, JsonOptions);
            if (testDto == null)
            {
                return SyncConflictResolutionResult.Failure(conflictId, "Данные удалённой версии заметки пусты.");
            }
        }
        catch (Exception ex)
        {
            return SyncConflictResolutionResult.Failure(conflictId, $"Данные удалённой версии заметки повреждены: {ex.Message}");
        }

        if (testDto.IsProtected)
        {
            return SyncConflictResolutionResult.Failure(conflictId, "Объединение недоступно для защищённой облачной версии. Используйте Keep Local, Keep Remote или Keep Both без фиктивного plaintext.");
        }

        choices ??= SyncConflictMergeChoices.KeepLocalMetadata;
        if (choices.Title == SyncConflictFieldChoice.Unspecified || choices.Tags == SyncConflictFieldChoice.Unspecified)
        {
            return SyncConflictResolutionResult.Failure(conflictId, "Для объединения нужно явно выбрать заголовок и теги (локальные или облачные).");
        }

        using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            Guid localDeviceId = _deviceIdProvider.GetDeviceId();
            var localNote = await db.Notes.Include(n => n.NoteTags).ThenInclude(nt => nt.Tag).FirstOrDefaultAsync(n => n.SyncId == conflict.SyncId, ct);
            if (localNote == null)
            {
                await tx.RollbackAsync(ct);
                return SyncConflictResolutionResult.Failure(conflictId, "Локальная заметка для объединения не найдена.");
            }

            if (localNote.IsProtected)
            {
                await tx.RollbackAsync(ct);
                return SyncConflictResolutionResult.Failure(conflictId, "Объединение недоступно для защищённой локальной заметки. Используйте Keep Local, Keep Remote или Keep Both без фиктивного plaintext.");
            }

            InvokePhase("entity");
            if (choices.Title == SyncConflictFieldChoice.Remote)
            {
                localNote.Title = testDto?.Title ?? string.Empty;
            }

            localNote.Text = mergedText ?? string.Empty;
            localNote.UpdatedAt = DateTime.Now;
            localNote.DeletedAt = null;

            if (choices.Tags == SyncConflictFieldChoice.Remote)
            {
                localNote.NoteTags.Clear();
                if (testDto?.Tags != null)
                {
                    foreach (var t in testDto.Tags)
                    {
                        var localTag = await db.Tags.FirstOrDefaultAsync(lt => lt.SyncId == t.TagSyncId, ct);
                        if (localTag != null)
                        {
                            localNote.NoteTags.Add(new NoteTag
                            {
                                Note = localNote,
                                Tag = localTag,
                                Origin = t.Origin,
                                IsSuppressed = t.IsSuppressed
                            });
                        }
                    }
                }
            }

            InvokePhase("history");
            if (_historyService != null)
            {
                _historyService.SaveSnapshot(db, localNote);
            }

            InvokePhase("sync_state");

            var state = await db.SyncEntityStates.FirstOrDefaultAsync(s => s.SyncId == conflict.SyncId, ct);
            if (state == null)
            {
                state = new SyncEntityState
                {
                    SyncId = conflict.SyncId,
                    EntityType = "Note"
                };
                db.SyncEntityStates.Add(state);
            }

            state.ParentRevisionId = conflict.RemoteRevisionId;
            state.RevisionId = Guid.NewGuid();
            state.DeviceId = localDeviceId;
            state.UpdatedAtUtc = DateTime.UtcNow;
            state.IsDeleted = false;
            state.DeletedAtUtc = null;
            state.ContentHash = SyncFingerprintHelper.ComputeNoteFingerprint(
                localNote.IsProtected ? string.Empty : (localNote.Title ?? string.Empty),
                localNote.Text,
                localNote.IsPinned,
                localNote.IsFavorite,
                localNote.IsInbox,
                localNote.SourceProcessName,
                localNote.SourceWindowTitle,
                localNote.SourceUrl,
                localNote.CapturedAt.HasValue ? SyncPackageExporter.NormalizeToUtc(localNote.CapturedAt.Value) : null,
                false,
                localNote.NoteTags.Where(nt => nt.Tag != null).Select(nt => (nt.Tag!.SyncId, nt.Origin, nt.IsSuppressed)),
                localNote.IsProtected,
                localNote.ProtectedFormatVersion,
                localNote.ProtectedKdfIterations,
                localNote.ProtectedSaltBase64,
                localNote.ProtectedNonceBase64,
                localNote.ProtectedTagBase64,
                localNote.ProtectedCiphertextBase64,
                localNote.IsProtected ? localNote.ProtectedKdfDescriptor : null,
                localNote.IsProtected ? localNote.ProtectedOriginalSyncId : null);

            conflict.IsResolved = true;
            conflict.ResolvedAtUtc = DateTime.UtcNow;
            conflict.ResolutionAction = "Merge";

            InvokePhase("conflict_row");
            InvokePhase("before_commit");

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return SyncConflictResolutionResult.Succeeded(conflictId, "Merge");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await tx.RollbackAsync(ct);
            ErrorLogService.Write("SyncConflictService.ResolveMergeNote", ex);
            return SyncConflictResolutionResult.Failure(conflictId, $"Сбой при объединении заметки: {ex.Message}");
        }
    }

    private async Task<SyncConflictResolutionResult> CompleteKeepBothFromExistingCopyAsync(
        QuickNotesDbContext db,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx,
        SyncConflictRecord conflict,
        CancellationToken ct)
    {
        conflict.IsResolved = true;
        conflict.ResolvedAtUtc = DateTime.UtcNow;
        conflict.ResolutionAction = "KeepBoth";
        InvokePhase("conflict_row");
        InvokePhase("before_commit");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return SyncConflictResolutionResult.Succeeded(conflict.Id, "KeepBoth");
    }

    private static bool HasKeepBothCopyProvenance(SyncEntityState? copyState, Guid expectedCopyRevisionId)
        => copyState != null && copyState.RevisionId == expectedCopyRevisionId && !copyState.IsDeleted;

    private static bool IsCompatibleKeepBothNoteCopy(
        Note copy,
        SyncNoteDto remote,
        SyncEntityState? copyState,
        Guid expectedCopyRevisionId)
    {
        if (!HasKeepBothCopyProvenance(copyState, expectedCopyRevisionId))
        {
            return false;
        }

        if (copy.IsProtected != remote.IsProtected)
        {
            return false;
        }

        if (remote.IsProtected)
        {
            return string.Equals(copy.ProtectedCiphertextBase64 ?? string.Empty, remote.ProtectedCiphertextBase64 ?? string.Empty, StringComparison.Ordinal)
                && string.Equals(copy.ProtectedNonceBase64 ?? string.Empty, remote.ProtectedNonceBase64 ?? string.Empty, StringComparison.Ordinal)
                && string.Equals(copy.ProtectedTagBase64 ?? string.Empty, remote.ProtectedTagBase64 ?? string.Empty, StringComparison.Ordinal)
                && string.Equals(copy.ProtectedSaltBase64 ?? string.Empty, remote.ProtectedSaltBase64 ?? string.Empty, StringComparison.Ordinal)
                && copy.ProtectedOriginalSyncId == (remote.ProtectedOriginalSyncId ?? remote.SyncId);
        }

        return string.Equals(copy.Text ?? string.Empty, remote.Text ?? string.Empty, StringComparison.Ordinal)
            && copy.IsPinned == remote.IsPinned
            && copy.IsFavorite == remote.IsFavorite
            && copy.IsInbox == remote.IsInbox;
    }

    private static bool IsCompatibleKeepBothTagCopy(
        Tag copy,
        SyncTagDto remote,
        SyncEntityState? copyState,
        Guid expectedCopyRevisionId)
    {
        if (!HasKeepBothCopyProvenance(copyState, expectedCopyRevisionId))
        {
            return false;
        }

        var copySynonyms = copy.Synonyms.Select(s => s.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remoteSynonyms = (remote.Synonyms ?? new List<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return copySynonyms.SetEquals(remoteSynonyms);
    }

    private static bool IsCompatibleKeepBothTemplateCopy(
        NoteTemplate copy,
        SyncTemplateDto remote,
        SyncEntityState? copyState,
        Guid expectedCopyRevisionId)
    {
        if (!HasKeepBothCopyProvenance(copyState, expectedCopyRevisionId))
        {
            return false;
        }

        return string.Equals(copy.Text ?? string.Empty, remote.Text ?? string.Empty, StringComparison.Ordinal);
    }

    private void InvokePhase(string phase)
    {
        FailureInjectionHook?.Invoke(phase);
    }

    private async Task<string?> GetStaleReasonAsync(QuickNotesDbContext db, SyncConflictRecord conflict, CancellationToken ct)
    {
        if (!conflict.LocalRevisionId.HasValue)
        {
            return null;
        }

        var state = await db.SyncEntityStates.AsNoTracking().FirstOrDefaultAsync(s => s.SyncId == conflict.SyncId, ct);
        if (state != null && state.RevisionId != conflict.LocalRevisionId.Value)
        {
            return "Локальная версия изменилась после обнаружения конфликта. Обновите список и повторите.";
        }

        return null;
    }

    private static async Task<string> AllocateUniqueNoteTitleAsync(QuickNotesDbContext db, string baseTitle, CancellationToken ct)
    {
        string unique = baseTitle;
        int counter = 2;
        while (await db.Notes.AnyAsync(n => n.Title == unique, ct))
        {
            unique = $"{baseTitle} {counter++}";
        }

        return unique;
    }

    private static string FormatDeviceDisplay(Guid deviceId, bool isLocal)
    {
        if (deviceId == Guid.Empty)
        {
            return "устройство неизвестно";
        }

        string shortId = deviceId.ToString("N")[..8];
        return isLocal ? $"Это устройство ({shortId})" : $"Устройство {shortId}";
    }

    private static string FormatTimeDisplay(DateTime? utc)
    {
        if (!utc.HasValue || utc.Value == default)
        {
            return "время версии неизвестно";
        }

        return utc.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
    }

    private static string BuildReasonDisplay(SyncConflictDetail detail)
    {
        string reason = string.IsNullOrWhiteSpace(detail.Reason) ? "Причина конфликта не указана." : detail.Reason.Trim();
        if (detail.IsRemoteCorrupted || detail.IsLocalCorrupted)
        {
            return reason + " Снимок повреждён или неполный; доступны только безопасные метаданные.";
        }

        if (detail.LocalIsDeleted ^ detail.RemoteIsDeleted)
        {
            return reason + " Это конфликт правки и удаления (tombstone).";
        }

        if (detail.EntityType == "Note"
            && !detail.LocalIsProtected
            && !detail.RemoteIsProtected
            && string.Equals(detail.LocalText, detail.RemoteText, StringComparison.Ordinal)
            && (!string.Equals(detail.LocalTitle, detail.RemoteTitle, StringComparison.Ordinal)
                || detail.LocalIsPinned != detail.RemoteIsPinned
                || detail.LocalIsFavorite != detail.RemoteIsFavorite
                || detail.LocalIsInbox != detail.RemoteIsInbox
                || !detail.LocalTags.SequenceEqual(detail.RemoteTags)))
        {
            return reason + " Текст совпадает, отличаются метаданные (заголовок, флаги или теги).";
        }

        return reason;
    }

    private static string ExtractFirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var first = text.Split('\n', '\r').FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim() ?? string.Empty;
        return first.Length > 80 ? first.Substring(0, 80) + "…" : first;
    }

    private static string BuildInitialMergedDraft(string localText, string remoteText)
    {
        if (string.Equals(localText, remoteText, StringComparison.Ordinal))
            return localText;

        return $"{localText}\n\n--- [Облачная версия] ---\n{remoteText}";
    }
}
