using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace QuickNotes.App.Services.EncryptedArchive;

internal static class EncryptedArchiveCrypto
{
    internal static long Pbkdf2Invocations;

    internal static void ResetPbkdf2Invocations() => Interlocked.Exchange(ref Pbkdf2Invocations, 0);

    internal static byte[] DerivePasswordKek(string password, byte[] salt, int iterations)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new EncryptedArchiveSecurityException();
        }

        if (salt == null || salt.Length != EncryptedArchiveConstants.SaltByteSize)
        {
            throw new EncryptedArchiveFormatException("Файл не является зашифрованным архивом QuickNotes.");
        }

        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
        Interlocked.Increment(ref Pbkdf2Invocations);
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(
                passwordBytes,
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                EncryptedArchiveConstants.KeyByteSize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    internal static byte[] DeriveRecoveryKek(byte[] recoveryKeyMaterial, byte[] salt)
    {
        if (recoveryKeyMaterial == null || recoveryKeyMaterial.Length != EncryptedArchiveConstants.KeyByteSize)
        {
            throw new EncryptedArchiveSecurityException();
        }

        if (salt == null || salt.Length != EncryptedArchiveConstants.SaltByteSize)
        {
            throw new EncryptedArchiveFormatException("Файл не является зашифрованным архивом QuickNotes.");
        }

        byte[] info = Encoding.UTF8.GetBytes(EncryptedArchiveConstants.HkdfInfoRecoveryV1);
        return HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            recoveryKeyMaterial,
            EncryptedArchiveConstants.KeyByteSize,
            salt,
            info);
    }

    internal static byte[] WrapDek(byte[] kek, byte[] dek, byte[] aad, out byte[] nonce, out byte[] tag)
    {
        nonce = RandomNumberGenerator.GetBytes(EncryptedArchiveConstants.NonceByteSize);
        tag = new byte[EncryptedArchiveConstants.TagByteSize];
        byte[] ciphertext = new byte[dek.Length];
        using (var aes = new AesGcm(kek, EncryptedArchiveConstants.TagByteSize))
        {
            aes.Encrypt(nonce, dek, ciphertext, tag, aad);
        }

        return ciphertext;
    }

    internal static byte[] UnwrapDek(byte[] kek, byte[] ciphertext, byte[] nonce, byte[] tag, byte[] aad)
    {
        if (ciphertext.Length != EncryptedArchiveConstants.KeyByteSize
            || nonce.Length != EncryptedArchiveConstants.NonceByteSize
            || tag.Length != EncryptedArchiveConstants.TagByteSize)
        {
            throw new EncryptedArchiveSecurityException();
        }

        byte[] dek = new byte[EncryptedArchiveConstants.KeyByteSize];
        try
        {
            using var aes = new AesGcm(kek, EncryptedArchiveConstants.TagByteSize);
            aes.Decrypt(nonce, ciphertext, tag, dek, aad);
            return dek;
        }
        catch (CryptographicException ex)
        {
            CryptographicOperations.ZeroMemory(dek);
            throw new EncryptedArchiveSecurityException(ex);
        }
    }

    internal static void EncryptPayload(byte[] dek, byte[] plaintext, byte[] aad, out byte[] nonce, out byte[] ciphertext, out byte[] tag)
    {
        nonce = RandomNumberGenerator.GetBytes(EncryptedArchiveConstants.NonceByteSize);
        ciphertext = new byte[plaintext.Length];
        tag = new byte[EncryptedArchiveConstants.TagByteSize];
        using var aes = new AesGcm(dek, EncryptedArchiveConstants.TagByteSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
    }

    internal static byte[] DecryptPayload(byte[] dek, byte[] ciphertext, byte[] nonce, byte[] tag, byte[] aad)
    {
        byte[] plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(dek, EncryptedArchiveConstants.TagByteSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
            return plaintext;
        }
        catch (CryptographicException ex)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new EncryptedArchiveSecurityException(ex);
        }
    }

    internal static byte[] BuildWrapAad(string canonicalHeader, string slotId)
        => Encoding.UTF8.GetBytes(canonicalHeader + "|wrap|" + slotId);

    internal static byte[] BuildPayloadAad(string canonicalHeader, string contentType, long plaintextLength)
        => Encoding.UTF8.GetBytes(canonicalHeader + "|payload|" + contentType + "|" + plaintextLength.ToString(System.Globalization.CultureInfo.InvariantCulture));

    internal static byte[] EncodeWrapSlot(byte[] salt, byte[] nonce, byte[] ciphertext, byte[] tag)
    {
        if (salt.Length != EncryptedArchiveConstants.SaltByteSize
            || nonce.Length != EncryptedArchiveConstants.NonceByteSize
            || ciphertext.Length != EncryptedArchiveConstants.KeyByteSize
            || tag.Length != EncryptedArchiveConstants.TagByteSize)
        {
            throw new EncryptedArchiveFormatException("Файл не является зашифрованным архивом QuickNotes.");
        }

        var slot = new byte[EncryptedArchiveConstants.WrapSlotByteSize];
        Buffer.BlockCopy(salt, 0, slot, 0, salt.Length);
        Buffer.BlockCopy(nonce, 0, slot, salt.Length, nonce.Length);
        Buffer.BlockCopy(ciphertext, 0, slot, salt.Length + nonce.Length, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, slot, salt.Length + nonce.Length + ciphertext.Length, tag.Length);
        return slot;
    }

    internal static void DecodeWrapSlot(byte[] slot, out byte[] salt, out byte[] nonce, out byte[] ciphertext, out byte[] tag)
    {
        if (slot == null || slot.Length != EncryptedArchiveConstants.WrapSlotByteSize)
        {
            throw new EncryptedArchiveFormatException("Файл не является зашифрованным архивом QuickNotes.");
        }

        salt = slot.AsSpan(0, EncryptedArchiveConstants.SaltByteSize).ToArray();
        nonce = slot.AsSpan(EncryptedArchiveConstants.SaltByteSize, EncryptedArchiveConstants.NonceByteSize).ToArray();
        ciphertext = slot.AsSpan(EncryptedArchiveConstants.SaltByteSize + EncryptedArchiveConstants.NonceByteSize, EncryptedArchiveConstants.KeyByteSize).ToArray();
        tag = slot.AsSpan(EncryptedArchiveConstants.WrapSlotByteSize - EncryptedArchiveConstants.TagByteSize, EncryptedArchiveConstants.TagByteSize).ToArray();
    }
}
