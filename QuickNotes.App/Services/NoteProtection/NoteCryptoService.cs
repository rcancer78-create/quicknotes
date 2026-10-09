using System;
using System.Security.Cryptography;
using System.Text;
using QuickNotes.App.Services.Crypto;

namespace QuickNotes.App.Services.NoteProtection;

/// <summary>
/// Controlled security failure for note protection operations.
/// Never includes the password, derived key, plaintext or partial decrypted data.
/// </summary>
public class NoteProtectionSecurityException : Exception
{
    public NoteProtectionSecurityException(string message) : base(message) { }
    public NoteProtectionSecurityException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// AES-256-GCM + PBKDF2-HMAC-SHA256 implementation of <see cref="INoteCryptoService"/>.
///
/// Envelope parameters (salt, nonce, tag, iterations, format version, explicit KDF descriptor)
/// are stored alongside the ciphertext; the password and derived key are never persisted and
/// never logged.
///
/// AAD = UTF8("QNNOTE|{formatVersion}|{syncId:D}|{objectType}") binds the ciphertext to the
/// format version, the note's SyncId and the type of the encrypted object (note text, revision,
/// attachment metadata, attachment content, source context, conflict snapshot, draft journal).
///
/// KDF descriptor versioning does not change that AAD string. Legacy envelopes without an
/// explicit descriptor keep a byte-for-byte identical AAD and are read with their exact
/// historical iteration count; the descriptor is metadata that is validated before any
/// derivation and whose tampering only makes AEAD authentication fail.
///
/// This stage does not change the effective PBKDF2 cost and does not adopt Argon2id.
/// </summary>
public class NoteCryptoService : INoteCryptoService
{
    public const string AlgorithmName = "AES-256-GCM";
    public const string KdfName = KdfAlgorithmIds.Pbkdf2HmacSha256;
    public const int KdfVersion = KdfDescriptorConstants.CurrentDescriptorVersion;
    public const int CurrentFormatVersionConst = 1;
    public const int DefaultIterationsConst = 120_000;
    public const int SaltByteSize = KdfDescriptorConstants.SaltByteSize;   // 256 bits
    public const int NonceByteSize = 12;  // 96 bits standard for GCM
    public const int TagByteSize = 16;    // 128 bits standard for GCM
    public const int KeyByteSize = 32;    // 256 bits

    public int CurrentFormatVersion => CurrentFormatVersionConst;
    public int DefaultIterations => DefaultIterationsConst;

    /// <summary>Descriptor written next to every new ciphertext produced by this instance.</summary>
    public KdfDescriptor CurrentDescriptor => KdfDescriptor.Pbkdf2(_iterations);

    private readonly int _iterations;

    public NoteCryptoService(int iterations = DefaultIterationsConst)
    {
        _iterations = iterations > 0 ? iterations : DefaultIterationsConst;
    }

    public static byte[] BuildAssociatedData(int formatVersion, Guid syncId, string objectType)
    {
        if (syncId == Guid.Empty)
        {
            throw new ArgumentException("SyncId не может быть пустым для защищённой заметки.", nameof(syncId));
        }
        if (string.IsNullOrWhiteSpace(objectType))
        {
            throw new ArgumentException("Тип зашифрованного объекта не может быть пустым.", nameof(objectType));
        }

        return Encoding.UTF8.GetBytes($"QNNOTE|{formatVersion}|{syncId:D}|{objectType}");
    }

    /// <summary>
    /// Resolves the KDF descriptor of a stored envelope.
    /// Null/empty descriptor text means a legacy envelope: it is accepted with the exact
    /// historical iteration count and salt length passed by the caller and flagged as needing
    /// migration. Rewriting is a separate explicit operation, never an implicit side effect.
    /// </summary>
    public KdfEnvelopeReading ResolveDescriptor(string? descriptorText, int legacyIterations, int legacySaltByteSize)
    {
        if (string.IsNullOrWhiteSpace(descriptorText))
        {
            // Legacy path: no descriptor was ever written. Keep historical defaults exactly.
            try
            {
                KdfDescriptor.ValidateIterations(legacyIterations, KdfDescriptorLimits.LocalEnvelope);
            }
            catch (KdfDescriptorValidationException ex)
            {
                throw new NoteProtectionSecurityException(
                    "Повреждены криптографические метаданные заметки: недопустимый work factor KDF.", ex);
            }

            if (legacySaltByteSize != SaltByteSize)
            {
                throw new NoteProtectionSecurityException("Неверный размер соли зашифрованного объекта.");
            }

            return new KdfEnvelopeReading(
                new KdfDescriptor(
                    KdfAlgorithmIds.Pbkdf2HmacSha256,
                    KdfDescriptorConstants.CurrentDescriptorVersion,
                    legacyIterations,
                    legacySaltByteSize),
                KdfEnvelopeState.LegacyMissingDescriptor);
        }

        if (!KdfDescriptor.TryParse(descriptorText, KdfDescriptorLimits.LocalEnvelope, out KdfDescriptor descriptor, out string error))
        {
            throw new NoteProtectionSecurityException("Неподдерживаемые параметры KDF зашифрованного объекта (" + error + ").");
        }

        return new KdfEnvelopeReading(descriptor, KdfEnvelopeState.Current);
    }

