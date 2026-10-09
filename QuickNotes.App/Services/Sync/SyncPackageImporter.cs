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
using QuickNotes.App.Services.Crypto;

namespace QuickNotes.App.Services.Sync;

public class SyncPackageImporter : ISyncPackageImporter
{
    private readonly ISyncCryptoService _cryptoService;
    private readonly INoteHistoryService? _historyService;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public SyncPackageImporter(
        ISyncCryptoService cryptoService,
        INoteHistoryService? historyService = null)
    {
        _cryptoService = cryptoService ?? throw new ArgumentNullException(nameof(cryptoService));
        _historyService = historyService;
    }

    public async Task<SyncImportResult> ImportFromFileAsync(QuickNotesDbContext db, string filePath, string password)
    {
        if (!File.Exists(filePath))
        {
            return SyncImportResult.Failure("Указанный файл пакета синхронизации не найден.");
        }

        string packageJson = await File.ReadAllTextAsync(filePath);
        return await ImportPackageAsync(db, packageJson, password);
    }

    public Task<SyncImportResult> ImportPackageAsync(QuickNotesDbContext db, string packageJson, string password)
        => ImportPackageAsync(db, packageJson, password, null, null);

    public async Task<SyncImportResult> ImportPackageAsync(
        QuickNotesDbContext db,
        string packageJson,
        string password,
        Func<SyncPackagePayload, Task>? beforeApply,
        UntrustedInboundKdfWorkBudget? inboundKdfBudget = null)
    {
        if (string.IsNullOrWhiteSpace(packageJson))
        {
            return SyncImportResult.Failure("Пакет синхронизации пуст.");
        }

        if (string.IsNullOrEmpty(password))
        {
            return SyncImportResult.Failure("Пароль для расшифровки пакета не может быть пустым.");
        }

        // 1. Envelope Parsing & Pre-validation
        SyncPackageEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<SyncPackageEnvelope>(packageJson, JsonOptions);
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("SyncPackageImporter.ParseEnvelope", ex.GetType().Name);
            return SyncImportResult.Failure("Не удалось разобрать структуру конверта пакета синхронизации.");
        }

        if (envelope == null || !string.Equals(envelope.Magic, "QNSP", StringComparison.Ordinal))
        {
            return SyncImportResult.Failure("Неверная сигнатура пакета синхронизации.");
        }

        if (envelope.FormatVersion > 1 || envelope.FormatVersion < 1)
        {
            return SyncImportResult.Failure($"Неподдерживаемая версия формата пакета ({envelope.FormatVersion}).");
        }

        if (envelope.Crypto == null ||
            !string.Equals(envelope.Crypto.Algorithm, SyncCryptoService.AlgorithmName, StringComparison.OrdinalIgnoreCase))
        {
            return SyncImportResult.Failure("Неподдерживаемые параметры криптографического конверта.");
        }

        // KDF descriptor: unknown algorithms/versions/work factors are rejected before any derivation.
        KdfEnvelopeReading reading;
        try
        {
            reading = _cryptoService.ResolveDescriptor(
                envelope.Crypto.KdfDescriptor,
                envelope.Crypto.KdfAlgorithm,
                envelope.Crypto.KdfVersion,
                envelope.Crypto.KdfIterations,
                SyncCryptoService.SaltByteSize);
        }
        catch (SyncKdfWorkBudgetExceededException)
        {
            throw;
        }
        catch (SyncSecurityException ex)
        {
            return SyncImportResult.Failure(ex.Message);
        }

        byte[] ciphertext;
        byte[] salt;
        byte[] nonce;
        byte[] tag;

        try
        {
            ciphertext = Convert.FromBase64String(envelope.EncryptedPayloadBase64);
            salt = Convert.FromBase64String(envelope.Crypto.SaltBase64);
            nonce = Convert.FromBase64String(envelope.Crypto.NonceBase64);
            tag = Convert.FromBase64String(envelope.Crypto.TagBase64);
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("SyncPackageImporter.Base64", ex.GetType().Name);
            return SyncImportResult.Failure("Повреждены зашифрованные данные пакета синхронизации.");
        }

        // 2. Decrypt Payload
        byte[] plaintextBytes;
        try
        {
            byte[] associatedData = envelope.GetAssociatedData();
            plaintextBytes = _cryptoService.DecryptPayload(
                ciphertext,
                salt,
                nonce,
                tag,
                password,
                associatedData,
                reading.Descriptor.Iterations,
                inboundKdfBudget);
        }
        catch (SyncKdfWorkBudgetExceededException)
        {
            throw;
        }
        catch (SyncSecurityException ex)
        {
            ErrorLogService.Write("SyncPackageImporter.Decrypt", ex.GetType().Name);
            return SyncImportResult.Failure(ex.Message);
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("SyncPackageImporter.Decrypt", ex.GetType().Name);
            return SyncImportResult.Failure("Сбой расшифровки пакета: неверный пароль или нарушена целостность.");
        }

