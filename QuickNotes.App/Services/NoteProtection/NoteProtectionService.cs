using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Services.Crypto;

namespace QuickNotes.App.Services.NoteProtection;

/// <summary>
/// Coordinates per-note password protection across the note text, source context,
/// revision history, attachments and FTS index.
/// All multi-entity transformations are atomic (single DB transaction).
/// </summary>
public class NoteProtectionService : INoteProtectionService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly INoteCryptoService _crypto;
    private readonly IProtectedNoteSessionStore _sessions;
    private readonly IAttachmentStorageService _attachmentStorage;
    private readonly IProtectionFileAdapter _fileAdapter;

    /// <summary>The in-memory session store (exposed for wiring/disposal).</summary>
    public IProtectedNoteSessionStore SessionStore => _sessions;

    /// <summary>The file-system adapter for atomic attachment operations.</summary>
    public IProtectionFileAdapter FileAdapter => _fileAdapter;

    public NoteProtectionService(
        INoteCryptoService? crypto = null,
        IProtectedNoteSessionStore? sessions = null,
        IAttachmentStorageService? attachmentStorage = null,
        IProtectionFileAdapter? fileAdapter = null)
    {
        _crypto = crypto ?? new NoteCryptoService();
        _sessions = sessions ?? new ProtectedNoteSessionStore();
        _attachmentStorage = attachmentStorage ?? new AttachmentStorageService();
        _fileAdapter = fileAdapter ?? new PhysicalProtectionFileAdapter();
    }

    public bool IsUnlocked(int noteId) => _sessions.IsUnlocked(noteId);
    public ProtectedNoteSession? GetSession(int noteId) => _sessions.Get(noteId);
    public void LockNote(int noteId) => _sessions.Remove(noteId);
    public void WipeAllSessions() => _sessions.WipeAll();

    /// <inheritdoc />
    public KdfEnvelopeReading InspectKdfEnvelope(Note note)
        => ResolveNoteEnvelopeDescriptor(note);

    /// <inheritdoc />
    public bool NeedsKdfMigration(QuickNotesDbContext db, int noteId)
    {
        if (db == null)
        {
            return false;
        }

        var note = db.Notes.AsNoTracking().FirstOrDefault(n => n.Id == noteId);
        if (note == null || !note.IsProtected)
        {
            return false;
        }

        // Reading only: a legacy envelope is reported, never rewritten implicitly.
        return ResolveNoteEnvelopeDescriptor(note).NeedsMigration;
    }

    public bool IsProtected(QuickNotesDbContext db, int noteId)
    {
        if (db == null) return false;
        try
        {
            var note = db.Notes.AsNoTracking().FirstOrDefault(n => n.Id == noteId);
            return note?.IsProtected ?? false;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------
    // Protect (set password)
    // ------------------------------------------------------------------

    public NoteProtectionResult ProtectNote(QuickNotesDbContext db, int noteId, string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return NoteProtectionResult.Fail("Пароль не может быть пустым.");
        }

        var note = db.Notes
            .Include(n => n.Revisions)
            .Include(n => n.Attachments)
            .FirstOrDefault(n => n.Id == noteId);

        if (note == null)
        {
            return NoteProtectionResult.Fail($"Заметка #{noteId} не найдена.");
        }

        if (note.IsProtected)
        {
            return NoteProtectionResult.Fail("Заметка уже защищена паролем.");
        }

        // Build the plaintext payload BEFORE touching the DB so a failure leaves the note intact.
        var payload = new NoteProtectedPayload
        {
            Title = note.Title,
            Text = note.Text,
            SourceProcessName = note.SourceProcessName,
            SourceWindowTitle = note.SourceWindowTitle,
            SourceUrl = note.SourceUrl
        };

        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        NoteEncryptionResult envelope;
        try
        {
            envelope = _crypto.Encrypt(password, plaintext, note.SyncId, NoteProtectedObjectType.NoteEnvelope);
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("NoteProtection.Protect", ex);
            return NoteProtectionResult.Fail("Не удалось зашифровать заметку: " + ErrorLogService.Sanitize(ex.Message));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        byte[] key = _crypto.DeriveKey(password, envelope.Salt, envelope.KdfIterations);
        var createdFiles = new List<string>();
        var oldFilesToDelete = new List<string>();
        try
        {
            using var tx = db.Database.BeginTransaction();
            try
            {
                // 1. Note envelope
                note.IsProtected = true;
                note.ProtectedFormatVersion = envelope.FormatVersion;
                note.ProtectedKdfIterations = envelope.KdfIterations;
                note.ProtectedKdfDescriptor = envelope.KdfDescriptorText;
                note.ProtectedSaltBase64 = Convert.ToBase64String(envelope.Salt);
                note.ProtectedNonceBase64 = Convert.ToBase64String(envelope.Nonce);
                note.ProtectedTagBase64 = Convert.ToBase64String(envelope.Tag);
                note.ProtectedCiphertextBase64 = Convert.ToBase64String(envelope.Ciphertext);
                note.Title = string.Empty;
                note.Text = string.Empty;
                note.SourceProcessName = null;
                note.SourceWindowTitle = null;
                note.SourceUrl = null;

                // 2. Revisions: encrypt each existing revision body with a unique nonce
                foreach (var rev in note.Revisions.ToList())
                {
                    if (rev.IsProtected) continue;
                    EncryptRevisionInternal(key, note.SyncId, _crypto.CurrentDescriptor, rev, rev.Title, rev.Text);
                }

                // 3. Attachments: encrypt content file + original name with two-phase tracking
                foreach (var att in note.Attachments.ToList())
                {
                    if (att.IsProtected) continue;
                    EncryptAttachmentInternal(key, note.SyncId, att, _crypto.CurrentDescriptor, att.OriginalFileName, createdFiles, oldFilesToDelete);
                }

                db.SaveChanges();
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                // Rollback: clean up all new files created before commit
                foreach (var f in createdFiles)
                {
                    try { _fileAdapter.Delete(f); } catch { /* best effort */ }
                }
                throw;
            }

            // Post-commit: delete old plaintext files
            foreach (var oldFile in oldFilesToDelete)
            {
                try { _fileAdapter.Delete(oldFile); }
                catch (Exception ex) { ErrorLogService.Write("NoteProtection.Protect.DeleteOldAttachment", ex); }
            }
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("NoteProtection.Protect", ex);
            return NoteProtectionResult.Fail("Не удалось защитить заметку: " + ErrorLogService.Sanitize(ex.Message));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        // 4. Remove from persistent indexes (outside the DB transaction; best-effort)
        RemoveFromFts(db, noteId);

        // 5. Keep the note unlocked in this process
        var protectSession = new ProtectedNoteSession(noteId, note.SyncId, _crypto.DeriveKey(password, envelope.Salt, envelope.KdfIterations), payload)
        {
            KdfDescriptorText = envelope.KdfDescriptorText,
            KdfState = KdfEnvelopeState.Current
        };
        _sessions.Add(protectSession);

        return NoteProtectionResult.Ok();
    }

    // ------------------------------------------------------------------
    // Unlock / Lock
    // ------------------------------------------------------------------

    public NoteProtectedPayload UnlockNote(QuickNotesDbContext db, int noteId, string password)
    {
        var note = db.Notes.AsNoTracking().FirstOrDefault(n => n.Id == noteId);
        if (note == null)
        {
            throw new NoteProtectionSecurityException("Заметка не найдена.");
        }
        if (!note.IsProtected)
        {
            throw new NoteProtectionSecurityException("Заметка не защищена паролем.");
        }

        KdfEnvelopeReading reading = ResolveNoteEnvelopeDescriptor(note);
        byte[] key = VerifyPasswordAndDeriveKey(db, noteId, password);
        try
        {
            Guid aadSyncId = note.ProtectedOriginalSyncId ?? note.SyncId;
            byte[] plaintext = _crypto.DecryptWithKey(
                key,
                Convert.FromBase64String(note.ProtectedCiphertextBase64!),
                Convert.FromBase64String(note.ProtectedNonceBase64!),
                Convert.FromBase64String(note.ProtectedTagBase64!),
                note.ProtectedFormatVersion,
                aadSyncId,
                NoteProtectedObjectType.NoteEnvelope);

            NoteProtectedPayload payload;
            try
            {
                payload = JsonSerializer.Deserialize<NoteProtectedPayload>(plaintext, JsonOptions) ?? new NoteProtectedPayload();
                payload.Title ??= string.Empty;
            }
            catch (Exception ex)
            {
                throw new NoteProtectionSecurityException("Повреждены расшифрованные данные заметки.", ex);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }

            // The session records the descriptor that actually produced this key. A legacy row
            // (no stored descriptor) keeps its exact historical work factor here, so the eventual
            // authenticated rewrite migrates the envelope without changing the effective cost.
            var unlockSession = new ProtectedNoteSession(noteId, aadSyncId, key, payload)
            {
                KdfDescriptorText = reading.Descriptor.ToCanonicalText(),
                KdfState = reading.State
            };
            _sessions.Add(unlockSession);
            return payload;
        }
        catch (NoteProtectionSecurityException)
        {
            CryptographicOperations.ZeroMemory(key);
            throw;
        }
        catch (Exception ex)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new NoteProtectionSecurityException("Не удалось расшифровать заметку: " + ErrorLogService.Sanitize(ex.Message), ex);
        }
    }

    public byte[] VerifyPasswordAndDeriveKey(QuickNotesDbContext db, int noteId, string password)
    {
        var note = db.Notes.AsNoTracking().FirstOrDefault(n => n.Id == noteId);
        if (note == null)
        {
            throw new NoteProtectionSecurityException("Заметка не найдена.");
        }
        if (!note.IsProtected)
        {
            throw new NoteProtectionSecurityException("Заметка не защищена паролем.");
        }
        if (string.IsNullOrEmpty(note.ProtectedSaltBase64) ||
            string.IsNullOrEmpty(note.ProtectedCiphertextBase64) ||
            string.IsNullOrEmpty(note.ProtectedNonceBase64) ||
            string.IsNullOrEmpty(note.ProtectedTagBase64))
        {
            throw new NoteProtectionSecurityException("Повреждены криптографические метаданные заметки.");
        }

        KdfEnvelopeReading reading = ResolveNoteEnvelopeDescriptor(note);
        byte[] key = _crypto.DeriveKey(password, Convert.FromBase64String(note.ProtectedSaltBase64), reading.Descriptor.Iterations);
        try
        {
            Guid aadSyncId = note.ProtectedOriginalSyncId ?? note.SyncId;
            // Verify by attempting a full decrypt of the note envelope.
            byte[] plaintext = _crypto.DecryptWithKey(
                key,
                Convert.FromBase64String(note.ProtectedCiphertextBase64),
                Convert.FromBase64String(note.ProtectedNonceBase64),
                Convert.FromBase64String(note.ProtectedTagBase64),
                note.ProtectedFormatVersion,
                aadSyncId,
                NoteProtectedObjectType.NoteEnvelope);
            CryptographicOperations.ZeroMemory(plaintext);
            return key;
        }
        catch (NoteProtectionSecurityException)
        {
            CryptographicOperations.ZeroMemory(key);
            throw;
        }
    }

    // ------------------------------------------------------------------
    // Change password
    // ------------------------------------------------------------------

    public NoteProtectionResult ChangePassword(QuickNotesDbContext db, int noteId, string currentPassword, string newPassword)
    {
        if (string.IsNullOrEmpty(newPassword))
        {
            return NoteProtectionResult.Fail("Новый пароль не может быть пустым.");
        }

        var note = db.Notes
            .Include(n => n.Revisions)
            .Include(n => n.Attachments)
            .FirstOrDefault(n => n.Id == noteId);

        if (note == null)
        {
            return NoteProtectionResult.Fail($"Заметка #{noteId} не найдена.");
        }
        if (!note.IsProtected)
        {
            return NoteProtectionResult.Fail("Заметка не защищена паролем.");
        }

        // 1. Verify current password
        byte[] oldKey;
        try
        {
            oldKey = VerifyPasswordAndDeriveKey(db, noteId, currentPassword);
        }
        catch (NoteProtectionSecurityException ex)
        {
            return NoteProtectionResult.Fail(ex.Message);
        }

        try
        {
            Guid oldAadSyncId = note.ProtectedOriginalSyncId ?? note.SyncId;
            // 2. Decrypt current payload with old key
            byte[] plaintext = _crypto.DecryptWithKey(
                oldKey,
                Convert.FromBase64String(note.ProtectedCiphertextBase64!),
                Convert.FromBase64String(note.ProtectedNonceBase64!),
                Convert.FromBase64String(note.ProtectedTagBase64!),
                note.ProtectedFormatVersion,
                oldAadSyncId,
                NoteProtectedObjectType.NoteEnvelope);

            NoteProtectedPayload payload;
            try
            {
                payload = JsonSerializer.Deserialize<NoteProtectedPayload>(plaintext, JsonOptions) ?? new NoteProtectedPayload();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }

            // 3. Re-encrypt note envelope with new password
            NoteEncryptionResult newEnvelope = _crypto.Encrypt(newPassword, JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions), note.SyncId, NoteProtectedObjectType.NoteEnvelope);
            byte[] newKey = _crypto.DeriveKey(newPassword, newEnvelope.Salt, newEnvelope.KdfIterations);
            var createdFiles = new List<string>();
            var oldFilesToDelete = new List<string>();

            try
            {
                using var tx = db.Database.BeginTransaction();
                try
                {
                    note.ProtectedOriginalSyncId = null;
                    note.ProtectedFormatVersion = newEnvelope.FormatVersion;
                    note.ProtectedKdfIterations = newEnvelope.KdfIterations;
                    note.ProtectedKdfDescriptor = newEnvelope.KdfDescriptorText;
                    note.ProtectedSaltBase64 = Convert.ToBase64String(newEnvelope.Salt);
                    note.ProtectedNonceBase64 = Convert.ToBase64String(newEnvelope.Nonce);
                    note.ProtectedTagBase64 = Convert.ToBase64String(newEnvelope.Tag);
                    note.ProtectedCiphertextBase64 = Convert.ToBase64String(newEnvelope.Ciphertext);

                    // 4. Re-encrypt all revisions with the new key
                    foreach (var rev in note.Revisions.ToList())
                    {
                        if (!rev.IsProtected) continue;
                        var revPayload = DecryptRevisionPayloadInternal(oldKey, oldAadSyncId, rev);
                        EncryptRevisionInternal(newKey, note.SyncId, newEnvelope.KdfDescriptor, rev, revPayload.Title ?? string.Empty, revPayload.Text);
                    }

                    // 5. Re-encrypt all attachments with the new key (in-memory decrypt + fresh encrypt, no double-encryption)
                    foreach (var att in note.Attachments.ToList())
                    {
                        if (!att.IsProtected) continue;
                        RotateAttachmentInternal(oldKey, newKey, oldAadSyncId, note.SyncId, att, newEnvelope.KdfDescriptor, createdFiles, oldFilesToDelete);
                    }

                    db.SaveChanges();
                    tx.Commit();
                }
                catch
                {
                    tx.Rollback();
                    foreach (var f in createdFiles)
                    {
                        try { _fileAdapter.Delete(f); } catch { /* best effort */ }
                    }
                    throw;
                }

                // Post-commit: delete old files
                foreach (var oldFile in oldFilesToDelete)
                {
                    try { _fileAdapter.Delete(oldFile); }
                    catch (Exception ex) { ErrorLogService.Write("NoteProtection.ChangePassword.DeleteOldAttachment", ex); }
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(newKey);
            }

            // 6. Update in-memory session key
            _sessions.Remove(noteId);
            _sessions.Add(new ProtectedNoteSession(noteId, note.SyncId, _crypto.DeriveKey(newPassword, newEnvelope.Salt, newEnvelope.KdfIterations), payload)
            {
                KdfDescriptorText = newEnvelope.KdfDescriptorText,
                KdfState = KdfEnvelopeState.Current
            });

            return NoteProtectionResult.Ok();
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("NoteProtection.ChangePassword", ex);
            return NoteProtectionResult.Fail("Не удалось изменить пароль: " + ErrorLogService.Sanitize(ex.Message));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(oldKey);
        }
    }

    // ------------------------------------------------------------------
    // Remove protection
    // ------------------------------------------------------------------

    public NoteProtectionResult RemoveProtection(QuickNotesDbContext db, int noteId, string password)
    {
        var note = db.Notes
            .Include(n => n.Revisions)
            .Include(n => n.Attachments)
            .FirstOrDefault(n => n.Id == noteId);

        if (note == null)
        {
            return NoteProtectionResult.Fail($"Заметка #{noteId} не найдена.");
        }
        if (!note.IsProtected)
        {
            return NoteProtectionResult.Fail("Заметка не защищена паролем.");
        }

        byte[] key;
        try
        {
            key = VerifyPasswordAndDeriveKey(db, noteId, password);
        }
        catch (NoteProtectionSecurityException ex)
        {
            return NoteProtectionResult.Fail(ex.Message);
        }

        try
        {
            // Decrypt the note payload
            Guid aadSyncId = note.ProtectedOriginalSyncId ?? note.SyncId;
            byte[] plaintext = _crypto.DecryptWithKey(
                key,
                Convert.FromBase64String(note.ProtectedCiphertextBase64!),
                Convert.FromBase64String(note.ProtectedNonceBase64!),
                Convert.FromBase64String(note.ProtectedTagBase64!),
                note.ProtectedFormatVersion,
                aadSyncId,
                NoteProtectedObjectType.NoteEnvelope);

            NoteProtectedPayload payload;
            try
            {
                payload = JsonSerializer.Deserialize<NoteProtectedPayload>(plaintext, JsonOptions) ?? new NoteProtectedPayload();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }

            var createdFiles = new List<string>();
            var oldFilesToDelete = new List<string>();

            using var tx = db.Database.BeginTransaction();
            try
            {
                // 1. Restore note plaintext
                note.IsProtected = false;
                note.ProtectedOriginalSyncId = null;
                note.ProtectedFormatVersion = 0;
                note.ProtectedKdfIterations = 0;
                note.ProtectedKdfDescriptor = null;
                note.ProtectedSaltBase64 = string.Empty;
                note.ProtectedNonceBase64 = string.Empty;
                note.ProtectedTagBase64 = string.Empty;
                note.ProtectedCiphertextBase64 = string.Empty;
                note.Title = payload.Title ?? string.Empty;
                note.Text = payload.Text;
                note.SourceProcessName = payload.SourceProcessName;
                note.SourceWindowTitle = payload.SourceWindowTitle;
                note.SourceUrl = payload.SourceUrl;

                // 2. Restore revisions
                foreach (var rev in note.Revisions.ToList())
                {
                    if (!rev.IsProtected) continue;
                    var revPayload = DecryptRevisionPayloadInternal(key, aadSyncId, rev);
                    rev.IsProtected = false;
                    rev.ProtectedFormatVersion = 0;
                    rev.ProtectedKdfIterations = 0;
                    rev.ProtectedKdfDescriptor = null;
                    rev.ProtectedSaltBase64 = string.Empty;
                    rev.ProtectedNonceBase64 = string.Empty;
                    rev.ProtectedTagBase64 = string.Empty;
                    rev.ProtectedCiphertextBase64 = string.Empty;
                    rev.Title = revPayload.Title ?? string.Empty;
                    rev.Text = revPayload.Text;
                }

                // 3. Restore attachments (decrypt content file + name)
                foreach (var att in note.Attachments.ToList())
                {
                    if (!att.IsProtected) continue;
                    RestoreAttachmentInternal(key, aadSyncId, att, createdFiles, oldFilesToDelete);
                }

                db.SaveChanges();
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                foreach (var f in createdFiles)
                {
                    try { _fileAdapter.Delete(f); } catch { /* best effort */ }
                }
                throw;
            }

            // Post-commit: delete old encrypted files
            foreach (var oldFile in oldFilesToDelete)
            {
                try { _fileAdapter.Delete(oldFile); }
                catch (Exception ex) { ErrorLogService.Write("NoteProtection.RemoveProtection.DeleteOldAttachment", ex); }
            }

            // 4. Re-index FTS
            ReindexIntoFts(db, noteId, note.Title, payload.Text);

            // 5. Wipe session
            _sessions.Remove(noteId);

            return NoteProtectionResult.Ok();
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("NoteProtection.RemoveProtection", ex);
            return NoteProtectionResult.Fail("Не удалось снять защиту: " + ErrorLogService.Sanitize(ex.Message));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    // ------------------------------------------------------------------
    // Commit-path helpers (unlocked protected note save)
    // ------------------------------------------------------------------

    public void ApplyUnlockedEdits(QuickNotesDbContext db, Note note, string text, string? sourceProcessName, string? sourceWindowTitle, string? sourceUrl)
    {
        ApplyUnlockedEdits(db, note, note?.Title ?? string.Empty, text, sourceProcessName, sourceWindowTitle, sourceUrl);
    }

    public void ApplyUnlockedEdits(QuickNotesDbContext db, Note note, string title, string text, string? sourceProcessName, string? sourceWindowTitle, string? sourceUrl)
    {
        if (!note.IsProtected)
        {
            note.Title = title ?? string.Empty;
            note.Text = text;
            note.SourceProcessName = sourceProcessName;
            note.SourceWindowTitle = sourceWindowTitle;
            note.SourceUrl = sourceUrl;
            return;
        }

        var session = _sessions.Get(note.Id);
        if (session == null)
        {
            throw new NoteProtectionSecurityException("Заметка заблокирована. Разблокируйте её перед сохранением.");
        }

        // If title, content and source context have not changed, keep the existing ciphertext
        // to avoid generating a new random nonce and triggering unnecessary sync revisions.
        if (string.Equals(session.Payload.Title ?? string.Empty, title ?? string.Empty, StringComparison.Ordinal) &&
            string.Equals(session.Payload.Text, text ?? string.Empty, StringComparison.Ordinal) &&
            string.Equals(session.Payload.SourceProcessName, sourceProcessName, StringComparison.Ordinal) &&
            string.Equals(session.Payload.SourceWindowTitle, sourceWindowTitle, StringComparison.Ordinal) &&
            string.Equals(session.Payload.SourceUrl, sourceUrl, StringComparison.Ordinal) &&
            note.ProtectedOriginalSyncId == null &&
            !string.IsNullOrEmpty(note.ProtectedCiphertextBase64))
        {
            return;
        }

        session.Payload.Title = title ?? string.Empty;
        session.Payload.Text = text ?? string.Empty;
        session.Payload.SourceProcessName = sourceProcessName;
        session.Payload.SourceWindowTitle = sourceWindowTitle;
        session.Payload.SourceUrl = sourceUrl;

        var payload = new NoteProtectedPayload
        {
            Title = title ?? string.Empty,
            Text = text ?? string.Empty,
            SourceProcessName = sourceProcessName,
            SourceWindowTitle = sourceWindowTitle,
            SourceUrl = sourceUrl
        };

        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        try
        {
            Guid oldAadSyncId = note.ProtectedOriginalSyncId ?? note.SyncId;
            bool aadSyncIdChanged = oldAadSyncId != note.SyncId;

            var obj = _crypto.EncryptWithKey(session.Key, plaintext, _crypto.CurrentFormatVersion, note.SyncId, NoteProtectedObjectType.NoteEnvelope);
            note.ProtectedOriginalSyncId = null;
            session.SyncId = note.SyncId;
            note.ProtectedFormatVersion = _crypto.CurrentFormatVersion;
            note.ProtectedKdfIterations = note.ProtectedKdfIterations > 0 ? note.ProtectedKdfIterations : _crypto.DefaultIterations;
            note.ProtectedKdfDescriptor = session.KdfDescriptorText ?? _crypto.CurrentDescriptor.ToCanonicalText();
            session.KdfDescriptorText = note.ProtectedKdfDescriptor;
            session.KdfState = KdfEnvelopeState.Current;
            note.ProtectedNonceBase64 = Convert.ToBase64String(obj.Nonce);
            note.ProtectedTagBase64 = Convert.ToBase64String(obj.Tag);
            note.ProtectedCiphertextBase64 = Convert.ToBase64String(obj.Ciphertext);
            note.Title = string.Empty;
            note.Text = string.Empty;
            note.SourceProcessName = null;
            note.SourceWindowTitle = null;
            note.SourceUrl = null;

            if (aadSyncIdChanged && note.Id > 0)
            {
                var revs = db.NoteRevisions.Where(r => r.NoteId == note.Id).ToList();
                foreach (var rev in revs)
                {
                    if (!rev.IsProtected) continue;
                    var revPayload = DecryptRevisionPayloadInternal(session.Key, oldAadSyncId, rev);
                    EncryptRevisionInternal(session.Key, note.SyncId, ResolveSessionDescriptor(session), rev, revPayload.Title ?? string.Empty, revPayload.Text);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public void EncryptRevision(QuickNotesDbContext db, int noteId, NoteRevision revision, string plaintext)
        => EncryptRevision(db, noteId, revision, string.Empty, plaintext);

    public void EncryptRevision(QuickNotesDbContext db, int noteId, NoteRevision revision, string title, string plaintext)
    {
        var session = _sessions.Get(noteId);
        if (session == null)
        {
            throw new NoteProtectionSecurityException("Заметка заблокирована. Разблокируйте её перед сохранением.");
        }
        EncryptRevisionInternal(session.Key, session.SyncId, ResolveSessionDescriptor(session), revision, title, plaintext);
    }

    public NoteRevision? SaveProtectedRevision(QuickNotesDbContext db, Note note, string plaintext, int maxRevisions = 20)
        => SaveProtectedRevision(db, note, string.Empty, plaintext, maxRevisions);

    public NoteRevision? SaveProtectedRevision(QuickNotesDbContext db, Note note, string title, string plaintext, int maxRevisions = 20)
    {
        if (db == null) throw new ArgumentNullException(nameof(db));
        if (note == null) throw new ArgumentNullException(nameof(note));
        if (note.Id <= 0 || !note.IsProtected)
        {
            return null;
        }

        var session = _sessions.Get(note.Id);
        if (session == null)
        {
            throw new NoteProtectionSecurityException("Заметка заблокирована. Разблокируйте её перед сохранением.");
        }

        // No-op detection against the last revision (decrypted in memory only)
        var lastRevision = db.NoteRevisions
            .Where(r => r.NoteId == note.Id)
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .FirstOrDefault();

        if (lastRevision != null)
        {
            try
            {
                var lastPayload = lastRevision.IsProtected
                    ? DecryptRevisionPayloadInternal(session.Key, session.SyncId, lastRevision)
                    : new NoteRevisionProtectedPayload { Title = lastRevision.Title ?? string.Empty, Text = lastRevision.Text ?? string.Empty };

                if (string.Equals(lastPayload.Title, title ?? string.Empty, StringComparison.Ordinal) &&
                    string.Equals(lastPayload.Text, plaintext ?? string.Empty, StringComparison.Ordinal))
                {
                    return null;
                }
            }
            catch (NoteProtectionSecurityException)
            {
                // Corruption in last revision - do not treat as equivalent; create a new snapshot
            }
        }

        // Prune oldest revisions if capacity is reached
        int count = db.NoteRevisions.Count(r => r.NoteId == note.Id);
        if (count >= maxRevisions)
        {
            var oldest = db.NoteRevisions
                .Where(r => r.NoteId == note.Id)
                .OrderBy(r => r.CreatedAt)
                .ThenBy(r => r.Id)
                .Take(count - maxRevisions + 1)
                .ToList();
            db.NoteRevisions.RemoveRange(oldest);
        }

        var newRevision = new NoteRevision
        {
            NoteId = note.Id,
            CreatedAt = DateTime.Now,
            // For protected notes we do not leak tag names in plaintext revision snapshots.
            // Tag history is reconstructed from the current note tags when unlocked.
            TagsJson = "[]"
        };

        EncryptRevisionInternal(session.Key, session.SyncId, ResolveSessionDescriptor(session), newRevision, title ?? string.Empty, plaintext ?? string.Empty);
        db.NoteRevisions.Add(newRevision);
        return newRevision;
    }

    public string? DecryptRevisionText(QuickNotesDbContext db, int noteId, NoteRevision revision)
    {
        if (revision == null || !revision.IsProtected) return null;
        var session = _sessions.Get(noteId);
        if (session == null)
        {
            throw new NoteProtectionSecurityException("Заметка заблокирована. Разблокируйте её для просмотра истории.");
        }
        return DecryptRevisionInternal(session.Key, session.SyncId, revision);
    }

    public NoteRevisionProtectedPayload? DecryptRevisionPayload(QuickNotesDbContext db, int noteId, NoteRevision revision)
    {
        if (revision == null || !revision.IsProtected) return null;
        var session = _sessions.Get(noteId);
        if (session == null)
        {
            throw new NoteProtectionSecurityException("Заметка заблокирована. Разблокируйте её для просмотра истории.");
        }
        return DecryptRevisionPayloadInternal(session.Key, session.SyncId, revision);
    }

    public void EncryptAttachment(
        QuickNotesDbContext db,
        int noteId,
        NoteAttachment attachment,
        string originalFileName,
        string sourceFilePath,
        List<string>? createdFiles = null,
        List<string>? oldFilesToDelete = null)
    {
        var session = _sessions.Get(noteId);
        if (session == null)
        {
            throw new NoteProtectionSecurityException("Заметка заблокирована. Разблокируйте её перед добавлением вложений.");
        }
        bool localTracking = createdFiles == null;
        var created = createdFiles ?? new List<string>();
        var oldToDelete = oldFilesToDelete ?? new List<string>();
        try
        {
            EncryptAttachmentInternal(session.Key, session.SyncId, attachment, ResolveSessionDescriptor(session), originalFileName, created, oldToDelete);
            if (localTracking)
            {
                foreach (var f in oldToDelete)
                {
                    try { _fileAdapter.Delete(f); } catch { /* best effort */ }
                }
            }
        }
        catch
        {
            if (localTracking)
            {
                foreach (var f in created)
                {
                    try { _fileAdapter.Delete(f); } catch { /* best effort */ }
                }
            }
            throw;
        }
    }

    public void EncryptAttachment(QuickNotesDbContext db, int noteId, NoteAttachment attachment, string originalFileName, string sourceFilePath)
        => EncryptAttachment(db, noteId, attachment, originalFileName, sourceFilePath, null, null);

    public string DecryptAttachmentName(QuickNotesDbContext db, int noteId, NoteAttachment attachment)
    {
        var session = _sessions.Get(noteId);
        if (session == null)
        {
            throw new NoteProtectionSecurityException("Заметка заблокирована. Разблокируйте её для просмотра вложений.");
        }
        return DecryptAttachmentNameInternal(session.Key, session.SyncId, attachment);
    }

    public string DecryptAttachmentToTempFile(QuickNotesDbContext db, int noteId, NoteAttachment attachment)
    {
        var session = _sessions.Get(noteId);
        if (session == null)
        {
            throw new NoteProtectionSecurityException("Заметка заблокирована. Разблокируйте её для открытия вложений.");
        }

        string fullPath = _attachmentStorage.GetFullPath(attachment.RelativePath);
        if (!_fileAdapter.Exists(fullPath))
        {
            throw new FileNotFoundException("Файл вложения недоступен на диске.", fullPath);
        }

        byte[] content = ProtectedAttachmentFile.DecryptFile(
            _fileAdapter,
            session.Key,
            fullPath,
            attachment.ProtectedFormatVersion,
            session.SyncId,
            ResolveSessionDescriptor(session));
        try
        {
            string originalName = DecryptAttachmentNameInternal(session.Key, session.SyncId, attachment);
            string ext = Path.GetExtension(originalName);
            return ProtectedAttachmentFile.WriteTempFile(content, ext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    // ------------------------------------------------------------------
    // FTS / semantic
    // ------------------------------------------------------------------

    public void RemoveFromFts(QuickNotesDbContext db, int noteId)
    {
        try
        {
            db.Database.ExecuteSqlRaw("DELETE FROM NotesFts WHERE NoteId = {0};", noteId);
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("NoteProtection.RemoveFromFts", ex);
        }
    }

    public void ReindexIntoFts(QuickNotesDbContext db, int noteId, string text)
    {
        ReindexIntoFts(db, noteId, string.Empty, text);
    }

    public void ReindexIntoFts(QuickNotesDbContext db, int noteId, string title, string text)
    {
        try
        {
            db.Database.ExecuteSqlRaw("DELETE FROM NotesFts WHERE NoteId = {0};", noteId);
            db.Database.ExecuteSqlRaw("INSERT INTO NotesFts(NoteId, Title, Text) VALUES ({0}, {1}, {2});", noteId, title ?? string.Empty, text);
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("NoteProtection.ReindexIntoFts", ex);
        }
    }

    // ------------------------------------------------------------------
    // In-memory search
    // ------------------------------------------------------------------

    public IReadOnlyList<ProtectedSearchHit> SearchUnlockedInMemory(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<ProtectedSearchHit>();
        }

        var terms = SearchPreview.ExtractSearchTerms(query);
        if (terms.Count == 0)
        {
            return Array.Empty<ProtectedSearchHit>();
        }

        var hits = new List<ProtectedSearchHit>();
        foreach (var noteId in _sessions.UnlockedNoteIds)
        {
            var session = _sessions.Get(noteId);
            if (session == null) continue;

            string text = session.Payload.Text ?? string.Empty;
            string title = session.Payload.Title ?? string.Empty;
            string haystack = string.IsNullOrWhiteSpace(title) ? text : $"{title}\n{text}";
            bool match = terms.All(t => haystack.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
            if (match)
            {
                string displayTitle = QuickNotes.App.Helpers.NoteTitleHelper.GetDisplayTitle(title, text, 72);
                hits.Add(new ProtectedSearchHit(noteId, displayTitle));
            }
        }

        return hits;
    }

    // ------------------------------------------------------------------
    // Internal helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Descriptor a session's key was actually derived from. Legacy sessions carry the note's
    /// historical work factor here, so mirrored revision/attachment metadata stays truthful even
    /// when this service instance is configured with a different default.
    /// </summary>
    private KdfDescriptor ResolveSessionDescriptor(ProtectedNoteSession session)
    {
        string? text = session?.KdfDescriptorText;
        if (!string.IsNullOrWhiteSpace(text)
            && KdfDescriptor.TryParse(text, KdfDescriptorLimits.LocalEnvelope, out KdfDescriptor parsed, out _))
        {
            return parsed;
        }

        return _crypto.CurrentDescriptor;
    }

    /// <summary>
    /// Descriptor recorded on a stored attachment envelope row, or null when the row is legacy
    /// (no descriptor). Used to cross-check a QNAT v2 container against its owning record before
    /// AES-GCM decrypt; an unparsable stored descriptor fails closed.
    /// </summary>
    private static KdfDescriptor? ResolveStoredDescriptor(string? descriptorText)
    {
        if (string.IsNullOrWhiteSpace(descriptorText))
        {
            return null;
        }

        if (!KdfDescriptor.TryParse(descriptorText, KdfDescriptorLimits.LocalEnvelope, out KdfDescriptor parsed, out _))
        {
            throw new NoteProtectionSecurityException("Недопустимые параметры KDF зашифрованного вложения.");
        }

        return parsed;
    }

    /// <summary>
    /// Resolves the KDF descriptor stored with a note envelope without deriving any key.
    /// A missing descriptor is the legacy path: the row's own historical iteration count is
    /// validated and the envelope is flagged for an explicit, later migration.
    /// </summary>
    private KdfEnvelopeReading ResolveNoteEnvelopeDescriptor(Note note)
    {
        if (note == null)
        {
            throw new NoteProtectionSecurityException("Не удалось определить параметры KDF: заметка не передана.");
        }

        int legacyIterations = note.ProtectedKdfIterations > 0
            ? note.ProtectedKdfIterations
            : _crypto.DefaultIterations;

        int legacySaltByteSize = string.IsNullOrEmpty(note.ProtectedSaltBase64)
            ? NoteCryptoService.SaltByteSize
            : Convert.FromBase64String(note.ProtectedSaltBase64).Length;

        return _crypto.ResolveDescriptor(note.ProtectedKdfDescriptor, legacyIterations, legacySaltByteSize);
    }

    private void EncryptRevisionInternal(byte[] key, Guid syncId, KdfDescriptor descriptor, NoteRevision rev, string plaintext)
        => EncryptRevisionInternal(key, syncId, descriptor, rev, string.Empty, plaintext);

    private void EncryptRevisionInternal(byte[] key, Guid syncId, KdfDescriptor descriptor, NoteRevision rev, string title, string plaintext)
    {
        var payload = new NoteRevisionProtectedPayload
        {
            Version = 1,
            Title = title ?? string.Empty,
            Text = plaintext ?? string.Empty
        };
        byte[] plaintextBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        try
        {
            var obj = _crypto.EncryptWithKey(key, plaintextBytes, _crypto.CurrentFormatVersion, syncId, NoteProtectedObjectType.Revision);
            rev.IsProtected = true;
            rev.ProtectedFormatVersion = _crypto.CurrentFormatVersion;
            rev.ProtectedKdfIterations = 0; // revisions reuse the note salt/iterations; 0 means "inherit from note"
            // Revisions reuse the owning note's session key, so they mirror the note's own
            // descriptor rather than this service's configured default.
            rev.ProtectedKdfDescriptor = descriptor.ToCanonicalText();
            rev.ProtectedNonceBase64 = Convert.ToBase64String(obj.Nonce);
            rev.ProtectedTagBase64 = Convert.ToBase64String(obj.Tag);
            rev.ProtectedCiphertextBase64 = Convert.ToBase64String(obj.Ciphertext);
            rev.Title = string.Empty;
            rev.Text = string.Empty;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }
    }

    private NoteRevisionProtectedPayload DecryptRevisionPayloadInternal(byte[] key, Guid syncId, NoteRevision rev)
    {
        if (!rev.IsProtected)
        {
            return new NoteRevisionProtectedPayload
            {
                Version = 1,
                Title = rev.Title ?? string.Empty,
                Text = rev.Text ?? string.Empty
            };
        }

        if (string.IsNullOrEmpty(rev.ProtectedCiphertextBase64) ||
            string.IsNullOrEmpty(rev.ProtectedNonceBase64) ||
            string.IsNullOrEmpty(rev.ProtectedTagBase64))
        {
            throw new NoteProtectionSecurityException("Повреждены криптографические метаданные редакции заметки.");
        }

        byte[] plaintext = _crypto.DecryptWithKey(
            key,
            Convert.FromBase64String(rev.ProtectedCiphertextBase64),
            Convert.FromBase64String(rev.ProtectedNonceBase64),
            Convert.FromBase64String(rev.ProtectedTagBase64),
            rev.ProtectedFormatVersion,
            syncId,
            NoteProtectedObjectType.Revision);
        try
        {
            try
            {
                var payload = JsonSerializer.Deserialize<NoteRevisionProtectedPayload>(plaintext, JsonOptions);
                if (payload != null && payload.Version >= 1 && payload.Text != null)
                {
                    payload.Title ??= string.Empty;
                    return payload;
                }
            }
            catch
            {
                // Backward-compatibility: old revisions stored raw UTF-8 text string
            }

            return new NoteRevisionProtectedPayload
            {
                Version = 1,
                Title = string.Empty,
                Text = Encoding.UTF8.GetString(plaintext)
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private string DecryptRevisionInternal(byte[] key, Guid syncId, NoteRevision rev)
    {
        return DecryptRevisionPayloadInternal(key, syncId, rev).Text;
    }

    private void EncryptAttachmentInternal(
        byte[] key,
        Guid syncId,
        NoteAttachment att,
        KdfDescriptor descriptor,
        string originalFileName,
        List<string> createdFiles,
        List<string> oldFilesToDelete)
    {
        string fullPath = _attachmentStorage.GetFullPath(att.RelativePath);
        if (!_fileAdapter.Exists(fullPath))
        {
            throw new FileNotFoundException("Файл вложения не найден при защите заметки.", fullPath);
        }

        byte[] plaintext = _fileAdapter.ReadAllBytes(fullPath);
        byte[] container;
        try
        {
            container = ProtectedAttachmentFile.CreateContainer(key, plaintext, _crypto.CurrentFormatVersion, syncId, descriptor);
            byte[] verified = ProtectedAttachmentFile.DecryptContainer(key, container, _crypto.CurrentFormatVersion, syncId, descriptor);
            try
            {
                if (!verified.SequenceEqual(plaintext))
                {
                    throw new NoteProtectionSecurityException("Ошибка верификации целостности зашифрованного вложения.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(verified);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        string encryptedStoredName = $"{Guid.NewGuid():N}.qnat";
        string encryptedFullPath = Path.Combine(_attachmentStorage.AttachmentsDirectory, encryptedStoredName);

        _fileAdapter.WriteAllBytes(encryptedFullPath, container);
        createdFiles.Add(encryptedFullPath);
        if (!string.Equals(Path.GetFullPath(fullPath), Path.GetFullPath(encryptedFullPath), StringComparison.OrdinalIgnoreCase))
        {
            oldFilesToDelete.Add(fullPath);
        }

        byte[] nameBytes = Encoding.UTF8.GetBytes(originalFileName ?? string.Empty);
        try
        {
            var nameObj = _crypto.EncryptWithKey(key, nameBytes, _crypto.CurrentFormatVersion, syncId, NoteProtectedObjectType.AttachmentMeta);

            att.IsProtected = true;
            att.ProtectedFormatVersion = _crypto.CurrentFormatVersion;
            att.ProtectedKdfIterations = 0;
            att.ProtectedKdfDescriptor = descriptor.ToCanonicalText(); // mirror the owning note KDF
            att.ProtectedNonceBase64 = Convert.ToBase64String(nameObj.Nonce);
            att.ProtectedTagBase64 = Convert.ToBase64String(nameObj.Tag);
            att.ProtectedCiphertextBase64 = Convert.ToBase64String(nameObj.Ciphertext);
            att.OriginalFileName = "attachment.qnat";
            att.StoredFileName = encryptedStoredName;
            att.RelativePath = Path.Combine("Attachments", encryptedStoredName);
            att.Sha256 = AttachmentFileHelper.ComputeSha256(container);
            att.Size = container.Length;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nameBytes);
        }
    }

    private void RotateAttachmentInternal(
        byte[] oldKey,
        byte[] newKey,
        Guid oldSyncId,
        Guid newSyncId,
        NoteAttachment att,
        KdfDescriptor descriptor,
        List<string> createdFiles,
        List<string> oldFilesToDelete)
    {
        string oldFullPath = _attachmentStorage.GetFullPath(att.RelativePath);
        if (!_fileAdapter.Exists(oldFullPath))
        {
            throw new FileNotFoundException("Файл вложения не найден при смене пароля.", oldFullPath);
        }

        byte[] oldContainer = _fileAdapter.ReadAllBytes(oldFullPath);
        byte[] plaintext = ProtectedAttachmentFile.DecryptContainer(
            oldKey, oldContainer, att.ProtectedFormatVersion, oldSyncId, ResolveStoredDescriptor(att.ProtectedKdfDescriptor));
        string originalName = DecryptAttachmentNameInternal(oldKey, oldSyncId, att);

        byte[] newContainer;
        try
        {
            newContainer = ProtectedAttachmentFile.CreateContainer(newKey, plaintext, _crypto.CurrentFormatVersion, newSyncId, descriptor);
            byte[] verified = ProtectedAttachmentFile.DecryptContainer(newKey, newContainer, _crypto.CurrentFormatVersion, newSyncId, descriptor);
            try
            {
                if (!verified.SequenceEqual(plaintext))
                {
                    throw new NoteProtectionSecurityException("Ошибка верификации целостности вложения после смены пароля.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(verified);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        string newStoredName = $"{Guid.NewGuid():N}.qnat";
        string newFullPath = Path.Combine(_attachmentStorage.AttachmentsDirectory, newStoredName);

        _fileAdapter.WriteAllBytes(newFullPath, newContainer);
        createdFiles.Add(newFullPath);
        if (!string.Equals(Path.GetFullPath(oldFullPath), Path.GetFullPath(newFullPath), StringComparison.OrdinalIgnoreCase))
        {
            oldFilesToDelete.Add(oldFullPath);
        }

        byte[] nameBytes = Encoding.UTF8.GetBytes(originalName ?? string.Empty);
        try
        {
            var nameObj = _crypto.EncryptWithKey(newKey, nameBytes, _crypto.CurrentFormatVersion, newSyncId, NoteProtectedObjectType.AttachmentMeta);

            att.ProtectedFormatVersion = _crypto.CurrentFormatVersion;
            att.ProtectedKdfIterations = 0;
            att.ProtectedKdfDescriptor = descriptor.ToCanonicalText(); // mirror the owning note KDF
            att.ProtectedNonceBase64 = Convert.ToBase64String(nameObj.Nonce);
            att.ProtectedTagBase64 = Convert.ToBase64String(nameObj.Tag);
            att.ProtectedCiphertextBase64 = Convert.ToBase64String(nameObj.Ciphertext);
            att.OriginalFileName = "attachment.qnat";
            att.StoredFileName = newStoredName;
            att.RelativePath = Path.Combine("Attachments", newStoredName);
            att.Sha256 = AttachmentFileHelper.ComputeSha256(newContainer);
            att.Size = newContainer.Length;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nameBytes);
        }
    }

    private string DecryptAttachmentNameInternal(byte[] key, Guid syncId, NoteAttachment att)
    {
        if (!att.IsProtected)
        {
            return att.OriginalFileName;
        }

        if (string.IsNullOrEmpty(att.ProtectedCiphertextBase64) ||
            string.IsNullOrEmpty(att.ProtectedNonceBase64) ||
            string.IsNullOrEmpty(att.ProtectedTagBase64))
        {
            throw new NoteProtectionSecurityException("Повреждены криптографические метаданные вложения заметки.");
        }

        byte[] plaintext = _crypto.DecryptWithKey(
            key,
            Convert.FromBase64String(att.ProtectedCiphertextBase64),
            Convert.FromBase64String(att.ProtectedNonceBase64),
            Convert.FromBase64String(att.ProtectedTagBase64),
            att.ProtectedFormatVersion,
            syncId,
            NoteProtectedObjectType.AttachmentMeta);
        try
        {
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private void RestoreAttachmentInternal(
        byte[] key,
        Guid syncId,
        NoteAttachment att,
        List<string> createdFiles,
        List<string> oldFilesToDelete)
    {
        string encryptedPath = _attachmentStorage.GetFullPath(att.RelativePath);
        if (!_fileAdapter.Exists(encryptedPath))
        {
            throw new FileNotFoundException("Зашифрованный файл вложения не найден при снятии защиты.", encryptedPath);
        }

        byte[] containerBytes = _fileAdapter.ReadAllBytes(encryptedPath);
        byte[] content = ProtectedAttachmentFile.DecryptContainer(
            key, containerBytes, att.ProtectedFormatVersion, syncId, ResolveStoredDescriptor(att.ProtectedKdfDescriptor));
        string originalName = DecryptAttachmentNameInternal(key, syncId, att);

        try
        {
            string sha256 = AttachmentFileHelper.ComputeSha256(content);
            string safeOriginal = string.IsNullOrWhiteSpace(originalName) ? "file.bin" : Path.GetFileName(originalName);
            string storedFileName = AttachmentFileHelper.GetSafeStoredFileName(sha256, safeOriginal);
            string destFullPath = Path.Combine(_attachmentStorage.AttachmentsDirectory, storedFileName);

            if (!_fileAdapter.Exists(destFullPath))
            {
                _fileAdapter.WriteAllBytes(destFullPath, content);
                createdFiles.Add(destFullPath);
            }

            if (!string.Equals(Path.GetFullPath(encryptedPath), Path.GetFullPath(destFullPath), StringComparison.OrdinalIgnoreCase))
            {
                oldFilesToDelete.Add(encryptedPath);
            }

            att.IsProtected = false;
            att.ProtectedFormatVersion = 0;
            att.ProtectedKdfIterations = 0;
            att.ProtectedKdfDescriptor = null;
            att.ProtectedSaltBase64 = string.Empty;
            att.ProtectedNonceBase64 = string.Empty;
            att.ProtectedTagBase64 = string.Empty;
            att.ProtectedCiphertextBase64 = string.Empty;
            att.OriginalFileName = originalName;
            att.StoredFileName = storedFileName;
            att.RelativePath = Path.Combine("Attachments", storedFileName);
            att.Sha256 = sha256;
            att.Size = content.Length;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }
}