    public byte[] DeriveKey(string password, byte[] salt, int iterations)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("Пароль не может быть пустым.", nameof(password));
        }
        if (salt == null || salt.Length == 0)
        {
            throw new ArgumentException("Отсутствует соль для вывода ключа.", nameof(salt));
        }

        int effectiveIterations = iterations > 0 ? iterations : _iterations;

        // Bounds are checked before any expensive derivation (resource-exhaustion guard).
        try
        {
            KdfDescriptor.ValidateIterations(effectiveIterations, KdfDescriptorLimits.LocalEnvelope);
        }
        catch (KdfDescriptorValidationException ex)
        {
            throw new NoteProtectionSecurityException("Недопустимый work factor KDF защищённого объекта.", ex);
        }

        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
        KdfDiagnostics.CountPbkdf2Invocation();
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(
                passwordBytes,
                salt,
                effectiveIterations,
                HashAlgorithmName.SHA256,
                KeyByteSize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    public NoteCryptoObject EncryptWithKey(byte[] key, byte[] plaintext, int formatVersion, Guid syncId, string objectType)
    {
        if (key == null || key.Length != KeyByteSize)
        {
            throw new ArgumentException($"Ключ должен быть {KeyByteSize} байт.", nameof(key));
        }
        if (plaintext == null)
        {
            throw new ArgumentNullException(nameof(plaintext));
        }

        byte[] nonce = RandomNumberGenerator.GetBytes(NonceByteSize);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[TagByteSize];
        byte[] aad = BuildAssociatedData(formatVersion, syncId, objectType);

        using (var aesGcm = new AesGcm(key, TagByteSize))
        {
            aesGcm.Encrypt(nonce, plaintext, ciphertext, tag, aad);
        }

        return new NoteCryptoObject
        {
            Nonce = nonce,
            Tag = tag,
            Ciphertext = ciphertext
        };
    }

    public byte[] DecryptWithKey(byte[] key, byte[] ciphertext, byte[] nonce, byte[] tag, int formatVersion, Guid syncId, string objectType)
    {
        if (key == null || key.Length != KeyByteSize)
        {
            throw new ArgumentException($"Ключ должен быть {KeyByteSize} байт.", nameof(key));
        }
        if (ciphertext == null)
        {
            throw new ArgumentNullException(nameof(ciphertext));
        }
        if (nonce == null || nonce.Length != NonceByteSize)
        {
            throw new NoteProtectionSecurityException("Неверный размер nonce зашифрованного объекта.");
        }
        if (tag == null || tag.Length != TagByteSize)
        {
            throw new NoteProtectionSecurityException("Неверный размер authentication tag зашифрованного объекта.");
        }

        byte[] plaintext = new byte[ciphertext.Length];
        byte[] aad = BuildAssociatedData(formatVersion, syncId, objectType);

        try
        {
            using (var aesGcm = new AesGcm(key, TagByteSize))
            {
                aesGcm.Decrypt(nonce, ciphertext, tag, plaintext, aad);
            }
            return plaintext;
        }
        catch (CryptographicException ex)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new NoteProtectionSecurityException(
                "Неверный пароль или нарушение целостности защищённой заметки. Данные не были изменены.", ex);
        }
    }

    public NoteEncryptionResult Encrypt(string password, byte[] plaintext, Guid syncId, string objectType, int? iterations = null)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("Пароль не может быть пустым.", nameof(password));
        }
        if (plaintext == null)
        {
            throw new ArgumentNullException(nameof(plaintext));
        }

        int effectiveIterations = iterations.HasValue && iterations.Value > 0 ? iterations.Value : _iterations;
        byte[] salt = RandomNumberGenerator.GetBytes(SaltByteSize);
        byte[] key = DeriveKey(password, salt, effectiveIterations);

        try
        {
            var obj = EncryptWithKey(key, plaintext, CurrentFormatVersionConst, syncId, objectType);
            return new NoteEncryptionResult
            {
                FormatVersion = CurrentFormatVersionConst,
                KdfIterations = effectiveIterations,
                Salt = salt,
                Nonce = obj.Nonce,
                Tag = obj.Tag,
                Ciphertext = obj.Ciphertext
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public byte[] Decrypt(string password, byte[] ciphertext, byte[] salt, byte[] nonce, byte[] tag, Guid syncId, string objectType, int iterations)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("Пароль не может быть пустым.", nameof(password));
        }

        byte[] key = DeriveKey(password, salt, iterations);
        try
        {
            return DecryptWithKey(key, ciphertext, nonce, tag, CurrentFormatVersionConst, syncId, objectType);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
