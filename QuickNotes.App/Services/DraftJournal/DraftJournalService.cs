using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Models.DraftJournal;
using QuickNotes.App.Services.NoteProtection;

namespace QuickNotes.App.Services.DraftJournal;

public class DraftJournalService : IDraftJournalService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private sealed class DraftState
    {
        public long LastPersistedSequence { get; set; } = -1;
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
    }

    private readonly string _journalsDirectory;
    private readonly INoteProtectionService? _noteProtectionService;
    private readonly INoteCryptoService _crypto;
    private readonly IDraftJournalFileOperations _fileOperations;
    private readonly ConcurrentDictionary<string, DraftState> _states = new(StringComparer.OrdinalIgnoreCase);

    public string JournalsDirectory => _journalsDirectory;
    public IDraftJournalFileOperations FileOperations => _fileOperations;

    public DraftJournalService(
        string? baseDirectory = null,
        INoteProtectionService? noteProtectionService = null,
        INoteCryptoService? crypto = null,
        IDraftJournalFileOperations? fileOperations = null)
    {
        string root = baseDirectory ?? QuickNotesDbContext.GetDefaultProfileDirectory();
        _journalsDirectory = Path.Combine(root, "DraftJournals");
        _noteProtectionService = noteProtectionService;
        _crypto = crypto ?? new NoteCryptoService();
        _fileOperations = fileOperations ?? new DefaultDraftJournalFileOperations();
    }

    public static bool IsSafeIdentifier(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128)
            return false;

        foreach (char c in id)
        {
            if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_'))
                return false;
        }

        return true;
    }

    public string GenerateNewDraftId() => $"new-{Guid.NewGuid():N}";

    public string GetDraftIdForNote(int noteId) => $"note-{noteId}";

    private string GetJournalFilePath(string draftId)
    {
        if (!IsSafeIdentifier(draftId))
        {
            throw new ArgumentException($"Недопустимый идентификатор черновика: '{draftId}'", nameof(draftId));
        }

        return Path.Combine(_journalsDirectory, $"{draftId}.journal");
    }

    public async Task<DraftSaveResult> SaveJournalAsync(DraftJournalSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        if (snapshot == null)
            throw new ArgumentNullException(nameof(snapshot));

        if (!IsSafeIdentifier(snapshot.DraftId))
            return DraftSaveResult.Fail($"Недопустимый идентификатор черновика: '{snapshot.DraftId}'");

        DraftJournalEnvelope envelope;

        if (snapshot.IsProtected)
        {
            if (!snapshot.NoteId.HasValue)
            {
                return DraftSaveResult.Fail("Защищённый черновик должен содержать идентификатор заметки.");
            }

            var session = _noteProtectionService?.GetSession(snapshot.NoteId.Value);
            if (session == null || session.Key == null)
            {
                // Honest status: secure persistence unavailable, write NO plaintext!
                return DraftSaveResult.CryptoUnavailable(
                    "Ключевой материал защищённой заметки недоступен в текущей сессии. Черновик не сохранён в открытом виде.");
            }

            try
            {
                var payload = new DraftJournalProtectedPayload
                {
                    Version = 1,
                    Title = snapshot.Title,
                    Text = snapshot.Text,
                    SourceProcessName = snapshot.SourceProcessName,
                    SourceWindowTitle = snapshot.SourceWindowTitle,
                    SourceUrl = snapshot.SourceUrl,
                    CapturedAt = snapshot.CapturedAt,
                    Tags = snapshot.Tags,
                    Attachments = snapshot.Attachments
                };

                byte[] plaintextBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
                int formatVersion = _crypto.CurrentFormatVersion;
                Guid aadSyncId = snapshot.SyncId ?? session.SyncId;

                var cryptoObj = _crypto.EncryptWithKey(
                    session.Key,
                    plaintextBytes,
                    formatVersion,
                    aadSyncId,
                    NoteProtectedObjectType.DraftJournal);

                envelope = new DraftJournalEnvelope
                {
                    Version = DraftJournalEnvelope.CurrentFormatVersion,
                    DraftId = snapshot.DraftId,
                    NoteId = snapshot.NoteId,
                    SyncId = aadSyncId,
                    SavedAtUtc = DateTime.UtcNow,
                    SequenceNumber = snapshot.SequenceNumber,
                    IsProtected = true,
                    Title = null,
                    Text = null,
                    SourceProcessName = null,
                    SourceWindowTitle = null,
                    SourceUrl = null,
                    CapturedAt = null,
                    Tags = null,
                    Attachments = null,
                    Crypto = new DraftJournalCryptoEnvelope
                    {
                        FormatVersion = formatVersion,
                        NonceBase64 = Convert.ToBase64String(cryptoObj.Nonce),
                        TagBase64 = Convert.ToBase64String(cryptoObj.Tag),
                        CiphertextBase64 = Convert.ToBase64String(cryptoObj.Ciphertext)
                    }
                };
            }
            catch (Exception ex)
            {
                ErrorLogService.Write("DraftJournalService.Encrypt", ex);
                return DraftSaveResult.Fail("Ошибка шифрования черновика заметки.");
            }
        }
        else
        {
            envelope = new DraftJournalEnvelope
            {
                Version = DraftJournalEnvelope.CurrentFormatVersion,
                DraftId = snapshot.DraftId,
                NoteId = snapshot.NoteId,
                SyncId = snapshot.SyncId,
                SavedAtUtc = DateTime.UtcNow,
                SequenceNumber = snapshot.SequenceNumber,
                IsProtected = false,
                Title = snapshot.Title,
                Text = snapshot.Text,
                SourceProcessName = snapshot.SourceProcessName,
                SourceWindowTitle = snapshot.SourceWindowTitle,
                SourceUrl = snapshot.SourceUrl,
                CapturedAt = snapshot.CapturedAt,
                Tags = snapshot.Tags,
                Attachments = snapshot.Attachments,
                Crypto = null
            };
        }

        var state = _states.GetOrAdd(snapshot.DraftId, _ => new DraftState());
        await state.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (snapshot.SequenceNumber <= state.LastPersistedSequence)
            {
                return DraftSaveResult.Superseded(snapshot.SequenceNumber);
            }

            Directory.CreateDirectory(_journalsDirectory);

            string tempFile = Path.Combine(_journalsDirectory, $"{snapshot.DraftId}.{Guid.NewGuid():N}.tmp");
            string targetFile = GetJournalFilePath(snapshot.DraftId);

            try
            {
                await using (var fs = new FileStream(
                    tempFile,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough | FileOptions.Asynchronous))
                {
                    await JsonSerializer.SerializeAsync(fs, envelope, JsonOptions, cancellationToken).ConfigureAwait(false);
                    await fs.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                _fileOperations.ReplaceFileSafely(tempFile, targetFile);

                state.LastPersistedSequence = snapshot.SequenceNumber;
                return DraftSaveResult.Ok(snapshot.SequenceNumber, envelope.SavedAtUtc);
            }
            finally
            {
                if (_fileOperations.FileExists(tempFile))
                {
                    try { _fileOperations.DeleteFile(tempFile); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("DraftJournalService.Save", ex);
            return DraftSaveResult.Fail("Ошибка сохранения черновика заметки.");
        }
        finally
        {
            state.Semaphore.Release();
        }
    }

    public DraftJournalReadResult ReadJournal(string draftId)
    {
        if (!IsSafeIdentifier(draftId))
        {
            return DraftJournalReadResult.Corrupt("Недопустимый идентификатор черновика.");
        }

        string targetFile = GetJournalFilePath(draftId);
        if (!_fileOperations.FileExists(targetFile))
        {
            return DraftJournalReadResult.NotFound();
        }

        DraftJournalEnvelope? envelope;
        try
        {
            string json = File.ReadAllText(targetFile);
            if (string.IsNullOrWhiteSpace(json))
            {
                return DraftJournalReadResult.Corrupt("Файл черновика пуст.");
            }

            envelope = JsonSerializer.Deserialize<DraftJournalEnvelope>(json, JsonOptions);
            if (envelope == null)
            {
                return DraftJournalReadResult.Corrupt("Не удалось разобрать содержимое черновика.");
            }
        }
        catch (JsonException ex)
        {
            ErrorLogService.Write("DraftJournalService.Read.JsonCorrupt", ex);
            return DraftJournalReadResult.Corrupt("Повреждённый формат черновика.");
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("DraftJournalService.Read.Corrupt", ex);
            return DraftJournalReadResult.Corrupt("Ошибка чтения черновика.");
        }

        if (envelope.Version > DraftJournalEnvelope.CurrentFormatVersion)
        {
            return DraftJournalReadResult.UnsupportedVersion(envelope.Version);
        }

        if (envelope.IsProtected)
        {
            if (envelope.Crypto == null ||
                string.IsNullOrEmpty(envelope.Crypto.CiphertextBase64) ||
                string.IsNullOrEmpty(envelope.Crypto.NonceBase64) ||
                string.IsNullOrEmpty(envelope.Crypto.TagBase64))
            {
                return DraftJournalReadResult.Corrupt("Отсутствуют криптографические данные в защищённом черновике.");
            }

            if (!envelope.NoteId.HasValue)
            {
                return DraftJournalReadResult.Corrupt("В защищённом черновике отсутствует идентификатор заметки.");
            }

            var session = _noteProtectionService?.GetSession(envelope.NoteId.Value);
            if (session == null || session.Key == null)
            {
                return DraftJournalReadResult.ProtectedSessionRequired(envelope);
            }

            try
            {
                byte[] ciphertext = Convert.FromBase64String(envelope.Crypto.CiphertextBase64);
                byte[] nonce = Convert.FromBase64String(envelope.Crypto.NonceBase64);
                byte[] tag = Convert.FromBase64String(envelope.Crypto.TagBase64);
                Guid aadSyncId = envelope.SyncId ?? session.SyncId;

                byte[] plaintextBytes = _crypto.DecryptWithKey(
                    session.Key,
                    ciphertext,
                    nonce,
                    tag,
                    envelope.Crypto.FormatVersion,
                    aadSyncId,
                    NoteProtectedObjectType.DraftJournal);

                var payload = JsonSerializer.Deserialize<DraftJournalProtectedPayload>(plaintextBytes, JsonOptions);
                return DraftJournalReadResult.Ok(
                    envelope,
                    payload?.Title,
                    payload?.Text ?? string.Empty,
                    payload?.SourceProcessName,
                    payload?.SourceWindowTitle,
                    payload?.SourceUrl,
                    payload?.CapturedAt,
                    payload?.Tags,
                    payload?.Attachments);
            }
            catch (NoteProtectionSecurityException ex)
            {
                ErrorLogService.Write("DraftJournalService.Read.SecurityException", ex);
                return DraftJournalReadResult.Corrupt("Ошибка расшифровки защищённого черновика.");
            }
            catch (Exception ex)
            {
                ErrorLogService.Write("DraftJournalService.Read.ProtectedPayloadCorrupt", ex);
                return DraftJournalReadResult.Corrupt("Не удалось восстановить данные защищённого черновика.");
            }
        }

        return DraftJournalReadResult.Ok(
            envelope,
            envelope.Title,
            envelope.Text ?? string.Empty,
            envelope.SourceProcessName,
            envelope.SourceWindowTitle,
            envelope.SourceUrl,
            envelope.CapturedAt,
            envelope.Tags,
            envelope.Attachments);
    }

    public DraftJournalReadResult ReadJournalForNote(int noteId) => ReadJournal(GetDraftIdForNote(noteId));

    public IReadOnlyList<DraftJournalReadResult> GetAllJournals()
    {
        var list = new List<DraftJournalReadResult>();
        if (!Directory.Exists(_journalsDirectory))
            return list;

        string[] files;
        try
        {
            files = Directory.GetFiles(_journalsDirectory, "*.journal");
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("DraftJournalService.GetAllJournals", ex);
            return list;
        }

        foreach (var file in files)
        {
            string draftId = Path.GetFileNameWithoutExtension(file);
            if (IsSafeIdentifier(draftId))
            {
                list.Add(ReadJournal(draftId));
            }
        }

        return list;
    }

    public bool DeleteJournal(string draftId)
    {
        if (!IsSafeIdentifier(draftId))
            return false;

        var state = _states.GetOrAdd(draftId, _ => new DraftState());
        state.Semaphore.Wait();
        try
        {
            string path = GetJournalFilePath(draftId);
            if (_fileOperations.FileExists(path))
            {
                _fileOperations.DeleteFile(path);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("DraftJournalService.DeleteJournal", ex);
            return false;
        }
        finally
        {
            state.Semaphore.Release();
        }
    }

    public bool DeleteJournalIfContentMatches(string draftId, string? committedTitle, string? committedText)
    {
        if (!IsSafeIdentifier(draftId))
            return false;

        var state = _states.GetOrAdd(draftId, _ => new DraftState());
        state.Semaphore.Wait();
        try
        {
            var read = ReadJournal(draftId);
            if (read.Status != DraftJournalReadStatus.Success || read.Envelope == null)
            {
                // If not found, nothing to delete
                if (read.Status == DraftJournalReadStatus.NotFound)
                    return false;

                // Corrupted or protected without session: do not delete
                return false;
            }

            string draftTitle = read.DecryptedTitle ?? string.Empty;
            string draftText = read.DecryptedText ?? string.Empty;
            committedTitle ??= string.Empty;
            committedText ??= string.Empty;

            if (string.Equals(draftTitle, committedTitle, StringComparison.Ordinal) &&
                string.Equals(draftText, committedText, StringComparison.Ordinal))
            {
                string path = GetJournalFilePath(draftId);
                if (_fileOperations.FileExists(path))
                {
                    _fileOperations.DeleteFile(path);
                }

                // Prevent any earlier/stale callbacks from re-persisting this sequence
                state.LastPersistedSequence = Math.Max(state.LastPersistedSequence, read.Envelope.SequenceNumber);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("DraftJournalService.DeleteJournalIfContentMatches", ex);
            return false;
        }
        finally
        {
            state.Semaphore.Release();
        }
    }

    public bool HasJournal(string draftId)
    {
        if (!IsSafeIdentifier(draftId)) return false;
        string path = GetJournalFilePath(draftId);
        return File.Exists(path);
    }

    public bool HasJournalForNote(int noteId) => HasJournal(GetDraftIdForNote(noteId));
}
