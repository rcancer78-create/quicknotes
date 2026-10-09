using System;
using System.Buffers.Binary;
using System.Text;

namespace QuickNotes.App.Services.EncryptedArchive;

internal sealed class EncryptedArchiveFramedFile
{
    public EncryptedArchiveCanonicalHeader Header { get; init; }
    public string CanonicalHeaderText { get; init; } = string.Empty;
    public byte[] PasswordWrap { get; init; } = Array.Empty<byte>();
    public byte[] RecoveryWrap { get; init; } = Array.Empty<byte>();
    public byte[] PayloadNonce { get; init; } = Array.Empty<byte>();
    public byte[] PayloadCiphertext { get; init; } = Array.Empty<byte>();
    public byte[] PayloadTag { get; init; } = Array.Empty<byte>();
}

internal static class EncryptedArchiveFraming
{
    internal static byte[] Write(EncryptedArchiveFramedFile file)
    {
        byte[] headerUtf8 = Encoding.UTF8.GetBytes(file.CanonicalHeaderText);
        if (headerUtf8.Length == 0 || headerUtf8.Length > EncryptedArchiveConstants.MaxHeaderBytes)
        {
            throw new EncryptedArchiveFormatException("Файл не является зашифрованным архивом QuickNotes.");
        }

        EncryptedArchiveInMemoryLimits.EnsureAllocatedSizeFits(file.PayloadCiphertext.LongLength, formatException: true);

        long total = EncryptedArchiveConstants.MagicSize
            + EncryptedArchiveConstants.HeaderLengthSize
            + headerUtf8.Length
            + EncryptedArchiveConstants.WrapSlotByteSize
            + EncryptedArchiveConstants.WrapSlotByteSize
            + EncryptedArchiveConstants.PayloadLengthSize
            + EncryptedArchiveConstants.NonceByteSize
            + file.PayloadCiphertext.LongLength
            + EncryptedArchiveConstants.TagByteSize;
        EncryptedArchiveInMemoryLimits.EnsureAllocatedSizeFits(total, formatException: true);

        byte[] buffer = new byte[(int)total];
        int offset = 0;
        EncryptedArchiveConstants.MagicQnar.CopyTo(buffer.AsSpan(offset));
        offset += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset), (ushort)headerUtf8.Length);
        offset += 2;
        headerUtf8.CopyTo(buffer.AsSpan(offset));
        offset += headerUtf8.Length;
        file.PasswordWrap.CopyTo(buffer.AsSpan(offset));
        offset += EncryptedArchiveConstants.WrapSlotByteSize;
        file.RecoveryWrap.CopyTo(buffer.AsSpan(offset));
        offset += EncryptedArchiveConstants.WrapSlotByteSize;
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset), (ulong)file.PayloadCiphertext.Length);
        offset += 8;
        file.PayloadNonce.CopyTo(buffer.AsSpan(offset));
        offset += EncryptedArchiveConstants.NonceByteSize;
        file.PayloadCiphertext.CopyTo(buffer.AsSpan(offset));
        offset += file.PayloadCiphertext.Length;
        file.PayloadTag.CopyTo(buffer.AsSpan(offset));
        return buffer;
    }

    internal static EncryptedArchiveFramedFile ReadBounded(byte[] fileBytes, EncryptedArchiveKdfLimits limits)
    {
        if (fileBytes == null)
        {
            throw new EncryptedArchiveFormatException("Файл не является зашифрованным архивом QuickNotes.");
        }

        EncryptedArchiveInMemoryLimits.EnsureAllocatedSizeFits(fileBytes.LongLength, formatException: true);

        if (fileBytes.Length < EncryptedArchiveConstants.MagicSize + EncryptedArchiveConstants.HeaderLengthSize)
        {
            throw new EncryptedArchiveFormatException("Файл не является зашифрованным архивом QuickNotes.");
        }

        if (!fileBytes.AsSpan(0, 4).SequenceEqual(EncryptedArchiveConstants.MagicQnar))
        {
            throw new EncryptedArchiveFormatException("Файл не является зашифрованным архивом QuickNotes.");
        }

        ushort headerLength = BinaryPrimitives.ReadUInt16LittleEndian(fileBytes.AsSpan(4));
        if (headerLength == 0 || headerLength > EncryptedArchiveConstants.MaxHeaderBytes)
        {
            throw new EncryptedArchiveFormatException("Файл не является зашифрованным архивом QuickNotes.");
        }

        int headerStart = 6;
        if (fileBytes.Length < headerStart + headerLength)
        {
            throw new EncryptedArchiveFormatException("Файл не является зашифрованным архивом QuickNotes.");
        }

        string canonical = Encoding.UTF8.GetString(fileBytes, headerStart, headerLength);

        int wrapStart = headerStart + headerLength;
        int wrapsBytes = EncryptedArchiveConstants.WrapSlotByteSize * 2;
        if (fileBytes.Length < wrapStart + wrapsBytes + EncryptedArchiveConstants.PayloadLengthSize)
        {
            throw new EncryptedArchiveFormatException("Файл не является зашифрованным архивом QuickNotes.");
        }

        EncryptedArchiveCanonicalHeader header = EncryptedArchiveCanonicalHeader.ParseV1OrThrow(
            canonical,
            limits,
            validateKdfAlgorithmAndVersion: true);

        byte[] passwordWrap = fileBytes.AsSpan(wrapStart, EncryptedArchiveConstants.WrapSlotByteSize).ToArray();
        byte[] recoveryWrap = fileBytes.AsSpan(wrapStart + EncryptedArchiveConstants.WrapSlotByteSize, EncryptedArchiveConstants.WrapSlotByteSize).ToArray();

        int payloadLengthOffset = wrapStart + wrapsBytes;
        ulong payloadLength = BinaryPrimitives.ReadUInt64LittleEndian(fileBytes.AsSpan(payloadLengthOffset));
        int payloadBytes = EncryptedArchiveInMemoryLimits.CheckedDeclaredBytes(
            payloadLength,
            EncryptedArchiveConstants.MaxPayloadBytes,
            formatException: true);

        int nonceOffset = payloadLengthOffset + EncryptedArchiveConstants.PayloadLengthSize;
        long neededAfterNonce = (long)payloadBytes + EncryptedArchiveConstants.NonceByteSize + EncryptedArchiveConstants.TagByteSize;
        if (fileBytes.Length < nonceOffset + neededAfterNonce)
        {
            throw new EncryptedArchiveFormatException("Файл не является зашифрованным архивом QuickNotes.");
        }

        int ciphertextOffset = nonceOffset + EncryptedArchiveConstants.NonceByteSize;
        int tagOffset = ciphertextOffset + payloadBytes;
        int expectedEnd = tagOffset + EncryptedArchiveConstants.TagByteSize;
        if (fileBytes.Length != expectedEnd)
        {
            throw new EncryptedArchiveFormatException("Файл не является зашифрованным архивом QuickNotes.");
        }

        return new EncryptedArchiveFramedFile
        {
            Header = header,
            CanonicalHeaderText = canonical,
            PasswordWrap = passwordWrap,
            RecoveryWrap = recoveryWrap,
            PayloadNonce = fileBytes.AsSpan(nonceOffset, EncryptedArchiveConstants.NonceByteSize).ToArray(),
            PayloadCiphertext = fileBytes.AsSpan(ciphertextOffset, payloadBytes).ToArray(),
            PayloadTag = fileBytes.AsSpan(tagOffset, EncryptedArchiveConstants.TagByteSize).ToArray()
        };
    }

    internal static byte[] ReplaceCanonicalHeader(byte[] fileBytes, string newCanonical)
    {
        var framed = ReadBoundedAllowingKdfForTests(fileBytes);
        framed = new EncryptedArchiveFramedFile
        {
            Header = framed.Header,
            CanonicalHeaderText = newCanonical,
            PasswordWrap = framed.PasswordWrap,
            RecoveryWrap = framed.RecoveryWrap,
            PayloadNonce = framed.PayloadNonce,
            PayloadCiphertext = framed.PayloadCiphertext,
            PayloadTag = framed.PayloadTag
        };
        return Write(framed);
    }

    /// <summary>
    /// Reads framing without KDF work. Used by tests to splice headers; still enforces size bounds.
    /// Does not validate kdfAlg/version so callers can inject unknown values.
    /// </summary>
    internal static EncryptedArchiveFramedFile ReadBoundedAllowingKdfForTests(byte[] fileBytes)
    {
        var permissive = new EncryptedArchiveKdfLimits(1, EncryptedArchiveConstants.MaxKdfIterations);
        if (fileBytes == null || fileBytes.Length < 6)
        {
            throw new EncryptedArchiveFormatException("Файл не является зашифрованным архивом QuickNotes.");
        }

        if (!fileBytes.AsSpan(0, 4).SequenceEqual(EncryptedArchiveConstants.MagicQnar))
        {
            throw new EncryptedArchiveFormatException("Файл не является зашифрованным архивом QuickNotes.");
        }

        ushort headerLength = BinaryPrimitives.ReadUInt16LittleEndian(fileBytes.AsSpan(4));
        int headerStart = 6;
        string canonical = Encoding.UTF8.GetString(fileBytes, headerStart, headerLength);
        string[] parts = canonical.Split('|');
        EncryptedArchiveCanonicalHeader header;
        try
        {
            header = EncryptedArchiveCanonicalHeader.ParseV1OrThrow(canonical, permissive, validateKdfAlgorithmAndVersion: false);
        }
        catch (EncryptedArchiveFormatException)
        {
            header = new EncryptedArchiveCanonicalHeader
            {
                FormatVersion = parts.Length > 1 && int.TryParse(parts[1], out int fv) ? fv : 0,
                ArchiveId = Guid.Empty,
                CreatedAtUnixUtc = 0,
                KdfAlg = parts.Length > 4 ? parts[4] : string.Empty,
                KdfVersion = 0,
                KdfIterations = 1,
                WrapCount = 2,
                PayloadContentType = EncryptedArchiveConstants.PayloadContentTypeV1
            };
        }

        int wrapStart = headerStart + headerLength;
        int payloadLengthOffset = wrapStart + EncryptedArchiveConstants.WrapSlotByteSize * 2;
        ulong payloadLength = BinaryPrimitives.ReadUInt64LittleEndian(fileBytes.AsSpan(payloadLengthOffset));
        int nonceOffset = payloadLengthOffset + 8;
        int ciphertextOffset = nonceOffset + EncryptedArchiveConstants.NonceByteSize;
        int tagOffset = ciphertextOffset + (int)payloadLength;
        return new EncryptedArchiveFramedFile
        {
            Header = header,
            CanonicalHeaderText = canonical,
            PasswordWrap = fileBytes.AsSpan(wrapStart, EncryptedArchiveConstants.WrapSlotByteSize).ToArray(),
            RecoveryWrap = fileBytes.AsSpan(wrapStart + EncryptedArchiveConstants.WrapSlotByteSize, EncryptedArchiveConstants.WrapSlotByteSize).ToArray(),
            PayloadNonce = fileBytes.AsSpan(nonceOffset, EncryptedArchiveConstants.NonceByteSize).ToArray(),
            PayloadCiphertext = fileBytes.AsSpan(ciphertextOffset, (int)payloadLength).ToArray(),
            PayloadTag = fileBytes.AsSpan(tagOffset, EncryptedArchiveConstants.TagByteSize).ToArray()
        };
    }
}
