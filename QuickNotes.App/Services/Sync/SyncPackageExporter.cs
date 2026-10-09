using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Models.Sync;
using QuickNotes.App.Services;
using QuickNotes.App.Services.Crypto;

namespace QuickNotes.App.Services.Sync;

public class SyncPackageExporter : ISyncPackageExporter
{
    private readonly ISyncCryptoService _cryptoService;
    private readonly IDeviceIdProvider _deviceIdProvider;
    private readonly IDateTimeProvider _dateTimeProvider;

    public static readonly JsonSerializerOptions DeterministicJsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public SyncPackageExporter(
        ISyncCryptoService cryptoService,
        IDeviceIdProvider deviceIdProvider,
        IDateTimeProvider? dateTimeProvider = null)
    {
        _cryptoService = cryptoService ?? throw new ArgumentNullException(nameof(cryptoService));
        _deviceIdProvider = deviceIdProvider ?? throw new ArgumentNullException(nameof(deviceIdProvider));
        _dateTimeProvider = dateTimeProvider ?? new SystemDateTimeProvider();
    }

    public static DateTime NormalizeToUtc(DateTime dt)
    {
        if (dt.Kind == DateTimeKind.Utc)
            return dt;
        if (dt.Kind == DateTimeKind.Local)
            return dt.ToUniversalTime();
        return DateTime.SpecifyKind(dt, DateTimeKind.Local).ToUniversalTime();
    }

    public static DateTime EnsureUtcKind(DateTime dt)
    {
        if (dt.Kind == DateTimeKind.Utc)
            return dt;
        return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
    }

