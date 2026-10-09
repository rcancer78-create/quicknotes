using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using QuickNotes.App.Models;
using QuickNotes.App.Services.Crypto;

namespace QuickNotes.App.Services.NoteProtection;

/// <summary>
/// Encrypted-at-rest format for attachment files of protected notes.
///
/// BINARY ENVELOPE, VERSION 2 (current; little-endian via BinaryWriter):
///   [4 bytes]  Magic "QNAT" (0x51 0x4E 0x41 0x54)
///   [1 byte]   Version 0x02
///   [1 byte]   KDF algorithm id (0x01 = PBKDF2-HMAC-SHA256)
///   [2 bytes]  KDF descriptor version (uint16)
///   [4 bytes]  KDF iterations (int32)
///   [4 bytes]  Salt length (= 32)
///   [N bytes]  Salt (PBKDF2 salt of the owning note envelope)
///   [12 bytes] Nonce (AES-GCM)
///   [16 bytes] Auth tag (AES-GCM)
///   [8 bytes]  Plaintext size (int64)
///   [M bytes]  Ciphertext
///
/// BINARY ENVELOPE, VERSION 1 (legacy, still readable):
///   Magic, version 0x01, salt length, salt, nonce, tag, plaintext size, ciphertext.
///   No KDF descriptor: readers use the owning note's historical iteration count and flag
///   the file as needing migration. Version 1 bytes are left untouched.
///
/// AAD = UTF8("QNNOTE|{formatVersion}|{syncId:D}|attachment-content") - identical for v1 and v2.
///
/// The descriptor is metadata, not key material: QNAT stores the work factor but never the salt or
/// key. DecryptContainer receives an already-derived key, so the descriptor cannot itself change
/// that key. Instead, a v2 container's descriptor is validated (algorithm id, descriptor version,
/// work factor and salt length bounds) and, when the caller supplies the descriptor of the
/// owning note/session, must match it exactly; any unknown, invalid or mismatched descriptor
/// fails before AES-GCM decrypt. The stored work factor is metadata about the key that was
/// derived earlier, so a tampered value is rejected as inconsistent rather than silently
/// ignored. Legacy v1 containers carry no descriptor and stay readable with the owning note's
/// historical parameters.
///
/// The original file name is stored separately in the DB attachment envelope
/// (object type "attachment-meta").
///
/// This stage does not change the effective PBKDF2 cost and does not adopt Argon2id.
/// </summary>
public static class ProtectedAttachmentFile
{
    private static readonly byte[] MagicBytes = { 0x51, 0x4E, 0x41, 0x54 }; // "QNAT"
    private const byte EnvelopeVersionV1 = 0x01;
    private const byte EnvelopeVersionV2 = 0x02;
    private const int MagicSize = 4;
    private const int VersionSize = 1;
    private const int SaltLenFieldSize = 4;
    private const int PlaintextSizeFieldSize = 8;
    private const int DescriptorHeaderSize = 1 + 2 + 4; // algId + descriptorVersion + iterations

    private const int HeaderOverheadV1 = MagicSize + VersionSize + SaltLenFieldSize
        + NoteCryptoService.SaltByteSize + NoteCryptoService.NonceByteSize
        + NoteCryptoService.TagByteSize + PlaintextSizeFieldSize;

    private const int HeaderOverheadV2 = HeaderOverheadV1 + DescriptorHeaderSize;

    /// <summary>Smallest valid container (empty plaintext, current version).</summary>
    public const int MinContainerBytes = HeaderOverheadV2;

