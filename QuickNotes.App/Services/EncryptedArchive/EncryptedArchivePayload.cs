using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace QuickNotes.App.Services.EncryptedArchive;

internal sealed class QnapEntry
{
    public string Path { get; init; } = string.Empty;
    public byte[] Content { get; init; } = Array.Empty<byte>();
    public byte[] Sha256 { get; init; } = Array.Empty<byte>();
}

internal static class EncryptedArchivePayload
{
    internal static byte[] Write(IReadOnlyList<QnapEntry> entries)
    {
        if (entries == null || entries.Count == 0 || entries.Count > EncryptedArchiveConstants.MaxEntryCount)
        {
            throw new EncryptedArchiveValidationException("Некорректная таблица записей архива.");
        }

        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            bw.Write(EncryptedArchiveConstants.MagicQnap);
            bw.Write((uint)entries.Count);
            long running = EncryptedArchiveConstants.MagicSize + 4;
            foreach (QnapEntry entry in entries)
            {
                byte[] pathUtf8 = Encoding.UTF8.GetBytes(entry.Path);
                if (pathUtf8.Length == 0 || pathUtf8.Length > EncryptedArchiveConstants.MaxPathUtf8Bytes)
                {
                    throw new EncryptedArchiveValidationException("Некорректный путь внутри архива.");
                }

                EncryptedArchiveInMemoryLimits.EnsureAllocatedSizeFits(entry.Content.LongLength, formatException: false);
                running += 2L + pathUtf8.Length + 8 + EncryptedArchiveConstants.Sha256ByteSize + entry.Content.LongLength;
                EncryptedArchiveInMemoryLimits.EnsureAllocatedSizeFits(running, formatException: false);

                bw.Write((ushort)pathUtf8.Length);
                bw.Write(pathUtf8);
                bw.Write((ulong)entry.Content.Length);
                bw.Write(entry.Sha256);
                bw.Write(entry.Content);
            }
        }

        return ms.ToArray();
    }

    internal static List<QnapEntry> ReadBounded(byte[] plaintext)
    {
        if (plaintext == null || plaintext.Length < 8)
        {
            throw new EncryptedArchiveValidationException("Нагрузка архива повреждена.");
        }

        EncryptedArchiveInMemoryLimits.EnsureAllocatedSizeFits(plaintext.LongLength, formatException: false);

        using var ms = new MemoryStream(plaintext, writable: false);
        using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
        byte[] magic = br.ReadBytes(4);
        if (magic.Length != 4 || !magic.AsSpan().SequenceEqual(EncryptedArchiveConstants.MagicQnap))
        {
            throw new EncryptedArchiveValidationException("Нагрузка архива повреждена.");
        }

        if (Remaining(ms) < 4)
        {
            throw new EncryptedArchiveValidationException("Нагрузка архива повреждена.");
        }

        uint entryCount = br.ReadUInt32();
        if (entryCount == 0 || entryCount > EncryptedArchiveConstants.MaxEntryCount)
        {
            throw new EncryptedArchiveValidationException("Размер записи архива превышает лимит.");
        }

        var entries = new List<QnapEntry>((int)Math.Min(entryCount, 1024));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (uint i = 0; i < entryCount; i++)
        {
            if (Remaining(ms) < 2)
            {
                throw new EncryptedArchiveValidationException("Нагрузка архива повреждена.");
            }

            ushort pathLength = br.ReadUInt16();
            if (pathLength == 0 || pathLength > EncryptedArchiveConstants.MaxPathUtf8Bytes)
            {
                throw new EncryptedArchiveValidationException("Некорректный путь внутри архива.");
            }

            if (Remaining(ms) < pathLength)
            {
                throw new EncryptedArchiveValidationException("Нагрузка архива повреждена.");
            }

            byte[] pathUtf8 = br.ReadBytes(pathLength);
            string rawPath = Encoding.UTF8.GetString(pathUtf8);
            string normalized = EncryptedArchivePathRules.NormalizeRelative(rawPath);
            EncryptedArchivePathRules.ValidateAllowedPayloadPath(normalized);
            if (!seen.Add(normalized))
            {
                throw new EncryptedArchiveValidationException("Нагрузка архива содержит повторяющиеся пути.");
            }

            if (Remaining(ms) < 8 + EncryptedArchiveConstants.Sha256ByteSize)
            {
                throw new EncryptedArchiveValidationException("Нагрузка архива повреждена.");
            }

            ulong fileSize = br.ReadUInt64();
            int contentLength = EncryptedArchiveInMemoryLimits.CheckedDeclaredBytes(
                fileSize,
                EncryptedArchiveConstants.MaxSingleFileBytes,
                formatException: false);

            long remaining = Remaining(ms);
            if (remaining < (long)contentLength + EncryptedArchiveConstants.Sha256ByteSize)
            {
                throw new EncryptedArchiveValidationException("Нагрузка архива повреждена.");
            }

            byte[] sha = br.ReadBytes(EncryptedArchiveConstants.Sha256ByteSize);
            byte[] content = br.ReadBytes(contentLength);
            byte[] actualSha = SHA256.HashData(content);
            if (!CryptographicOperations.FixedTimeEquals(sha, actualSha))
            {
                throw new EncryptedArchiveValidationException("Контрольная сумма записи архива не совпала.");
            }

            entries.Add(new QnapEntry
            {
                Path = normalized,
                Content = content,
                Sha256 = sha
            });
        }

        if (Remaining(ms) != 0)
        {
            throw new EncryptedArchiveValidationException("Нагрузка архива повреждена.");
        }

        return entries;
    }

    internal static byte[] WriteHostile(uint declaredEntryCount, IReadOnlyList<(string Path, byte[] Content, ulong DeclaredSize, byte[] Sha)> entries)
    {
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            bw.Write(EncryptedArchiveConstants.MagicQnap);
            bw.Write(declaredEntryCount);
            foreach ((string path, byte[] content, ulong declaredSize, byte[] sha) in entries)
            {
                byte[] pathUtf8 = Encoding.UTF8.GetBytes(path);
                bw.Write((ushort)pathUtf8.Length);
                bw.Write(pathUtf8);
                bw.Write(declaredSize);
                bw.Write(sha);
                bw.Write(content);
            }
        }

        return ms.ToArray();
    }

    internal static QnapEntry Create(string path, byte[] content)
    {
        string normalized = EncryptedArchivePathRules.NormalizeRelative(path);
        EncryptedArchivePathRules.ValidateAllowedPayloadPath(normalized);
        return new QnapEntry
        {
            Path = normalized,
            Content = content,
            Sha256 = SHA256.HashData(content)
        };
    }

    private static long Remaining(Stream stream) => stream.Length - stream.Position;
}