        // 3. Deserialize Payload
        SyncPackagePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<SyncPackagePayload>(plaintextBytes, JsonOptions);
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("SyncPackageImporter.ParsePayload", ex.GetType().Name);
            return SyncImportResult.Failure("Не удалось разобрать расшифрованную полезную нагрузку пакета.");
        }

        if (payload == null || payload.PayloadVersion > 1)
        {
            return SyncImportResult.Failure("Неподдерживаемая версия схемы полезной нагрузки пакета.");
        }

        if (beforeApply != null)
        {
            await beforeApply(payload).ConfigureAwait(false);
        }

        var result = new SyncImportResult
        {
            Success = true,
            PackageId = envelope.PackageId,
            SourceDeviceId = envelope.DeviceId
        };

        // 4. Build Change Plan & Execute Atomically
        using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            var recordedConflictKeys = new HashSet<(Guid, Guid)>();
            var syncStates = await db.SyncEntityStates.ToDictionaryAsync(s => s.SyncId);

            var localTags = await db.Tags
                .Include(t => t.Synonyms)
                .Include(t => t.ParentTag)
                .ToDictionaryAsync(t => t.SyncId);

            var localNotes = await db.Notes
                .Include(n => n.NoteTags)
                .ThenInclude(nt => nt.Tag)
                .ToDictionaryAsync(n => n.SyncId);

            var localTemplates = await db.NoteTemplates
                .Include(t => t.TemplateTags)
                .ThenInclude(tt => tt.Tag)
                .ToDictionaryAsync(t => t.SyncId);

            var localAttachments = await db.NoteAttachments
                .Include(a => a.Note)
                .ToDictionaryAsync(a => a.SyncId);

            // A. Apply Tags
            foreach (var incTag in payload.Tags)
            {
                string tagHash = incTag.Operation == SyncOperationType.Delete
                    ? SyncFingerprintHelper.ComputeTombstoneFingerprint("Tag")
                    : SyncFingerprintHelper.ComputeTagFingerprint(incTag.Name, incTag.ParentTagSyncId, incTag.Synonyms);

                if (syncStates.TryGetValue(incTag.SyncId, out var state))
                {
                    if (incTag.RevisionId == state.RevisionId)
                    {
                        result.Tags.Skipped++;
                        continue;
                    }

                    if (incTag.ParentRevisionId == state.RevisionId)
                    {
                        // Linear revision
                        if (incTag.Operation == SyncOperationType.Delete)
                        {
                            if (localTags.TryGetValue(incTag.SyncId, out var existingTag))
                            {
                                db.Tags.Remove(existingTag);
                                localTags.Remove(incTag.SyncId);
                            }
                            state.ParentRevisionId = state.RevisionId;
                            state.RevisionId = incTag.RevisionId;
                            state.IsDeleted = true;
                            state.DeletedAtUtc = incTag.UpdatedAtUtc;
                            state.UpdatedAtUtc = incTag.UpdatedAtUtc;
                            state.DeviceId = incTag.DeviceId;
                            state.ContentHash = tagHash;
                            result.Tags.Deleted++;
                        }
                        else
                        {
                            if (localTags.TryGetValue(incTag.SyncId, out var existingTag))
                            {
                                existingTag.Name = incTag.Name;

                                var existingSynonyms = existingTag.Synonyms.Select(s => s.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
                                var incomingSynonyms = incTag.Synonyms.ToHashSet(StringComparer.OrdinalIgnoreCase);

                                var toRemove = existingTag.Synonyms.Where(s => !incomingSynonyms.Contains(s.Value)).ToList();
                                foreach (var syn in toRemove)
                                {
                                    db.TagSynonyms.Remove(syn);
                                    existingTag.Synonyms.Remove(syn);
                                }

                                foreach (var syn in incTag.Synonyms)
                                {
                                    if (!existingSynonyms.Contains(syn))
                                    {
                                        existingTag.Synonyms.Add(new TagSynonym { Value = syn, Tag = existingTag });
                                    }
                                }
                            }
                            else
                            {
                                existingTag = new Tag
                                {
                                    SyncId = incTag.SyncId,
                                    Name = incTag.Name
                                };
                                foreach (var syn in incTag.Synonyms)
                                {
                                    existingTag.Synonyms.Add(new TagSynonym { Value = syn, Tag = existingTag });
                                }

                                db.Tags.Add(existingTag);
                                localTags[incTag.SyncId] = existingTag;
                            }

                            state.ParentRevisionId = state.RevisionId;
                            state.RevisionId = incTag.RevisionId;
                            state.UpdatedAtUtc = incTag.UpdatedAtUtc;
                            state.IsDeleted = false;
                            state.DeletedAtUtc = null;
                            state.DeviceId = incTag.DeviceId;
                            state.ContentHash = tagHash;
                            result.Tags.Updated++;
                        }
                    }
                    else
                    {
                        // Conflict
                        string? localTagJson = null;
                        if (localTags.TryGetValue(incTag.SyncId, out var existingTag))
                        {
                            localTagJson = JsonSerializer.Serialize(new
                            {
                                existingTag.Name,
                                ParentTagSyncId = existingTag.ParentTag?.SyncId,
                                Synonyms = existingTag.Synonyms.Select(s => s.Value).ToList()
                            });
                        }
                        else if (state != null)
                        {
                            localTagJson = JsonSerializer.Serialize(new { IsDeleted = state.IsDeleted, state.DeletedAtUtc });
                        }
                        string remoteTagJson = JsonSerializer.Serialize(incTag);
                        RecordDurableConflict(db, result, recordedConflictKeys, incTag.SyncId, "Tag", state?.RevisionId, incTag.RevisionId, incTag.ParentRevisionId, envelope.DeviceId, envelope.PackageId, "Конфликт ветвления ревизий тега: входящий родитель не совпадает с текущей ревизией.", localTagJson, remoteTagJson);
                        result.Tags.Skipped++;
                    }
                }
                else
                {
                    // New Tag
                    if (incTag.Operation == SyncOperationType.Delete)
                    {
                        state = new SyncEntityState
                        {
                            SyncId = incTag.SyncId,
                            EntityType = "Tag",
                            RevisionId = incTag.RevisionId,
                            ParentRevisionId = incTag.ParentRevisionId,
                            DeviceId = incTag.DeviceId,
                            UpdatedAtUtc = incTag.UpdatedAtUtc,
                            IsDeleted = true,
                            DeletedAtUtc = incTag.UpdatedAtUtc,
                            ContentHash = tagHash
                        };
                        db.SyncEntityStates.Add(state);
                        syncStates[incTag.SyncId] = state;
                        result.Tags.Skipped++;
                    }
                    else
                    {
                        var newTag = new Tag
                        {
                            SyncId = incTag.SyncId,
                            Name = incTag.Name
                        };
                        foreach (var syn in incTag.Synonyms)
                        {
                            newTag.Synonyms.Add(new TagSynonym { Value = syn, Tag = newTag });
                        }

                        db.Tags.Add(newTag);
                        localTags[incTag.SyncId] = newTag;

                        state = new SyncEntityState
                        {
                            SyncId = incTag.SyncId,
                            EntityType = "Tag",
                            RevisionId = incTag.RevisionId,
                            ParentRevisionId = incTag.ParentRevisionId,
                            DeviceId = incTag.DeviceId,
                            UpdatedAtUtc = incTag.UpdatedAtUtc,
                            IsDeleted = false,
                            ContentHash = tagHash
                        };
                        db.SyncEntityStates.Add(state);
                        syncStates[incTag.SyncId] = state;
                        result.Tags.Created++;
                    }
                }
            }

            // Persist new tags so they have local integer IDs for hierarchy
            await db.SaveChangesAsync();

            // Pass 2: Resolve ParentTagId
            foreach (var incTag in payload.Tags.Where(t => t.Operation == SyncOperationType.Upsert))
            {
                if (localTags.TryGetValue(incTag.SyncId, out var targetTag))
                {
                    if (incTag.ParentTagSyncId.HasValue)
                    {
                        if (localTags.TryGetValue(incTag.ParentTagSyncId.Value, out var parentTag))
                        {
                            targetTag.ParentTagId = parentTag.Id;
                            targetTag.ParentTag = parentTag;
                        }
                        else
                        {
                            result.Diagnostics.Add($"Родительский тег {incTag.ParentTagSyncId} для тега {incTag.SyncId} не найден в локальной базе.");
                            targetTag.ParentTagId = null;
                            targetTag.ParentTag = null;
                        }
                    }
                    else
                    {
                        targetTag.ParentTagId = null;
                        targetTag.ParentTag = null;
                    }

                    if (syncStates.TryGetValue(incTag.SyncId, out var st) && !st.IsDeleted)
                    {
                        st.ContentHash = SyncFingerprintHelper.ComputeTagFingerprint(
                            targetTag.Name,
                            targetTag.ParentTag?.SyncId,
                            targetTag.Synonyms.Select(s => s.Value));
                    }
                }
            }
            await db.SaveChangesAsync();

            // B. Apply Notes
            foreach (var incNote in payload.Notes)
            {
                if (syncStates.TryGetValue(incNote.SyncId, out var state))
                {
                    if (incNote.RevisionId == state.RevisionId)
                    {
                        result.Notes.Skipped++;
                        continue;
                    }

                    if (incNote.ParentRevisionId == state.RevisionId)
                    {
                        // Linear revision
                        if (incNote.Operation == SyncOperationType.Delete)
                        {
                            if (localNotes.TryGetValue(incNote.SyncId, out var existingNote))
                            {
                                existingNote.DeletedAt = incNote.UpdatedAtUtc.ToLocalTime();
                            }
                            state.ParentRevisionId = state.RevisionId;
                            state.RevisionId = incNote.RevisionId;
                            state.IsDeleted = true;
                            state.DeletedAtUtc = incNote.UpdatedAtUtc;
                            state.UpdatedAtUtc = incNote.UpdatedAtUtc;
                            state.DeviceId = incNote.DeviceId;
                            state.ContentHash = SyncFingerprintHelper.ComputeTombstoneFingerprint("Note");
                            result.Notes.Deleted++;
                        }
                        else
                        {
                            if (localNotes.TryGetValue(incNote.SyncId, out var existingNote))
                            {
                                ApplyNoteFields(existingNote, incNote);
                                existingNote.DeletedAt = null;

                                var incTagSyncIds = incNote.Tags.Select(t => t.TagSyncId).ToHashSet();
                                var toRemoveTags = existingNote.NoteTags
                                    .Where(nt => nt.Tag != null && !incTagSyncIds.Contains(nt.Tag.SyncId))
                                    .ToList();

                                foreach (var nt in toRemoveTags)
                                {
                                    db.NoteTags.Remove(nt);
                                    existingNote.NoteTags.Remove(nt);
                                }

                                foreach (var tagDto in incNote.Tags)
                                {
                                    if (localTags.TryGetValue(tagDto.TagSyncId, out var linkedTag))
                                    {
                                        var existingLink = existingNote.NoteTags.FirstOrDefault(nt => nt.TagId == linkedTag.Id);
                                        if (existingLink != null)
                                        {
                                            existingLink.Origin = tagDto.Origin;
                                            existingLink.IsSuppressed = tagDto.IsSuppressed;
                                        }
                                        else
                                        {
                                            existingNote.NoteTags.Add(new NoteTag
                                            {
                                                Note = existingNote,
                                                Tag = linkedTag,
                                                Origin = tagDto.Origin,
                                                IsSuppressed = tagDto.IsSuppressed
                                            });
                                        }
                                    }
                                    else
                                    {
                                        result.Diagnostics.Add($"Тег {tagDto.TagSyncId} для заметки {incNote.SyncId} не найден в локальной базе.");
                                    }
                                }

                                _historyService?.SaveSnapshot(db, existingNote);
                            }
                            else
                            {
                                existingNote = new Note
                                {
                                    SyncId = incNote.SyncId,
                                    Title = incNote.IsProtected ? string.Empty : (incNote.Title ?? string.Empty),
                                    Text = incNote.IsProtected ? string.Empty : incNote.Text,
                                    CreatedAt = incNote.CreatedAtUtc.ToLocalTime(),
                                    UpdatedAt = incNote.UpdatedAtUtc.ToLocalTime(),
                                    IsPinned = incNote.IsPinned,
                                    IsFavorite = incNote.IsFavorite,
                                    IsInbox = incNote.IsInbox,
                                    SourceProcessName = incNote.IsProtected ? null : incNote.SourceProcessName,
                                    SourceWindowTitle = incNote.IsProtected ? null : incNote.SourceWindowTitle,
                                    SourceUrl = incNote.IsProtected ? null : incNote.SourceUrl,
                                    CapturedAt = incNote.CapturedAtUtc?.ToLocalTime(),
                                    DeletedAt = null,
                                    IsProtected = incNote.IsProtected,
                                    ProtectedFormatVersion = incNote.IsProtected ? incNote.ProtectedFormatVersion : 0,
                                    ProtectedKdfIterations = incNote.IsProtected ? incNote.ProtectedKdfIterations : 0,
                                    ProtectedKdfDescriptor = incNote.IsProtected ? incNote.ProtectedKdfDescriptor : null,
                                    ProtectedSaltBase64 = incNote.IsProtected ? incNote.ProtectedSaltBase64 : string.Empty,
                                    ProtectedNonceBase64 = incNote.IsProtected ? incNote.ProtectedNonceBase64 : string.Empty,
                                    ProtectedTagBase64 = incNote.IsProtected ? incNote.ProtectedTagBase64 : string.Empty,
                                    ProtectedCiphertextBase64 = incNote.IsProtected ? incNote.ProtectedCiphertextBase64 : string.Empty,
                                    ProtectedOriginalSyncId = incNote.IsProtected ? incNote.ProtectedOriginalSyncId : null
                                };

                                foreach (var tagDto in incNote.Tags)
                                {
                                    if (localTags.TryGetValue(tagDto.TagSyncId, out var newLinkedTag))
                                    {
                                        existingNote.NoteTags.Add(new NoteTag
                                        {
                                            Note = existingNote,
                                            Tag = newLinkedTag,
                                            Origin = tagDto.Origin,
                                            IsSuppressed = tagDto.IsSuppressed
                                        });
                                    }
                                    else
                                    {
                                        result.Diagnostics.Add($"Тег {tagDto.TagSyncId} для новой заметки {incNote.SyncId} не найден.");
                                    }
                                }

                                db.Notes.Add(existingNote);
                                localNotes[incNote.SyncId] = existingNote;
                                _historyService?.SaveSnapshot(db, existingNote);
                            }

                            state.ParentRevisionId = state.RevisionId;
                            state.RevisionId = incNote.RevisionId;
                            state.UpdatedAtUtc = incNote.UpdatedAtUtc;
                            state.IsDeleted = false;
                            state.DeletedAtUtc = null;
                            state.DeviceId = incNote.DeviceId;
                            state.ContentHash = SyncFingerprintHelper.ComputeNoteFingerprint(
                                existingNote.IsProtected ? string.Empty : (existingNote.Title ?? string.Empty),
                                existingNote.Text,
                                existingNote.IsPinned,
                                existingNote.IsFavorite,
                                existingNote.IsInbox,
                                existingNote.SourceProcessName,
                                existingNote.SourceWindowTitle,
                                existingNote.SourceUrl,
                                existingNote.CapturedAt.HasValue ? SyncPackageExporter.NormalizeToUtc(existingNote.CapturedAt.Value) : null,
                                false,
                                existingNote.NoteTags.Where(nt => nt.Tag != null).Select(nt => (nt.Tag.SyncId, nt.Origin, nt.IsSuppressed)),
                                existingNote.IsProtected,
                                existingNote.ProtectedFormatVersion,
                                existingNote.ProtectedKdfIterations,
                                existingNote.ProtectedSaltBase64,
                                existingNote.ProtectedNonceBase64,
                                existingNote.ProtectedTagBase64,
                                existingNote.ProtectedCiphertextBase64,
                                existingNote.IsProtected ? existingNote.ProtectedKdfDescriptor : null,
                                existingNote.IsProtected ? existingNote.ProtectedOriginalSyncId : null);
                            result.Notes.Updated++;
                        }
                    }
                    else
                    {
                        // Conflict
                        string? localNoteJson = null;
                        if (localNotes.TryGetValue(incNote.SyncId, out var existingNote))
                        {
                            localNoteJson = JsonSerializer.Serialize(new
                            {
                                Title = existingNote.IsProtected ? string.Empty : (existingNote.Title ?? string.Empty),
                                existingNote.Text,
                                existingNote.IsPinned,
                                existingNote.IsFavorite,
                                existingNote.IsInbox,
                                existingNote.SourceProcessName,
                                existingNote.SourceWindowTitle,
                                existingNote.SourceUrl,
                                CapturedAtUtc = existingNote.CapturedAt.HasValue ? SyncPackageExporter.NormalizeToUtc(existingNote.CapturedAt.Value) : (DateTime?)null,
                                DeletedAt = existingNote.DeletedAt,
                                Tags = existingNote.NoteTags.Where(nt => nt.Tag != null).Select(nt => new
                                {
                                    TagSyncId = nt.Tag!.SyncId,
                                    TagName = nt.Tag.Name,
                                    Origin = (int)nt.Origin,
                                    nt.IsSuppressed
                                }).ToList()
                            });
                        }
                        else if (state != null)
                        {
                            localNoteJson = JsonSerializer.Serialize(new { IsDeleted = state.IsDeleted, state.DeletedAtUtc });
                        }
                        string remoteNoteJson = JsonSerializer.Serialize(incNote);
                        RecordDurableConflict(db, result, recordedConflictKeys, incNote.SyncId, "Note", state?.RevisionId, incNote.RevisionId, incNote.ParentRevisionId, envelope.DeviceId, envelope.PackageId, "Конфликт ветвления ревизий заметки: входящий родитель не совпадает с текущей ревизией.", localNoteJson, remoteNoteJson);
                        result.Notes.Skipped++;
                    }
                }
                else
                {
                    // New Note
                    if (incNote.Operation == SyncOperationType.Delete)
                    {
                        state = new SyncEntityState
                        {
                            SyncId = incNote.SyncId,
                            EntityType = "Note",
                            RevisionId = incNote.RevisionId,
                            ParentRevisionId = incNote.ParentRevisionId,
                            DeviceId = incNote.DeviceId,
                            UpdatedAtUtc = incNote.UpdatedAtUtc,
                            IsDeleted = true,
                            DeletedAtUtc = incNote.UpdatedAtUtc,
                            ContentHash = SyncFingerprintHelper.ComputeTombstoneFingerprint("Note")
                        };
                        db.SyncEntityStates.Add(state);
                        syncStates[incNote.SyncId] = state;
                        result.Notes.Skipped++;
                    }
                    else
                    {
                        var newNote = new Note
                        {
                            SyncId = incNote.SyncId,
                            Title = incNote.IsProtected ? string.Empty : (incNote.Title ?? string.Empty),
                            Text = incNote.IsProtected ? string.Empty : incNote.Text,
                            CreatedAt = incNote.CreatedAtUtc.ToLocalTime(),
                            UpdatedAt = incNote.UpdatedAtUtc.ToLocalTime(),
                            IsPinned = incNote.IsPinned,
                            IsFavorite = incNote.IsFavorite,
                            IsInbox = incNote.IsInbox,
                            SourceProcessName = incNote.IsProtected ? null : incNote.SourceProcessName,
                            SourceWindowTitle = incNote.IsProtected ? null : incNote.SourceWindowTitle,
                            SourceUrl = incNote.IsProtected ? null : incNote.SourceUrl,
                            CapturedAt = incNote.CapturedAtUtc?.ToLocalTime(),
                            IsProtected = incNote.IsProtected,
                            ProtectedFormatVersion = incNote.IsProtected ? incNote.ProtectedFormatVersion : 0,
                            ProtectedKdfIterations = incNote.IsProtected ? incNote.ProtectedKdfIterations : 0,
                            ProtectedKdfDescriptor = incNote.IsProtected ? incNote.ProtectedKdfDescriptor : null,
                            ProtectedSaltBase64 = incNote.IsProtected ? incNote.ProtectedSaltBase64 : string.Empty,
                            ProtectedNonceBase64 = incNote.IsProtected ? incNote.ProtectedNonceBase64 : string.Empty,
                            ProtectedTagBase64 = incNote.IsProtected ? incNote.ProtectedTagBase64 : string.Empty,
                            ProtectedCiphertextBase64 = incNote.IsProtected ? incNote.ProtectedCiphertextBase64 : string.Empty,
                            ProtectedOriginalSyncId = incNote.IsProtected ? incNote.ProtectedOriginalSyncId : null
                        };

                        foreach (var tagDto in incNote.Tags)
                        {
                            if (localTags.TryGetValue(tagDto.TagSyncId, out var newLinkedTag))
                            {
                                newNote.NoteTags.Add(new NoteTag
                                {
                                    Note = newNote,
                                    Tag = newLinkedTag,
                                    Origin = tagDto.Origin,
                                    IsSuppressed = tagDto.IsSuppressed
                                });
                            }
                            else
                            {
                                result.Diagnostics.Add($"Тег {tagDto.TagSyncId} для новой заметки {incNote.SyncId} не найден.");
                            }
                        }

                        db.Notes.Add(newNote);
                        localNotes[incNote.SyncId] = newNote;

                        state = new SyncEntityState
                        {
                            SyncId = incNote.SyncId,
                            EntityType = "Note",
                            RevisionId = incNote.RevisionId,
                            ParentRevisionId = incNote.ParentRevisionId,
                            DeviceId = incNote.DeviceId,
                            UpdatedAtUtc = incNote.UpdatedAtUtc,
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
                                newNote.NoteTags.Where(nt => nt.Tag != null).Select(nt => (nt.Tag.SyncId, nt.Origin, nt.IsSuppressed)),
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
                        db.SyncEntityStates.Add(state);
                        syncStates[incNote.SyncId] = state;
                        result.Notes.Created++;

                        _historyService?.SaveSnapshot(db, newNote);
                    }
                }
            }

            // Persist notes so attachments can find local integer NoteId
            await db.SaveChangesAsync();

            // C. Apply Templates
            foreach (var incTmpl in payload.Templates)
            {
                if (syncStates.TryGetValue(incTmpl.SyncId, out var state))
                {
                    if (incTmpl.RevisionId == state.RevisionId)
                    {
                        result.Templates.Skipped++;
                        continue;
                    }

                    if (incTmpl.ParentRevisionId == state.RevisionId)
                    {
                        if (incTmpl.Operation == SyncOperationType.Delete)
                        {
                            if (localTemplates.TryGetValue(incTmpl.SyncId, out var existingTmpl))
                            {
                                db.NoteTemplates.Remove(existingTmpl);
                                localTemplates.Remove(incTmpl.SyncId);
                            }
                            state.ParentRevisionId = state.RevisionId;
                            state.RevisionId = incTmpl.RevisionId;
                            state.IsDeleted = true;
                            state.DeletedAtUtc = incTmpl.UpdatedAtUtc;
                            state.UpdatedAtUtc = incTmpl.UpdatedAtUtc;
                            state.DeviceId = incTmpl.DeviceId;
                            state.ContentHash = SyncFingerprintHelper.ComputeTombstoneFingerprint("NoteTemplate");
                            result.Templates.Deleted++;
                        }
                        else
                        {
                            if (localTemplates.TryGetValue(incTmpl.SyncId, out var existingTmpl))
                            {
                                existingTmpl.Title = incTmpl.Title;
                                existingTmpl.Text = incTmpl.Text;
                                existingTmpl.UpdatedAt = incTmpl.UpdatedAtUtc.ToLocalTime();

                                var incTagIds = incTmpl.TagSyncIds.ToHashSet();
                                var toRemove = existingTmpl.TemplateTags
                                    .Where(tt => tt.Tag != null && !incTagIds.Contains(tt.Tag.SyncId))
                                    .ToList();

                                foreach (var tt in toRemove)
                                {
                                    db.NoteTemplateTags.Remove(tt);
                                    existingTmpl.TemplateTags.Remove(tt);
                                }

                                foreach (var tagSyncId in incTmpl.TagSyncIds)
                                {
                                    if (localTags.TryGetValue(tagSyncId, out var tmplTag))
                                    {
                                        if (!existingTmpl.TemplateTags.Any(tt => tt.TagId == tmplTag.Id))
                                        {
                                            existingTmpl.TemplateTags.Add(new NoteTemplateTag
                                            {
                                                Template = existingTmpl,
                                                Tag = tmplTag
                                            });
                                        }
                                    }
                                }
                            }
                            else
                            {
                                existingTmpl = new NoteTemplate
                                {
                                    SyncId = incTmpl.SyncId,
                                    Title = incTmpl.Title,
                                    Text = incTmpl.Text,
                                    CreatedAt = incTmpl.CreatedAtUtc.ToLocalTime(),
                                    UpdatedAt = incTmpl.UpdatedAtUtc.ToLocalTime()
                                };

                                foreach (var tagSyncId in incTmpl.TagSyncIds)
                                {
                                    if (localTags.TryGetValue(tagSyncId, out var newTmplTag))
                                    {
                                        existingTmpl.TemplateTags.Add(new NoteTemplateTag
                                        {
                                            Template = existingTmpl,
                                            Tag = newTmplTag
                                        });
                                    }
                                }

                                db.NoteTemplates.Add(existingTmpl);
                                localTemplates[incTmpl.SyncId] = existingTmpl;
                            }

                            state.ParentRevisionId = state.RevisionId;
                            state.RevisionId = incTmpl.RevisionId;
                            state.UpdatedAtUtc = incTmpl.UpdatedAtUtc;
                            state.IsDeleted = false;
                            state.DeletedAtUtc = null;
                            state.DeviceId = incTmpl.DeviceId;
                            state.ContentHash = SyncFingerprintHelper.ComputeTemplateFingerprint(
                                existingTmpl.Title,
                                existingTmpl.Text,
                                existingTmpl.TemplateTags.Where(tt => tt.Tag != null).Select(tt => tt.Tag.SyncId));
                            result.Templates.Updated++;
                        }
                    }
                    else
                    {
                        // Conflict
                        string? localTmplJson = null;
                        if (localTemplates.TryGetValue(incTmpl.SyncId, out var existingTmpl))
                        {
                            localTmplJson = JsonSerializer.Serialize(new
                            {
                                existingTmpl.Title,
                                existingTmpl.Text,
                                TagSyncIds = existingTmpl.TemplateTags.Where(tt => tt.Tag != null).Select(tt => tt.Tag.SyncId).ToList()
                            });
                        }
                        else if (state != null)
                        {
                            localTmplJson = JsonSerializer.Serialize(new { IsDeleted = state.IsDeleted, state.DeletedAtUtc });
                        }
                        string remoteTmplJson = JsonSerializer.Serialize(incTmpl);
                        RecordDurableConflict(db, result, recordedConflictKeys, incTmpl.SyncId, "NoteTemplate", state?.RevisionId, incTmpl.RevisionId, incTmpl.ParentRevisionId, envelope.DeviceId, envelope.PackageId, "Конфликт ветвления ревизий шаблона: входящий родитель не совпадает с текущей ревизией.", localTmplJson, remoteTmplJson);
                        result.Templates.Skipped++;
                    }
                }
                else
                {
                    if (incTmpl.Operation == SyncOperationType.Delete)
                    {
                        state = new SyncEntityState
                        {
                            SyncId = incTmpl.SyncId,
                            EntityType = "NoteTemplate",
                            RevisionId = incTmpl.RevisionId,
                            ParentRevisionId = incTmpl.ParentRevisionId,
                            DeviceId = incTmpl.DeviceId,
                            UpdatedAtUtc = incTmpl.UpdatedAtUtc,
                            IsDeleted = true,
                            DeletedAtUtc = incTmpl.UpdatedAtUtc,
                            ContentHash = SyncFingerprintHelper.ComputeTombstoneFingerprint("NoteTemplate")
                        };
                        db.SyncEntityStates.Add(state);
                        syncStates[incTmpl.SyncId] = state;
                        result.Templates.Skipped++;
                    }
                    else
                    {
                        var newTmpl = new NoteTemplate
                        {
                            SyncId = incTmpl.SyncId,
                            Title = incTmpl.Title,
                            Text = incTmpl.Text,
                            CreatedAt = incTmpl.CreatedAtUtc.ToLocalTime(),
                            UpdatedAt = incTmpl.UpdatedAtUtc.ToLocalTime()
                        };

                        foreach (var tagSyncId in incTmpl.TagSyncIds)
                        {
                            if (localTags.TryGetValue(tagSyncId, out var newTmplTag))
                            {
                                newTmpl.TemplateTags.Add(new NoteTemplateTag
                                {
                                    Template = newTmpl,
                                    Tag = newTmplTag
                                });
                            }
                        }

                        db.NoteTemplates.Add(newTmpl);
                        localTemplates[incTmpl.SyncId] = newTmpl;

                        state = new SyncEntityState
                        {
                            SyncId = incTmpl.SyncId,
                            EntityType = "NoteTemplate",
                            RevisionId = incTmpl.RevisionId,
                            ParentRevisionId = incTmpl.ParentRevisionId,
                            DeviceId = incTmpl.DeviceId,
                            UpdatedAtUtc = incTmpl.UpdatedAtUtc,
                            IsDeleted = false,
                            ContentHash = SyncFingerprintHelper.ComputeTemplateFingerprint(
                                newTmpl.Title,
                                newTmpl.Text,
                                newTmpl.TemplateTags.Where(tt => tt.Tag != null).Select(tt => tt.Tag.SyncId))
                        };
                        db.SyncEntityStates.Add(state);
                        syncStates[incTmpl.SyncId] = state;
                        result.Templates.Created++;
                    }
                }
            }

            // D. Apply Attachments
            foreach (var incAtt in payload.Attachments)
            {
                if (syncStates.TryGetValue(incAtt.SyncId, out var state))
                {
                    if (incAtt.RevisionId == state.RevisionId)
                    {
                        result.Attachments.Skipped++;
                        continue;
                    }

                    if (incAtt.ParentRevisionId == state.RevisionId)
                    {
                        if (incAtt.Operation == SyncOperationType.Delete)
                        {
                            if (localAttachments.TryGetValue(incAtt.SyncId, out var existingAtt))
                            {
                                db.NoteAttachments.Remove(existingAtt);
                                localAttachments.Remove(incAtt.SyncId);
                            }
                            state.ParentRevisionId = state.RevisionId;
                            state.RevisionId = incAtt.RevisionId;
                            state.IsDeleted = true;
                            state.DeletedAtUtc = incAtt.UpdatedAtUtc;
                            state.UpdatedAtUtc = incAtt.UpdatedAtUtc;
                            state.DeviceId = incAtt.DeviceId;
                            state.ContentHash = SyncFingerprintHelper.ComputeTombstoneFingerprint("NoteAttachment");
                            result.Attachments.Deleted++;
                        }
                        else
                        {
                            if (localAttachments.TryGetValue(incAtt.SyncId, out var existingAtt))
                            {
                                ApplyAttachmentFields(existingAtt, incAtt);
                                if (localNotes.TryGetValue(incAtt.NoteSyncId, out var parentNoteForExisting))
                                {
                                    existingAtt.Note = parentNoteForExisting;
                                    existingAtt.NoteId = parentNoteForExisting.Id;
                                }
                            }
                            else if (localNotes.TryGetValue(incAtt.NoteSyncId, out var parentNote))
                            {
                                existingAtt = new NoteAttachment
                                {
                                    SyncId = incAtt.SyncId,
                                    NoteId = parentNote.Id,
                                    Note = parentNote,
                                    CreatedAt = incAtt.CreatedAtUtc.ToLocalTime(),
                                    StoredFileName = string.Empty,
                                    RelativePath = string.Empty
                                };
                                ApplyAttachmentFields(existingAtt, incAtt);
                                db.NoteAttachments.Add(existingAtt);
                                localAttachments[incAtt.SyncId] = existingAtt;
                            }

                            state.ParentRevisionId = state.RevisionId;
                            state.RevisionId = incAtt.RevisionId;
                            state.UpdatedAtUtc = incAtt.UpdatedAtUtc;
                            state.IsDeleted = false;
                            state.DeletedAtUtc = null;
                            state.DeviceId = incAtt.DeviceId;
                            state.ContentHash = SyncFingerprintHelper.ComputeAttachmentFingerprint(
                                incAtt.NoteSyncId,
                                incAtt.OriginalFileName,
                                incAtt.ContentType,
                                incAtt.Size,
                                incAtt.Sha256,
                                incAtt.IsProtected,
                                incAtt.ProtectedFormatVersion,
                                incAtt.ProtectedKdfIterations,
                                incAtt.ProtectedSaltBase64,
                                incAtt.ProtectedNonceBase64,
                                incAtt.ProtectedTagBase64,
                                incAtt.ProtectedCiphertextBase64,
                                incAtt.IsProtected ? incAtt.ProtectedKdfDescriptor : null);
                            result.Attachments.Updated++;
                        }
                    }
                    else
                    {
                        // Conflict
                        string? localAttJson = null;
                        if (localAttachments.TryGetValue(incAtt.SyncId, out var existingAtt))
                        {
                            localAttJson = JsonSerializer.Serialize(new
                            {
                                existingAtt.OriginalFileName,
                                existingAtt.ContentType,
                                existingAtt.Size,
                                existingAtt.Sha256,
                                existingAtt.IsProtected,
                                existingAtt.ProtectedFormatVersion,
                                existingAtt.ProtectedKdfIterations,
                                existingAtt.ProtectedSaltBase64,
                                existingAtt.ProtectedNonceBase64,
                                existingAtt.ProtectedTagBase64,
                                existingAtt.ProtectedCiphertextBase64
                            });
                        }
                        else if (state != null)
                        {
                            localAttJson = JsonSerializer.Serialize(new { IsDeleted = state.IsDeleted, state.DeletedAtUtc });
                        }
                        string remoteAttJson = JsonSerializer.Serialize(incAtt);
                        RecordDurableConflict(db, result, recordedConflictKeys, incAtt.SyncId, "NoteAttachment", state?.RevisionId, incAtt.RevisionId, incAtt.ParentRevisionId, envelope.DeviceId, envelope.PackageId, "Конфликт ветвления ревизий вложения: входящий родитель не совпадает с текущей ревизией.", localAttJson, remoteAttJson);
                        result.Attachments.Skipped++;
                    }
                }
                else
                {
                    if (incAtt.Operation == SyncOperationType.Delete)
                    {
                        state = new SyncEntityState
                        {
                            SyncId = incAtt.SyncId,
                            EntityType = "NoteAttachment",
                            RevisionId = incAtt.RevisionId,
                            ParentRevisionId = incAtt.ParentRevisionId,
                            DeviceId = incAtt.DeviceId,
                            UpdatedAtUtc = incAtt.UpdatedAtUtc,
                            IsDeleted = true,
                            DeletedAtUtc = incAtt.UpdatedAtUtc,
                            ContentHash = SyncFingerprintHelper.ComputeTombstoneFingerprint("NoteAttachment")
                        };
                        db.SyncEntityStates.Add(state);
                        syncStates[incAtt.SyncId] = state;
                        result.Attachments.Skipped++;
                    }
                    else
                    {
                        if (localNotes.TryGetValue(incAtt.NoteSyncId, out var parentNote))
                        {
                            var newAtt = new NoteAttachment
                            {
                                SyncId = incAtt.SyncId,
                                NoteId = parentNote.Id,
                                Note = parentNote,
                                CreatedAt = incAtt.CreatedAtUtc.ToLocalTime(),
                                StoredFileName = string.Empty,
                                RelativePath = string.Empty
                            };
                            ApplyAttachmentFields(newAtt, incAtt);
                            db.NoteAttachments.Add(newAtt);
                            localAttachments[incAtt.SyncId] = newAtt;

                            state = new SyncEntityState
                            {
                                SyncId = incAtt.SyncId,
                                EntityType = "NoteAttachment",
                                RevisionId = incAtt.RevisionId,
                                ParentRevisionId = incAtt.ParentRevisionId,
                                DeviceId = incAtt.DeviceId,
                                UpdatedAtUtc = incAtt.UpdatedAtUtc,
                                IsDeleted = false,
                                ContentHash = SyncFingerprintHelper.ComputeAttachmentFingerprint(
                                    incAtt.NoteSyncId,
                                    incAtt.OriginalFileName,
                                    incAtt.ContentType,
                                    incAtt.Size,
                                    incAtt.Sha256,
                                    incAtt.IsProtected,
                                    incAtt.ProtectedFormatVersion,
                                    incAtt.ProtectedKdfIterations,
                                    incAtt.ProtectedSaltBase64,
                                    incAtt.ProtectedNonceBase64,
                                    incAtt.ProtectedTagBase64,
                                    incAtt.ProtectedCiphertextBase64,
                                    incAtt.IsProtected ? incAtt.ProtectedKdfDescriptor : null)
                            };
                            db.SyncEntityStates.Add(state);
                            syncStates[incAtt.SyncId] = state;
                            result.Attachments.Created++;
                        }
                        else
                        {
                            result.Diagnostics.Add($"Заметка {incAtt.NoteSyncId} для вложения {incAtt.SyncId} не найдена в локальной базе.");
                            result.Attachments.Skipped++;
                        }
                    }
                }
            }

            await db.SaveChangesAsync();
            await transaction.CommitAsync();

            return result;
        }
        catch (SyncKdfWorkBudgetExceededException)
        {
            await transaction.RollbackAsync();
            db.ChangeTracker.Clear();
            throw;
        }
        catch (OperationCanceledException)
        {
            await transaction.RollbackAsync();
            db.ChangeTracker.Clear();
            throw;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            db.ChangeTracker.Clear();
            ErrorLogService.Write("SyncPackageImporter.Apply", ex.GetType().Name);
            return SyncImportResult.Failure($"Ошибка транзакции при применении изменений пакета: {ErrorLogService.Sanitize(ex.Message)}");
        }
    }

    /// <summary>
    /// Applies all note fields from the sync DTO (including the protection envelope).
    /// Protected notes keep their ciphertext and technical parameters; their plaintext
    /// fields (Text, source context) are cleared to avoid leaking into local storage.
    /// </summary>
    private static void ApplyNoteFields(Note note, SyncNoteDto dto)
    {
        note.CreatedAt = dto.CreatedAtUtc.ToLocalTime();
        note.UpdatedAt = dto.UpdatedAtUtc.ToLocalTime();
        note.IsPinned = dto.IsPinned;
        note.IsFavorite = dto.IsFavorite;
        note.IsInbox = dto.IsInbox;
        note.CapturedAt = dto.CapturedAtUtc?.ToLocalTime();
        note.IsProtected = dto.IsProtected;
        note.ProtectedFormatVersion = dto.IsProtected ? dto.ProtectedFormatVersion : 0;
        note.ProtectedKdfIterations = dto.IsProtected ? dto.ProtectedKdfIterations : 0;
        note.ProtectedKdfDescriptor = dto.IsProtected ? dto.ProtectedKdfDescriptor : null;
        note.ProtectedSaltBase64 = dto.IsProtected ? dto.ProtectedSaltBase64 : string.Empty;
        note.ProtectedNonceBase64 = dto.IsProtected ? dto.ProtectedNonceBase64 : string.Empty;
        note.ProtectedTagBase64 = dto.IsProtected ? dto.ProtectedTagBase64 : string.Empty;
        note.ProtectedCiphertextBase64 = dto.IsProtected ? dto.ProtectedCiphertextBase64 : string.Empty;
        note.ProtectedOriginalSyncId = dto.IsProtected ? dto.ProtectedOriginalSyncId : null;
        note.Title = dto.IsProtected ? string.Empty : (dto.Title ?? string.Empty);
        note.Text = dto.IsProtected ? string.Empty : dto.Text;
        note.SourceProcessName = dto.IsProtected ? null : dto.SourceProcessName;
        note.SourceWindowTitle = dto.IsProtected ? null : dto.SourceWindowTitle;
        note.SourceUrl = dto.IsProtected ? null : dto.SourceUrl;
    }

    private static void ApplyAttachmentFields(NoteAttachment att, SyncAttachmentDto dto)
    {
        att.OriginalFileName = dto.OriginalFileName;
        att.ContentType = dto.ContentType;
        att.Size = dto.Size;
        att.Sha256 = dto.Sha256;
        att.IsProtected = dto.IsProtected;
        att.ProtectedFormatVersion = dto.IsProtected ? dto.ProtectedFormatVersion : 0;
        att.ProtectedKdfIterations = dto.IsProtected ? dto.ProtectedKdfIterations : 0;
        att.ProtectedKdfDescriptor = dto.IsProtected ? dto.ProtectedKdfDescriptor : null;
        att.ProtectedSaltBase64 = dto.IsProtected ? dto.ProtectedSaltBase64 : null;
        att.ProtectedNonceBase64 = dto.IsProtected ? dto.ProtectedNonceBase64 : null;
        att.ProtectedTagBase64 = dto.IsProtected ? dto.ProtectedTagBase64 : null;
        att.ProtectedCiphertextBase64 = dto.IsProtected ? dto.ProtectedCiphertextBase64 : null;
    }

    private static void RecordDurableConflict(
        QuickNotesDbContext db,
        SyncImportResult result,
        HashSet<(Guid SyncId, Guid RemoteRevId)> recordedConflictKeys,
        Guid syncId,
        string entityType,
        Guid? localRevisionId,
        Guid remoteRevisionId,
        Guid? parentRevisionId,
        Guid sourceDeviceId,
        Guid sourcePackageId,
        string reason,
        string? localDataJson,
        string? remoteDataJson)
    {
        result.Conflicts.Add(new SyncConflict(
            syncId,
            entityType,
            localRevisionId,
            remoteRevisionId,
            parentRevisionId,
            reason));

        var key = (syncId, remoteRevisionId);
        if (!recordedConflictKeys.Add(key))
            return;

        bool alreadyInDb = db.SyncConflicts.Any(c => c.SyncId == syncId && c.RemoteRevisionId == remoteRevisionId);
        if (!alreadyInDb)
        {
            db.SyncConflicts.Add(new SyncConflictRecord
            {
                SyncId = syncId,
                EntityType = entityType,
                LocalRevisionId = localRevisionId,
                RemoteRevisionId = remoteRevisionId,
                ParentRevisionId = parentRevisionId,
                SourceDeviceId = sourceDeviceId,
                SourcePackageId = sourcePackageId,
                DetectedAtUtc = DateTime.UtcNow,
                Reason = reason,
                LocalDataJson = localDataJson,
                RemoteDataJson = remoteDataJson,
                IsResolved = false
            });
        }
    }
}