    public SyncPackagePayload BuildDeterministicPayload(QuickNotesDbContext db, Guid deviceId, Guid? packageId = null)
    {
        if (deviceId == Guid.Empty)
        {
            deviceId = _deviceIdProvider.GetDeviceId();
            if (deviceId == Guid.Empty)
            {
                deviceId = Guid.NewGuid();
            }
        }

        var nowUtc = EnsureUtcKind(_dateTimeProvider.UtcNow);
        var payload = new SyncPackagePayload
        {
            PayloadVersion = 1,
            PackageId = packageId.HasValue && packageId.Value != Guid.Empty ? packageId.Value : Guid.NewGuid(),
            SourceDeviceId = deviceId,
            CreatedAtUtc = nowUtc
        };

        var notes = db.Notes
            .Include(n => n.NoteTags)
            .ThenInclude(nt => nt.Tag)
            .ToList();

        var tags = db.Tags
            .Include(t => t.Synonyms)
            .Include(t => t.ParentTag)
            .ToList();

        var templates = db.NoteTemplates
            .Include(t => t.TemplateTags)
            .ThenInclude(tt => tt.Tag)
            .ToList();

        var attachments = db.NoteAttachments
            .Include(a => a.Note)
            .ToList();

        var syncStates = db.SyncEntityStates.ToDictionary(s => s.SyncId);

        // 1. Tags
        var activeTagSyncIds = new HashSet<Guid>();
        string tagTombstoneHash = SyncFingerprintHelper.ComputeTombstoneFingerprint("Tag");

        foreach (var tag in tags)
        {
            activeTagSyncIds.Add(tag.SyncId);
            var tagSynonyms = tag.Synonyms.Select(s => s.Value).ToList();
            Guid? parentTagSyncId = tag.ParentTag?.SyncId ?? (tag.ParentTagId.HasValue ? tags.FirstOrDefault(t => t.Id == tag.ParentTagId.Value)?.SyncId : null);
            string currentHash = SyncFingerprintHelper.ComputeTagFingerprint(
                tag.Name,
                parentTagSyncId,
                tagSynonyms);

            if (!syncStates.TryGetValue(tag.SyncId, out var state))
            {
                state = new SyncEntityState
                {
                    SyncId = tag.SyncId,
                    EntityType = "Tag",
                    RevisionId = Guid.NewGuid(),
                    ParentRevisionId = null,
                    DeviceId = deviceId,
                    UpdatedAtUtc = nowUtc,
                    IsDeleted = false,
                    ContentHash = currentHash
                };
                db.SyncEntityStates.Add(state);
                syncStates[tag.SyncId] = state;
            }
            else
            {
                bool isFirstExportOrMigrated = string.IsNullOrEmpty(state.ContentHash);
                bool hasChanged = isFirstExportOrMigrated || !string.Equals(state.ContentHash, currentHash, StringComparison.Ordinal);

                if (hasChanged)
                {
                    state.ParentRevisionId = state.RevisionId;
                    state.RevisionId = Guid.NewGuid();
                    state.DeviceId = deviceId;
                    state.UpdatedAtUtc = nowUtc;
                    state.ContentHash = currentHash;
                    state.IsDeleted = false;
                    state.DeletedAtUtc = null;
                }
                else if (state.DeviceId == Guid.Empty)
                {
                    state.DeviceId = deviceId;
                }
            }

            payload.Tags.Add(new SyncTagDto
            {
                SyncId = tag.SyncId,
                RevisionId = state.RevisionId,
                ParentRevisionId = state.ParentRevisionId,
                DeviceId = state.DeviceId,
                UpdatedAtUtc = EnsureUtcKind(state.UpdatedAtUtc),
                Operation = SyncOperationType.Upsert,
                Name = tag.Name,
                ParentTagSyncId = parentTagSyncId,
                Synonyms = tagSynonyms
                    .OrderBy(s => s, StringComparer.Ordinal)
                    .ToList()
            });
        }

        // Tombstones for hard-deleted tags
        foreach (var state in syncStates.Values.Where(s => s.EntityType == "Tag"))
        {
            if (!activeTagSyncIds.Contains(state.SyncId))
            {
                bool isFirstExportOrMigrated = string.IsNullOrEmpty(state.ContentHash);
                bool hasChanged = isFirstExportOrMigrated || !state.IsDeleted || !string.Equals(state.ContentHash, tagTombstoneHash, StringComparison.Ordinal);

                if (hasChanged)
                {
                    state.ParentRevisionId = state.RevisionId;
                    state.RevisionId = Guid.NewGuid();
                    state.IsDeleted = true;
                    state.DeletedAtUtc = state.DeletedAtUtc ?? nowUtc;
                    state.UpdatedAtUtc = nowUtc;
                    state.DeviceId = deviceId;
                    state.ContentHash = tagTombstoneHash;
                }
                else if (state.DeviceId == Guid.Empty)
                {
                    state.DeviceId = deviceId;
                }

                payload.Tags.Add(new SyncTagDto
                {
                    SyncId = state.SyncId,
                    RevisionId = state.RevisionId,
                    ParentRevisionId = state.ParentRevisionId,
                    DeviceId = state.DeviceId,
                    UpdatedAtUtc = EnsureUtcKind(state.UpdatedAtUtc),
                    Operation = SyncOperationType.Delete
                });
            }
        }

        // 2. Notes
        var activeNoteSyncIds = new HashSet<Guid>();
        string noteTombstoneHash = SyncFingerprintHelper.ComputeTombstoneFingerprint("Note");

        foreach (var note in notes)
        {
            activeNoteSyncIds.Add(note.SyncId);
            bool isNoteDeleted = note.DeletedAt.HasValue;

            var noteTags = note.NoteTags
                .Where(nt => nt.Tag != null)
                .Select(nt => new SyncNoteTagDto
                {
                    TagSyncId = nt.Tag.SyncId,
                    Origin = nt.Origin,
                    IsSuppressed = nt.IsSuppressed
                })
                .OrderBy(nt => nt.TagSyncId)
                .ToList();

            // For protected notes never transmit plaintext - transmit the crypto envelope
            // (salt, nonce, tag, ciphertext, format version, derivation params).
            bool isProtected = note.IsProtected;
            string fingerprintTitle = isProtected ? string.Empty : (note.Title ?? string.Empty);
            string fingerprintText = isProtected ? string.Empty : (note.Text ?? string.Empty);
            string? fpSourceProcess = isProtected ? null : note.SourceProcessName;
            string? fpSourceWindow = isProtected ? null : note.SourceWindowTitle;
            string? fpSourceUrl = isProtected ? null : note.SourceUrl;

            string currentHash = isNoteDeleted
                ? noteTombstoneHash
                : SyncFingerprintHelper.ComputeNoteFingerprint(
                    fingerprintTitle,
                    fingerprintText,
                    note.IsPinned,
                    note.IsFavorite,
                    note.IsInbox,
                    fpSourceProcess,
                    fpSourceWindow,
                    fpSourceUrl,
                    note.CapturedAt.HasValue ? NormalizeToUtc(note.CapturedAt.Value) : null,
                    false,
                    noteTags.Select(nt => (nt.TagSyncId, nt.Origin, nt.IsSuppressed)),
                    isProtected,
                    note.ProtectedFormatVersion,
                    note.ProtectedKdfIterations,
                    note.ProtectedSaltBase64,
                    note.ProtectedNonceBase64,
                    note.ProtectedTagBase64,
                    note.ProtectedCiphertextBase64,
                    isProtected ? note.ProtectedKdfDescriptor : null,
                    isProtected ? note.ProtectedOriginalSyncId : null);

            if (!syncStates.TryGetValue(note.SyncId, out var state))
            {
                state = new SyncEntityState
                {
                    SyncId = note.SyncId,
                    EntityType = "Note",
                    RevisionId = Guid.NewGuid(),
                    ParentRevisionId = null,
                    DeviceId = deviceId,
                    UpdatedAtUtc = isNoteDeleted ? NormalizeToUtc(note.DeletedAt!.Value) : NormalizeToUtc(note.UpdatedAt),
                    IsDeleted = isNoteDeleted,
                    DeletedAtUtc = isNoteDeleted ? NormalizeToUtc(note.DeletedAt!.Value) : null,
                    ContentHash = currentHash
                };
                db.SyncEntityStates.Add(state);
                syncStates[note.SyncId] = state;
            }
            else
            {
                bool isFirstExportOrMigrated = string.IsNullOrEmpty(state.ContentHash);
                bool hasChanged = isFirstExportOrMigrated || !string.Equals(state.ContentHash, currentHash, StringComparison.Ordinal);

                if (hasChanged)
                {
                    state.ParentRevisionId = state.RevisionId;
                    state.RevisionId = Guid.NewGuid();
                    state.DeviceId = deviceId;
                    state.ContentHash = currentHash;
                    state.UpdatedAtUtc = nowUtc;

                    if (isNoteDeleted)
                    {
                        state.IsDeleted = true;
                        state.DeletedAtUtc = NormalizeToUtc(note.DeletedAt!.Value);
                    }
                    else
                    {
                        state.IsDeleted = false;
                        state.DeletedAtUtc = null;
                    }
                }
                else if (state.DeviceId == Guid.Empty)
                {
                    state.DeviceId = deviceId;
                }
            }

            if (state.IsDeleted)
            {
                payload.Notes.Add(new SyncNoteDto
                {
                    SyncId = note.SyncId,
                    RevisionId = state.RevisionId,
                    ParentRevisionId = state.ParentRevisionId,
                    DeviceId = state.DeviceId,
                    UpdatedAtUtc = EnsureUtcKind(state.UpdatedAtUtc),
                    Operation = SyncOperationType.Delete
                });
            }
            else
            {
                payload.Notes.Add(new SyncNoteDto
                {
                    SyncId = note.SyncId,
                    RevisionId = state.RevisionId,
                    ParentRevisionId = state.ParentRevisionId,
                    DeviceId = state.DeviceId,
                    UpdatedAtUtc = EnsureUtcKind(state.UpdatedAtUtc),
                    Operation = SyncOperationType.Upsert,
                    Title = isProtected ? string.Empty : (note.Title ?? string.Empty),
                    Text = isProtected ? string.Empty : (note.Text ?? string.Empty),
                    CreatedAtUtc = NormalizeToUtc(note.CreatedAt),
                    IsPinned = note.IsPinned,
                    IsFavorite = note.IsFavorite,
                    IsInbox = note.IsInbox,
                    SourceProcessName = isProtected ? null : note.SourceProcessName,
                    SourceWindowTitle = isProtected ? null : note.SourceWindowTitle,
                    SourceUrl = isProtected ? null : note.SourceUrl,
                    CapturedAtUtc = note.CapturedAt.HasValue ? NormalizeToUtc(note.CapturedAt.Value) : null,
                    Tags = noteTags,
                    IsProtected = isProtected,
                    ProtectedFormatVersion = isProtected ? note.ProtectedFormatVersion : 0,
                    ProtectedKdfIterations = isProtected ? note.ProtectedKdfIterations : 0,
                    ProtectedKdfDescriptor = isProtected ? note.ProtectedKdfDescriptor : null,
                    ProtectedSaltBase64 = isProtected ? (note.ProtectedSaltBase64 ?? string.Empty) : string.Empty,
                    ProtectedNonceBase64 = isProtected ? (note.ProtectedNonceBase64 ?? string.Empty) : string.Empty,
                    ProtectedTagBase64 = isProtected ? (note.ProtectedTagBase64 ?? string.Empty) : string.Empty,
                    ProtectedCiphertextBase64 = isProtected ? (note.ProtectedCiphertextBase64 ?? string.Empty) : string.Empty,
                    ProtectedOriginalSyncId = isProtected ? note.ProtectedOriginalSyncId : null
                });
            }
        }

        // Tombstones for hard-deleted notes
        foreach (var state in syncStates.Values.Where(s => s.EntityType == "Note"))
        {
            if (!activeNoteSyncIds.Contains(state.SyncId))
            {
                bool isFirstExportOrMigrated = string.IsNullOrEmpty(state.ContentHash);
                bool hasChanged = isFirstExportOrMigrated || !state.IsDeleted || !string.Equals(state.ContentHash, noteTombstoneHash, StringComparison.Ordinal);

                if (hasChanged)
                {
                    state.ParentRevisionId = state.RevisionId;
                    state.RevisionId = Guid.NewGuid();
                    state.IsDeleted = true;
                    state.DeletedAtUtc = state.DeletedAtUtc ?? nowUtc;
                    state.UpdatedAtUtc = nowUtc;
                    state.DeviceId = deviceId;
                    state.ContentHash = noteTombstoneHash;
                }
                else if (state.DeviceId == Guid.Empty)
                {
                    state.DeviceId = deviceId;
                }

                payload.Notes.Add(new SyncNoteDto
                {
                    SyncId = state.SyncId,
                    RevisionId = state.RevisionId,
                    ParentRevisionId = state.ParentRevisionId,
                    DeviceId = state.DeviceId,
                    UpdatedAtUtc = EnsureUtcKind(state.UpdatedAtUtc),
                    Operation = SyncOperationType.Delete
                });
            }
        }

        // 3. NoteTemplates
        var activeTemplateSyncIds = new HashSet<Guid>();
        string templateTombstoneHash = SyncFingerprintHelper.ComputeTombstoneFingerprint("NoteTemplate");

        foreach (var template in templates)
        {
            activeTemplateSyncIds.Add(template.SyncId);
            var tagSyncIds = template.TemplateTags
                .Where(tt => tt.Tag != null)
                .Select(tt => tt.Tag.SyncId)
                .Distinct()
                .OrderBy(g => g)
                .ToList();

            string currentHash = SyncFingerprintHelper.ComputeTemplateFingerprint(
                template.Title,
                template.Text,
                tagSyncIds);

            if (!syncStates.TryGetValue(template.SyncId, out var state))
            {
                state = new SyncEntityState
                {
                    SyncId = template.SyncId,
                    EntityType = "NoteTemplate",
                    RevisionId = Guid.NewGuid(),
                    ParentRevisionId = null,
                    DeviceId = deviceId,
                    UpdatedAtUtc = nowUtc,
                    IsDeleted = false,
                    ContentHash = currentHash
                };
                db.SyncEntityStates.Add(state);
                syncStates[template.SyncId] = state;
            }
            else
            {
                bool isFirstExportOrMigrated = string.IsNullOrEmpty(state.ContentHash);
                bool hasChanged = isFirstExportOrMigrated || !string.Equals(state.ContentHash, currentHash, StringComparison.Ordinal);

                if (hasChanged)
                {
                    state.ParentRevisionId = state.RevisionId;
                    state.RevisionId = Guid.NewGuid();
                    state.DeviceId = deviceId;
                    state.UpdatedAtUtc = nowUtc;
                    state.ContentHash = currentHash;
                    state.IsDeleted = false;
                    state.DeletedAtUtc = null;
                }
                else if (state.DeviceId == Guid.Empty)
                {
                    state.DeviceId = deviceId;
                }
            }

            payload.Templates.Add(new SyncTemplateDto
            {
                SyncId = template.SyncId,
                RevisionId = state.RevisionId,
                ParentRevisionId = state.ParentRevisionId,
                DeviceId = state.DeviceId,
                UpdatedAtUtc = EnsureUtcKind(state.UpdatedAtUtc),
                Operation = SyncOperationType.Upsert,
                Title = template.Title,
                Text = template.Text,
                CreatedAtUtc = NormalizeToUtc(template.CreatedAt),
                TagSyncIds = tagSyncIds
            });
        }

        // Tombstones for hard-deleted templates
        foreach (var state in syncStates.Values.Where(s => s.EntityType == "NoteTemplate"))
        {
            if (!activeTemplateSyncIds.Contains(state.SyncId))
            {
                bool isFirstExportOrMigrated = string.IsNullOrEmpty(state.ContentHash);
                bool hasChanged = isFirstExportOrMigrated || !state.IsDeleted || !string.Equals(state.ContentHash, templateTombstoneHash, StringComparison.Ordinal);

                if (hasChanged)
                {
                    state.ParentRevisionId = state.RevisionId;
                    state.RevisionId = Guid.NewGuid();
                    state.IsDeleted = true;
                    state.DeletedAtUtc = state.DeletedAtUtc ?? nowUtc;
                    state.UpdatedAtUtc = nowUtc;
                    state.DeviceId = deviceId;
                    state.ContentHash = templateTombstoneHash;
                }
                else if (state.DeviceId == Guid.Empty)
                {
                    state.DeviceId = deviceId;
                }

                payload.Templates.Add(new SyncTemplateDto
                {
                    SyncId = state.SyncId,
                    RevisionId = state.RevisionId,
                    ParentRevisionId = state.ParentRevisionId,
                    DeviceId = state.DeviceId,
                    UpdatedAtUtc = EnsureUtcKind(state.UpdatedAtUtc),
                    Operation = SyncOperationType.Delete
                });
            }
        }

        // 4. NoteAttachments
        var activeAttachmentSyncIds = new HashSet<Guid>();
        string attachmentTombstoneHash = SyncFingerprintHelper.ComputeTombstoneFingerprint("NoteAttachment");

        foreach (var attachment in attachments)
        {
            activeAttachmentSyncIds.Add(attachment.SyncId);

            var noteSyncId = attachment.Note?.SyncId ?? Guid.Empty;
            if (noteSyncId == Guid.Empty)
            {
                noteSyncId = db.Notes.Where(n => n.Id == attachment.NoteId).Select(n => n.SyncId).FirstOrDefault();
            }

            string currentHash = SyncFingerprintHelper.ComputeAttachmentFingerprint(
                noteSyncId,
                attachment.OriginalFileName,
                attachment.ContentType,
                attachment.Size,
                attachment.Sha256,
                attachment.IsProtected,
                attachment.ProtectedFormatVersion,
                attachment.ProtectedKdfIterations,
                attachment.ProtectedSaltBase64,
                attachment.ProtectedNonceBase64,
                attachment.ProtectedTagBase64,
                attachment.ProtectedCiphertextBase64,
                attachment.IsProtected ? attachment.ProtectedKdfDescriptor : null);

            if (!syncStates.TryGetValue(attachment.SyncId, out var state))
            {
                state = new SyncEntityState
                {
                    SyncId = attachment.SyncId,
                    EntityType = "NoteAttachment",
                    RevisionId = Guid.NewGuid(),
                    ParentRevisionId = null,
                    DeviceId = deviceId,
                    UpdatedAtUtc = nowUtc,
                    IsDeleted = false,
                    ContentHash = currentHash
                };
                db.SyncEntityStates.Add(state);
                syncStates[attachment.SyncId] = state;
            }
            else
            {
                bool isFirstExportOrMigrated = string.IsNullOrEmpty(state.ContentHash);
                bool hasChanged = isFirstExportOrMigrated || !string.Equals(state.ContentHash, currentHash, StringComparison.Ordinal);

                if (hasChanged)
                {
                    state.ParentRevisionId = state.RevisionId;
                    state.RevisionId = Guid.NewGuid();
                    state.DeviceId = deviceId;
                    state.UpdatedAtUtc = nowUtc;
                    state.ContentHash = currentHash;
                    state.IsDeleted = false;
                    state.DeletedAtUtc = null;
                }
                else if (state.DeviceId == Guid.Empty)
                {
                    state.DeviceId = deviceId;
                }
            }

            payload.Attachments.Add(new SyncAttachmentDto
            {
                SyncId = attachment.SyncId,
                NoteSyncId = noteSyncId,
                RevisionId = state.RevisionId,
                ParentRevisionId = state.ParentRevisionId,
                DeviceId = state.DeviceId,
                UpdatedAtUtc = EnsureUtcKind(state.UpdatedAtUtc),
                Operation = SyncOperationType.Upsert,
                OriginalFileName = attachment.OriginalFileName,
                ContentType = attachment.ContentType,
                Size = attachment.Size,
                Sha256 = attachment.Sha256,
                CreatedAtUtc = NormalizeToUtc(attachment.CreatedAt),
                IsProtected = attachment.IsProtected,
                ProtectedFormatVersion = attachment.ProtectedFormatVersion,
                ProtectedKdfIterations = attachment.ProtectedKdfIterations,
                ProtectedKdfDescriptor = attachment.ProtectedKdfDescriptor,
                ProtectedSaltBase64 = attachment.ProtectedSaltBase64,
                ProtectedNonceBase64 = attachment.ProtectedNonceBase64,
                ProtectedTagBase64 = attachment.ProtectedTagBase64,
                ProtectedCiphertextBase64 = attachment.ProtectedCiphertextBase64
            });
        }

        // Tombstones for hard-deleted attachments
        foreach (var state in syncStates.Values.Where(s => s.EntityType == "NoteAttachment"))
        {
            if (!activeAttachmentSyncIds.Contains(state.SyncId))
            {
                bool isFirstExportOrMigrated = string.IsNullOrEmpty(state.ContentHash);
                bool hasChanged = isFirstExportOrMigrated || !state.IsDeleted || !string.Equals(state.ContentHash, attachmentTombstoneHash, StringComparison.Ordinal);

                if (hasChanged)
                {
                    state.ParentRevisionId = state.RevisionId;
                    state.RevisionId = Guid.NewGuid();
                    state.IsDeleted = true;
                    state.DeletedAtUtc = state.DeletedAtUtc ?? nowUtc;
                    state.UpdatedAtUtc = nowUtc;
                    state.DeviceId = deviceId;
                    state.ContentHash = attachmentTombstoneHash;
                }
                else if (state.DeviceId == Guid.Empty)
                {
                    state.DeviceId = deviceId;
                }

                payload.Attachments.Add(new SyncAttachmentDto
                {
                    SyncId = state.SyncId,
                    NoteSyncId = Guid.Empty,
                    RevisionId = state.RevisionId,
                    ParentRevisionId = state.ParentRevisionId,
                    DeviceId = state.DeviceId,
                    UpdatedAtUtc = EnsureUtcKind(state.UpdatedAtUtc),
                    Operation = SyncOperationType.Delete
                });
            }
        }

        // Persist any newly tracked states or updated revisions
        db.SaveChanges();

        // 5. Deterministic sorting
        payload.Tags = payload.Tags.OrderBy(t => t.SyncId).ToList();
        payload.Notes = payload.Notes.OrderBy(n => n.SyncId).ToList();
        payload.Templates = payload.Templates.OrderBy(t => t.SyncId).ToList();
        payload.Attachments = payload.Attachments.OrderBy(a => a.SyncId).ToList();

        // Validate that no object in payload has Guid.Empty DeviceId
        foreach (var t in payload.Tags)
        {
            if (t.DeviceId == Guid.Empty)
                throw new InvalidOperationException($"Tag {t.SyncId} has empty DeviceId.");
        }
        foreach (var n in payload.Notes)
        {
            if (n.DeviceId == Guid.Empty)
                throw new InvalidOperationException($"Note {n.SyncId} has empty DeviceId.");
        }
        foreach (var tmpl in payload.Templates)
        {
            if (tmpl.DeviceId == Guid.Empty)
                throw new InvalidOperationException($"Template {tmpl.SyncId} has empty DeviceId.");
        }
        foreach (var att in payload.Attachments)
        {
            if (att.DeviceId == Guid.Empty)
                throw new InvalidOperationException($"Attachment {att.SyncId} has empty DeviceId.");
        }

        return payload;
    }

