using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QuickNotes.App.Services.Sync;

/// <summary>
/// Secure storage for the end-to-end cloud sync encryption password using Windows DPAPI (CurrentUser).
/// Strictly separated from SQLite database, settings.json, logs, and export payloads.
/// Employs atomic file replacement, memory zeroing of sensitive buffers, and safe deletion.
/// </summary>
public class DpapiSyncPasswordStorage : ISyncPasswordStorage, ISyncPasswordProvider
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("QuickNotes.SyncPassword.DPAPI.Entropy.v1");
    private readonly string _filePath;
    private readonly string _pendingFilePath;

    public DpapiSyncPasswordStorage(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            QuickNotes.App.Data.QuickNotesDbContext.GetDefaultProfileDirectory(),
            "sync_password.dat");
        _pendingFilePath = _filePath + ".pending";
    }

    public bool HasPassword()
    {
        try
        {
            return File.Exists(_filePath) && new FileInfo(_filePath).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public Task<string?> LoadPasswordAsync(CancellationToken ct = default)
        => ReadProtectedAsync(_filePath, ct);

    public async Task SavePendingPasswordAsync(string password, CancellationToken ct = default)
    {
        await WriteProtectedAsync(_pendingFilePath, password, ct).ConfigureAwait(false);
    }

    public Task<string?> LoadPendingPasswordAsync(CancellationToken ct = default)
        => ReadProtectedAsync(_pendingFilePath, ct);

    public Task PromotePendingPasswordAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            if (!File.Exists(_pendingFilePath) || new FileInfo(_pendingFilePath).Length == 0)
                throw new SyncSecurityException("Нет отложенного пароля для активации.");

            string directory = Path.GetDirectoryName(Path.GetFullPath(_filePath))!;
            Directory.CreateDirectory(directory);
            File.Move(_pendingFilePath, _filePath, overwrite: true);
        }
        catch (SyncSecurityException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorLogService.Write("DpapiSyncPasswordStorage.Promote", "Не удалось активировать отложенный пароль синхронизации.");
            throw new SyncSecurityException("Ошибка активации отложенного пароля синхронизации.", ex);
        }

        return Task.CompletedTask;
    }

    public Task DeletePendingPasswordAsync(CancellationToken ct = default)
        => SecureDeleteFileAsync(_pendingFilePath);

    public bool HasPendingPassword()
    {
        try
        {
            return File.Exists(_pendingFilePath) && new FileInfo(_pendingFilePath).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task SavePasswordAsync(string password, CancellationToken ct = default)
    {
        await WriteProtectedAsync(_filePath, password, ct).ConfigureAwait(false);
    }

    public async Task DeletePasswordAsync(CancellationToken ct = default)
    {
        await SecureDeleteFileAsync(_filePath).ConfigureAwait(false);
        await SecureDeleteFileAsync(_pendingFilePath).ConfigureAwait(false);
    }

    private async Task WriteProtectedAsync(string path, string password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("Пароль синхронизации не может быть пустым.", nameof(password));

        byte[] plaintextBytes = Encoding.UTF8.GetBytes(password);
        byte[] encryptedBytes;

        try
        {
            encryptedBytes = ProtectedData.Protect(plaintextBytes, Entropy, DataProtectionScope.CurrentUser);
        }
        finally
        {
            Array.Clear(plaintextBytes, 0, plaintextBytes.Length);
        }

        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);

        string tempFile = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(tempFile, encryptedBytes, ct).ConfigureAwait(false);
            File.Move(tempFile, path, overwrite: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorLogService.Write("DpapiSyncPasswordStorage.Save", "Не удалось сохранить зашифрованный пароль синхронизации.");
            throw new SyncSecurityException("Ошибка безопасной записи пароля синхронизации.", ex);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
    }

    private async Task<string?> ReadProtectedAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return null;

        byte[] encryptedBytes;
        try
        {
            encryptedBytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorLogService.Write("DpapiSyncPasswordStorage.Load", "Ошибка чтения файла пароля синхронизации.");
            throw new SyncSecurityException("Не удалось прочитать файл пароля синхронизации.", ex);
        }

        if (encryptedBytes.Length == 0)
            return null;

        byte[]? decryptedBytes = null;
        try
        {
            decryptedBytes = ProtectedData.Unprotect(encryptedBytes, Entropy, DataProtectionScope.CurrentUser);
            if (decryptedBytes == null || decryptedBytes.Length == 0)
                return null;

            return Encoding.UTF8.GetString(decryptedBytes);
        }
        catch (CryptographicException ex)
        {
            ErrorLogService.Write("DpapiSyncPasswordStorage.Load", "Не удалось расшифровать пароль синхронизации через DPAPI (изменился контекст пользователя или повреждён файл).");
            throw new SyncSecurityException("Не удалось расшифровать защищённый пароль синхронизации через DPAPI.", ex);
        }
        finally
        {
            if (decryptedBytes != null)
                Array.Clear(decryptedBytes, 0, decryptedBytes.Length);
        }
    }

    private static Task SecureDeleteFileAsync(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                try { File.WriteAllBytes(path, new byte[128]); } catch { }
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorLogService.Write("DpapiSyncPasswordStorage.Delete", "Не удалось удалить файл пароля синхронизации.");
            throw new SyncSecurityException("Ошибка безопасного удаления пароля синхронизации.", ex);
        }

        return Task.CompletedTask;
    }

    public Task<string?> GetPasswordAsync(CancellationToken ct = default)
    {
        return LoadPasswordAsync(ct);
    }
}
