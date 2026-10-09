using System;
using System.Security.Cryptography;
using System.Text;
using QuickNotes.App.Services.Crypto;

namespace QuickNotes.App.Services.Sync;

/// <summary>
/// Client-side AEAD encryption and key derivation using AES-256-GCM and PBKDF2-HMAC-SHA256.
/// Enforces authenticated encryption, random salt/nonce, an explicit versioned KDF descriptor
/// on new writes, work-factor bounds before derivation, zeroed key material and tamper detection.
///
/// KDF descriptor versioning does not change AAD: sync envelope AAD is built by
/// <c>SyncPackageEnvelope.GetAssociatedData()</c> from the header fields, and the descriptor is
/// non-secret metadata validated before derivation. Tampering with it changes the derived key,
/// so AEAD authentication fails closed.
///
/// This stage does not change the effective PBKDF2 cost and does not adopt Argon2id.
/// </summary>
public class SyncCryptoService : ISyncCryptoService
{
    public const string AlgorithmName = "AES-256-GCM";
    public const string KdfName = KdfAlgorithmIds.Pbkdf2HmacSha256;
    public const int KdfVersion = KdfDescriptorConstants.CurrentDescriptorVersion;
    public const int DefaultIterations = 100_000;
    public const int SaltByteSize = KdfDescriptorConstants.SaltByteSize; // 256 bits
    public const int NonceByteSize = 12; // 96 bits standard for GCM
    public const int TagByteSize = 16; // 128 bits standard for GCM
    public const int KeyByteSize = 32; // 256 bits

    private readonly int _iterations;
    public int Iterations => _iterations;

    /// <summary>Descriptor written next to every new ciphertext produced by this instance.</summary>
    public KdfDescriptor CurrentDescriptor => KdfDescriptor.Pbkdf2(_iterations);

    public SyncCryptoService(int iterations = DefaultIterations)
    {
        _iterations = iterations > 0 ? iterations : DefaultIterations;
    }

    public EncryptedPayloadResult EncryptPayload(byte[] plaintextBytes, string password, byte[] associatedData)
    {
        if (plaintextBytes == null)
            throw new ArgumentNullException(nameof(plaintextBytes));
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Пароль для шифрования не может быть пустым.", nameof(password));

        byte[] salt = RandomNumberGenerator.GetBytes(SaltByteSize);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceByteSize);
        byte[] key = DeriveKey(password, salt, _iterations);

        try
        {
            byte[] ciphertext = new byte[plaintextBytes.Length];
            byte[] tag = new byte[TagByteSize];

            using (var aesGcm = new AesGcm(key, TagByteSize))
            {
                aesGcm.Encrypt(nonce, plaintextBytes, ciphertext, tag, associatedData);
            }

            return new EncryptedPayloadResult
            {
                Ciphertext = ciphertext,
                Salt = salt,
                Nonce = nonce,
                Tag = tag,
                Algorithm = AlgorithmName,
                KdfAlgorithm = KdfName,
                KdfVersion = KdfVersion,
                KdfIterations = _iterations
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public byte[] DecryptPayload(byte[] ciphertext, byte[] salt, byte[] nonce, byte[] tag, string password, byte[] associatedData, int iterations, UntrustedInboundKdfWorkBudget? inboundKdfBudget = null)
    {
        if (ciphertext == null)
            throw new ArgumentNullException(nameof(ciphertext));
        if (salt == null || salt.Length == 0)
            throw new ArgumentException("Отсутствует соль для вывода ключа.", nameof(salt));
        if (nonce == null || nonce.Length != NonceByteSize)
            throw new ArgumentException($"Неверный размер nonce (ожидается {NonceByteSize} байт).", nameof(nonce));
        if (tag == null || tag.Length != TagByteSize)
            throw new ArgumentException($"Неверный размер auth tag (ожидается {TagByteSize} байт).", nameof(tag));
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Пароль для расшифровки не может быть пустым.", nameof(password));

        int effectiveIterations = iterations > 0 ? iterations : _iterations;
        try
        {
            KdfDescriptor.ValidateIterations(effectiveIterations, KdfDescriptorLimits.CloudEnvelope);
        }
        catch (KdfDescriptorValidationException ex)
        {
            throw new SyncSecurityException("Недопустимый work factor KDF.", ex);
        }

        inboundKdfBudget?.Reserve(effectiveIterations);
        byte[] key = DeriveKey(password, salt, effectiveIterations);

        try
        {
            byte[] plaintext = new byte[ciphertext.Length];
            using (var aesGcm = new AesGcm(key, TagByteSize))
            {
                aesGcm.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
            }
            return plaintext;
        }
        catch (CryptographicException ex)
        {
            throw new SyncSecurityException("Неверный пароль или нарушение целостности пакета синхронизации.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// Resolves the KDF parameters of a stored sync envelope.
    /// Empty descriptor text is the legacy path: the header's own algorithm/version/iterations
    /// are used exactly as before, and the envelope is flagged as needing migration.
    /// </summary>
    public KdfEnvelopeReading ResolveDescriptor(string? descriptorText, string? kdfAlgorithm, int kdfVersion, int iterations, int saltByteSize)
    {
        if (!string.IsNullOrWhiteSpace(descriptorText))
        {
            if (!KdfDescriptor.TryParse(descriptorText, KdfDescriptorLimits.CloudEnvelope, out KdfDescriptor descriptor, out string error))
            {
                throw new SyncSecurityException("Неподдерживаемые параметры KDF пакета синхронизации (" + error + ").");
            }

            return new KdfEnvelopeReading(descriptor, KdfEnvelopeState.Current);
        }

        // Legacy envelope: keep the historical contract (explicit algorithm id + version + N).
        if (!string.Equals(kdfAlgorithm, KdfName, StringComparison.OrdinalIgnoreCase))
        {
            throw new SyncSecurityException("Неподдерживаемые параметры криптографического конверта.");
        }

        if (kdfVersion != KdfVersion)
        {
            throw new SyncSecurityException("Неподдерживаемые параметры криптографического конверта.");
        }

        try
        {
            KdfDescriptor.ValidateIterations(iterations, KdfDescriptorLimits.CloudEnvelope);
        }
        catch (KdfDescriptorValidationException ex)
        {
            throw new SyncSecurityException("Недопустимый work factor KDF пакета синхронизации.", ex);
        }

        if (saltByteSize != SaltByteSize)
        {
            throw new SyncSecurityException("Неверный размер соли пакета синхронизации.");
        }

        return new KdfEnvelopeReading(
            new KdfDescriptor(KdfAlgorithmIds.Pbkdf2HmacSha256, KdfVersion, iterations, saltByteSize),
            KdfEnvelopeState.LegacyMissingDescriptor);
    }

    public static byte[] DeriveKey(string password, byte[] salt, int iterations)
    {
        try
        {
            KdfDescriptor.ValidateIterations(iterations, KdfDescriptorLimits.CloudEnvelope);
        }
        catch (KdfDescriptorValidationException ex)
        {
            throw new SyncSecurityException("Недопустимый work factor KDF.", ex);
        }

        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
        KdfDiagnostics.CountPbkdf2Invocation();
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(
                passwordBytes,
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                KeyByteSize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }
}