    public async Task<SyncExportResult> ExportPackageAsync(QuickNotesDbContext db, string password, Guid? deviceId = null, Guid? packageId = null)
    {
        await Task.Yield();

        if (string.IsNullOrEmpty(password))
        {
            return SyncExportResult.Failure("Пароль для шифрования пакета синхронизации не может быть пустым.");
        }

        try
        {
            var effectiveDeviceId = deviceId ?? _deviceIdProvider.GetDeviceId();
            var payload = BuildDeterministicPayload(db, effectiveDeviceId, packageId);

            byte[] plaintextBytes = JsonSerializer.SerializeToUtf8Bytes(payload, DeterministicJsonOptions);

            var envelope = new SyncPackageEnvelope
            {
                Magic = "QNSP",
                FormatVersion = 1,
                PackageId = payload.PackageId,
                DeviceId = effectiveDeviceId,
                CreatedAtUtc = payload.CreatedAtUtc,
                Crypto = new SyncCryptoHeader
                {
                    Algorithm = SyncCryptoService.AlgorithmName,
                    KdfAlgorithm = SyncCryptoService.KdfName,
                    KdfVersion = SyncCryptoService.KdfVersion,
                    KdfIterations = _cryptoService.Iterations,
                    KdfDescriptor = _cryptoService.CurrentDescriptor.ToCanonicalText()
                }
            };

            byte[] associatedData = envelope.GetAssociatedData();
            var encResult = _cryptoService.EncryptPayload(plaintextBytes, password, associatedData);

            envelope.Crypto.Algorithm = encResult.Algorithm;
            envelope.Crypto.KdfAlgorithm = encResult.KdfAlgorithm;
            envelope.Crypto.KdfVersion = encResult.KdfVersion;
            envelope.Crypto.KdfIterations = encResult.KdfIterations;
            envelope.Crypto.KdfDescriptor = encResult.KdfDescriptorText;
            envelope.Crypto.SaltBase64 = Convert.ToBase64String(encResult.Salt);
            envelope.Crypto.NonceBase64 = Convert.ToBase64String(encResult.Nonce);
            envelope.Crypto.TagBase64 = Convert.ToBase64String(encResult.Tag);
            envelope.EncryptedPayloadBase64 = Convert.ToBase64String(encResult.Ciphertext);

            string packageJson = JsonSerializer.Serialize(envelope, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });

            return new SyncExportResult
            {
                Success = true,
                PackageId = envelope.PackageId,
                ExportedNotesCount = payload.Notes.Count,
                ExportedTagsCount = payload.Tags.Count,
                ExportedTemplatesCount = payload.Templates.Count,
                ExportedAttachmentsCount = payload.Attachments.Count,
                PackageBytes = Encoding.UTF8.GetByteCount(packageJson),
                PackageJson = packageJson
            };
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("SyncPackageExporter.Export", ex.GetType().Name);
            return SyncExportResult.Failure($"Сбой при экспорте пакета синхронизации: {ErrorLogService.Sanitize(ex.Message)}");
        }
    }

    public async Task<SyncExportResult> ExportToFileAsync(QuickNotesDbContext db, string filePath, string password, Guid? deviceId = null, Guid? packageId = null)
    {
        var result = await ExportPackageAsync(db, password, deviceId, packageId);
        if (!result.Success || string.IsNullOrEmpty(result.PackageJson))
        {
            return result;
        }

        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tempPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(tempPath, result.PackageJson, Encoding.UTF8);
            File.Move(tempPath, filePath, true);

            return result;
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("SyncPackageExporter.ExportToFile", ex.GetType().Name);
            return SyncExportResult.Failure($"Не удалось сохранить файл пакета: {ErrorLogService.Sanitize(ex.Message)}");
        }
    }
}