    /// <summary>
    /// Encrypts plaintext bytes into a current-version (v2) QNAT container in memory.
    /// The descriptor describes the KDF that produced <paramref name="key"/>.
    /// </summary>
    public static byte[] CreateContainer(byte[] key, byte[] plaintext, int formatVersion, Guid syncId, KdfDescriptor descriptor)
    {
        if (plaintext == null)
        {
            throw new ArgumentNullException(nameof(plaintext));
        }

        descriptor.Validate(KdfDescriptorLimits.LocalEnvelope);
        if (descriptor.AlgorithmId != KdfAlgorithmIds.Pbkdf2HmacSha256)
        {
            throw new NoteProtectionSecurityException("Неподдерживаемый алгоритм KDF вложения.");
        }

        byte[] nonce = RandomNumberGenerator.GetBytes(NoteCryptoService.NonceByteSize);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[NoteCryptoService.TagByteSize];
        byte[] aad = NoteCryptoService.BuildAssociatedData(formatVersion, syncId, NoteProtectedObjectType.AttachmentContent);

        using (var aesGcm = new AesGcm(key, NoteCryptoService.TagByteSize))
        {
            aesGcm.Encrypt(nonce, plaintext, ciphertext, tag, aad);
        }

        using var ms = new MemoryStream(HeaderOverheadV2 + plaintext.Length);
        using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            bw.Write(MagicBytes);
            bw.Write(EnvelopeVersionV2);
            bw.Write(KdfAlgorithmIds.Pbkdf2HmacSha256Id);
            bw.Write((ushort)descriptor.DescriptorVersion);
            bw.Write(descriptor.Iterations);
            bw.Write(NoteCryptoService.SaltByteSize);
            bw.Write(new byte[NoteCryptoService.SaltByteSize]); // salt placeholder (claim-check, not key input)
            bw.Write(nonce);
            bw.Write(tag);
            bw.Write((long)plaintext.Length);
            bw.Write(ciphertext);
        }
        CryptographicOperations.ZeroMemory(ciphertext);
        return ms.ToArray();
    }

    /// <summary>
    /// Reads only the KDF descriptor of a QNAT container without decrypting anything.
    /// Returns false for malformed containers; legacy v1 containers produce
    /// <see cref="KdfEnvelopeState.LegacyMissingDescriptor"/> with the supplied fallback.
    /// </summary>
    public static bool TryReadDescriptor(byte[] containerBytes, int legacyIterations, out KdfEnvelopeReading reading)
    {
        reading = default;
        if (containerBytes == null || containerBytes.Length < HeaderOverheadV1)
        {
            return false;
        }

        if (!containerBytes.AsSpan(0, MagicSize).SequenceEqual(MagicBytes))
        {
            return false;
        }

        byte version = containerBytes[MagicSize];
        if (version == EnvelopeVersionV1)
        {
            var legacy = new KdfDescriptor(
                KdfAlgorithmIds.Pbkdf2HmacSha256,
                KdfDescriptorConstants.CurrentDescriptorVersion,
                legacyIterations,
                NoteCryptoService.SaltByteSize);
            try
            {
                KdfDescriptor.ValidateIterations(legacyIterations, KdfDescriptorLimits.LocalEnvelope);
            }
            catch (KdfDescriptorValidationException)
            {
                return false;
            }

            reading = new KdfEnvelopeReading(legacy, KdfEnvelopeState.LegacyMissingDescriptor);
            return true;
        }

        if (version != EnvelopeVersionV2 || containerBytes.Length < HeaderOverheadV2)
        {
            return false;
        }

        byte algorithmId = containerBytes[MagicSize + VersionSize];
        if (algorithmId != KdfAlgorithmIds.Pbkdf2HmacSha256Id)
        {
            return false;
        }

        int descriptorVersion = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(
            containerBytes.AsSpan(MagicSize + VersionSize + 1));
        int iterations = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(
            containerBytes.AsSpan(MagicSize + VersionSize + 3));

        var descriptor = new KdfDescriptor(
            KdfAlgorithmIds.Pbkdf2HmacSha256,
            descriptorVersion,
            iterations,
            NoteCryptoService.SaltByteSize);
        try
        {
            descriptor.Validate(KdfDescriptorLimits.LocalEnvelope);
        }
        catch (KdfDescriptorValidationException)
        {
            return false;
        }

        reading = new KdfEnvelopeReading(descriptor, KdfEnvelopeState.Current);
        return true;
    }

    /// <summary>
    /// Decrypts a QNAT container byte array in memory. Accepts v1 (legacy) and v2 (current).
    /// Throws <see cref="NoteProtectionSecurityException"/> on tamper/wrong key/unknown KDF.
    ///
    /// When <paramref name="expectedDescriptor"/> is supplied (the descriptor of the owning
    /// note/session), a v2 container's own descriptor must match it byte-for-byte before any
    /// AES-GCM decrypt. Legacy v1 containers have no descriptor and are unaffected.
    /// </summary>
    public static byte[] DecryptContainer(
        byte[] key,
        byte[] containerBytes,
        int formatVersion,
        Guid syncId,
        KdfDescriptor? expectedDescriptor = null)
    {
        if (containerBytes == null || containerBytes.Length < HeaderOverheadV1)
        {
            throw new NoteProtectionSecurityException("Файл вложения повреждён или имеет неверный размер.");
        }

        using var ms = new MemoryStream(containerBytes);
        using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        byte[] magic = br.ReadBytes(MagicSize);
        if (magic.Length != MagicSize || !magic.AsSpan().SequenceEqual(MagicBytes))
        {
            throw new NoteProtectionSecurityException("Файл вложения не является зашифрованным файлом QuickNotes.");
        }

        byte version = br.ReadByte();
        if (version == EnvelopeVersionV2)
        {
            byte algorithmId = br.ReadByte();
            if (algorithmId != KdfAlgorithmIds.Pbkdf2HmacSha256Id)
            {
                throw new NoteProtectionSecurityException("Неподдерживаемый алгоритм KDF зашифрованного вложения.");
            }

            int descriptorVersion = br.ReadUInt16();
            int kdfIterations = br.ReadInt32();
            var descriptor = new KdfDescriptor(
                KdfAlgorithmIds.Pbkdf2HmacSha256,
                descriptorVersion,
                kdfIterations,
                NoteCryptoService.SaltByteSize);
            try
            {
                descriptor.Validate(KdfDescriptorLimits.LocalEnvelope);
            }
            catch (KdfDescriptorValidationException ex)
            {
                throw new NoteProtectionSecurityException("Недопустимые параметры KDF зашифрованного вложения.", ex);
            }

            // Fail before AES-GCM when the container disagrees with the owning note/session.
            if (expectedDescriptor.HasValue && !descriptor.Matches(expectedDescriptor.Value))
            {
                throw new NoteProtectionSecurityException(
                    "Параметры KDF вложения не соответствуют заметке-владельцу.");
            }
        }
        else if (version != EnvelopeVersionV1)
        {
            throw new NoteProtectionSecurityException($"Неподдерживаемая версия зашифрованного вложения ({version}).");
        }

        int saltLen = br.ReadInt32();
        if (saltLen != NoteCryptoService.SaltByteSize)
        {
            throw new NoteProtectionSecurityException("Неверный размер соли зашифрованного вложения.");
        }
        byte[] salt = br.ReadBytes(saltLen);
        byte[] nonce = br.ReadBytes(NoteCryptoService.NonceByteSize);
        byte[] tag = br.ReadBytes(NoteCryptoService.TagByteSize);
        long plaintextSize = br.ReadInt64();
        if (plaintextSize < 0 || plaintextSize > ms.Length - ms.Position)
        {
            throw new NoteProtectionSecurityException("Неверный размер содержимого зашифрованного вложения.");
        }

        byte[] ciphertext = br.ReadBytes((int)plaintextSize);
        byte[] plaintext = new byte[plaintextSize];
        byte[] aad = NoteCryptoService.BuildAssociatedData(formatVersion, syncId, NoteProtectedObjectType.AttachmentContent);

        try
        {
            using (var aesGcm = new AesGcm(key, NoteCryptoService.TagByteSize))
            {
                aesGcm.Decrypt(nonce, ciphertext, tag, plaintext, aad);
            }
            return plaintext;
        }
        catch (CryptographicException ex)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new NoteProtectionSecurityException(
                "Не удалось расшифровать вложение: неверный пароль или нарушение целостности.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    /// <summary>
    /// Encrypts the source file content into an encrypted envelope file at destPath using the given adapter.
    /// The source file is left untouched (caller decides when to delete it).
    /// </summary>
    public static void EncryptFileTo(IProtectionFileAdapter fileAdapter, byte[] key, string sourcePath, string destPath, int formatVersion, Guid syncId, KdfDescriptor descriptor)
    {
        if (fileAdapter == null) throw new ArgumentNullException(nameof(fileAdapter));
        byte[] plaintext = fileAdapter.ReadAllBytes(sourcePath);
        try
        {
            byte[] container = CreateContainer(key, plaintext, formatVersion, syncId, descriptor);
            fileAdapter.WriteAllBytes(destPath, container);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// Encrypts the source file content into an encrypted envelope file at destPath.
    /// The source file is left untouched (caller decides when to delete it).
    /// </summary>
    public static void EncryptFileTo(byte[] key, string sourcePath, string destPath, int formatVersion, Guid syncId, KdfDescriptor descriptor)
        => EncryptFileTo(new PhysicalProtectionFileAdapter(), key, sourcePath, destPath, formatVersion, syncId, descriptor);

    /// <summary>
    /// Decrypts an encrypted envelope file into a byte array using the given adapter. Throws
    /// <see cref="NoteProtectionSecurityException"/> on tamper/wrong key.
    /// </summary>
    public static byte[] DecryptFile(
        IProtectionFileAdapter fileAdapter,
        byte[] key,
        string sourcePath,
        int formatVersion,
        Guid syncId,
        KdfDescriptor? expectedDescriptor = null)
    {
        if (fileAdapter == null) throw new ArgumentNullException(nameof(fileAdapter));
        byte[] container = fileAdapter.ReadAllBytes(sourcePath);
        return DecryptContainer(key, container, formatVersion, syncId, expectedDescriptor);
    }

    /// <summary>
    /// Decrypts an encrypted envelope file into a byte array. Throws
    /// <see cref="NoteProtectionSecurityException"/> on tamper/wrong key.
    /// </summary>
    public static byte[] DecryptFile(
        byte[] key,
        string sourcePath,
        int formatVersion,
        Guid syncId,
        KdfDescriptor? expectedDescriptor = null)
        => DecryptFile(new PhysicalProtectionFileAdapter(), key, sourcePath, formatVersion, syncId, expectedDescriptor);

    /// <summary>
    /// Writes decrypted bytes to a temporary file with a safe random name.
    /// </summary>
    public static string WriteTempFile(byte[] content, string extension)
    {
        string safeExt = string.IsNullOrWhiteSpace(extension) ? ".bin" : extension;
        if (safeExt.Length > 10 || safeExt.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            safeExt = ".bin";
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "QuickNotes");
        Directory.CreateDirectory(tempDir);
        string tempPath = Path.Combine(tempDir, $"qn_{Guid.NewGuid():N}{safeExt}");
        File.WriteAllBytes(tempPath, content);
        return tempPath;
    }

    public static void TryDeleteTempFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup; never throw from cleanup
        }
    }
}
