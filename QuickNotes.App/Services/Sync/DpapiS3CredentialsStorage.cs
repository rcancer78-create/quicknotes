using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuickNotes.App.Models.Sync;

namespace QuickNotes.App.Services.Sync;

/// <summary>
/// Secure storage for S3 Access Key ID and Secret Access Key using Windows DPAPI (CurrentUser).
/// Strictly separated from SQLite database, settings.json, logs, and export payloads.
/// Employs atomic file replacement, memory zeroing of sensitive buffers, and safe deletion.
/// </summary>
public class DpapiS3CredentialsStorage : IS3CredentialsStorage, IS3CredentialsProvider
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("QuickNotes.S3.DPAPI.Entropy.v1");
    private readonly string _filePath;

    private class CredentialPayload
    {
        public string AccessKeyId { get; set; } = string.Empty;
        public string SecretAccessKey { get; set; } = string.Empty;
    }

    public DpapiS3CredentialsStorage(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            QuickNotes.App.Data.QuickNotesDbContext.GetDefaultProfileDirectory(),
            "s3_credentials.dat");
    }

    public bool HasCredentials()
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

    public async Task<S3Credentials?> LoadCredentialsAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_filePath))
            return null;

        byte[] encryptedBytes;
        try
        {
            encryptedBytes = await File.ReadAllBytesAsync(_filePath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorLogService.Write("DpapiS3CredentialsStorage.Load", "Ошибка чтения файла учетных данных S3.");
            throw new SyncSecurityException("Не удалось прочитать файл учетных данных S3.", ex);
        }

        if (encryptedBytes.Length == 0)
            return null;

        byte[]? decryptedBytes = null;
        try
        {
            decryptedBytes = ProtectedData.Unprotect(encryptedBytes, Entropy, DataProtectionScope.CurrentUser);
            var payload = JsonSerializer.Deserialize<CredentialPayload>(decryptedBytes);
            if (payload == null || string.IsNullOrWhiteSpace(payload.AccessKeyId) || string.IsNullOrWhiteSpace(payload.SecretAccessKey))
            {
                return null;
            }

            return new S3Credentials(payload.AccessKeyId, payload.SecretAccessKey);
        }
        catch (CryptographicException ex)
        {
            ErrorLogService.Write("DpapiS3CredentialsStorage.Load", "Не удалось расшифровать учетные данные DPAPI (изменился контекст пользователя или повреждён файл).");
            throw new SyncSecurityException("Не удалось расшифровать защищённые ключи S3 через DPAPI.", ex);
        }
        catch (JsonException ex)
        {
            ErrorLogService.Write("DpapiS3CredentialsStorage.Load", "Повреждён формат расшифрованных учетных данных.");
            throw new SyncSecurityException("Повреждены данные ключей S3.", ex);
        }
        finally
        {
            if (decryptedBytes != null)
            {
                Array.Clear(decryptedBytes, 0, decryptedBytes.Length);
            }
        }
    }

    public async Task SaveCredentialsAsync(S3Credentials credentials, CancellationToken ct = default)
    {
        if (credentials == null)
            throw new ArgumentNullException(nameof(credentials));

        var payload = new CredentialPayload
        {
            AccessKeyId = credentials.AccessKeyId,
            SecretAccessKey = credentials.SecretAccessKey
        };

        byte[] plaintextBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        byte[] encryptedBytes;

        try
        {
            encryptedBytes = ProtectedData.Protect(plaintextBytes, Entropy, DataProtectionScope.CurrentUser);
        }
        finally
        {
            Array.Clear(plaintextBytes, 0, plaintextBytes.Length);
        }

        string directory = Path.GetDirectoryName(Path.GetFullPath(_filePath))!;
        Directory.CreateDirectory(directory);

        string tempFile = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(tempFile, encryptedBytes, ct).ConfigureAwait(false);
            File.Move(tempFile, _filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorLogService.Write("DpapiS3CredentialsStorage.Save", "Не удалось сохранить зашифрованные ключи S3.");
            throw new SyncSecurityException("Ошибка безопасной записи учетных данных S3.", ex);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
    }

    public Task DeleteCredentialsAsync(CancellationToken ct = default)
    {
        try
        {
            if (File.Exists(_filePath))
            {
                // Securely wipe existing file contents before deletion
                try
                {
                    File.WriteAllBytes(_filePath, new byte[128]);
                }
                catch { }

                File.Delete(_filePath);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorLogService.Write("DpapiS3CredentialsStorage.Delete", "Не удалось удалить файл ключей S3.");
            throw new SyncSecurityException("Ошибка безопасного удаления учетных данных S3.", ex);
        }

        return Task.CompletedTask;
    }

    public Task<S3Credentials?> GetCredentialsAsync(CancellationToken ct = default)
    {
        return LoadCredentialsAsync(ct);
    }
}
