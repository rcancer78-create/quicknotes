using System;
using System.Security.Cryptography;
using System.Text;

namespace QuickNotes.App.Services.EncryptedArchive;

/// <summary>
/// Crockford Base32 (no I/L/O/U) with SHA-256[0..4) checksum as 8 extra symbols, groups of 4.
/// </summary>
public static class RecoveryKeyEncoding
{
    internal const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int KeyByteSize = EncryptedArchiveConstants.KeyByteSize;
    private const int ChecksumByteSize = 4;

    public static string Format(ReadOnlySpan<byte> keyMaterial)
    {
        if (keyMaterial.Length != KeyByteSize)
        {
            throw new EncryptedArchiveValidationException("Некорректная длина recovery key.");
        }

        string keySymbols = Encode(keyMaterial);
        Span<byte> checksumSource = stackalloc byte[ChecksumByteSize + 1];
        Sha256Prefix(keyMaterial, checksumSource);
        checksumSource[ChecksumByteSize] = 0;
        string checksumSymbols = Encode(checksumSource);
        return Group(keySymbols + checksumSymbols);
    }

    public static byte[] Parse(string formatted)
    {
        if (string.IsNullOrWhiteSpace(formatted))
        {
            throw new EncryptedArchiveSecurityException();
        }

        string compact = Normalize(formatted);
        if (compact.Length != 60)
        {
            throw new EncryptedArchiveSecurityException();
        }

        string keySymbols = compact[..52];
        string checksumSymbols = compact[52..];
        byte[] key = DecodeExact(keySymbols, KeyByteSize);
        try
        {
            Span<byte> expectedSource = stackalloc byte[ChecksumByteSize + 1];
            Sha256Prefix(key, expectedSource);
            expectedSource[ChecksumByteSize] = 0;
            string expected = Encode(expectedSource);
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(expected),
                    Encoding.ASCII.GetBytes(checksumSymbols)))
            {
                throw new EncryptedArchiveSecurityException();
            }

            return key;
        }
        catch (EncryptedArchiveSecurityException)
        {
            CryptographicOperations.ZeroMemory(key);
            throw;
        }
        catch (Exception ex)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new EncryptedArchiveSecurityException(ex);
        }
    }

    internal static string Normalize(string formatted)
    {
        var sb = new StringBuilder(formatted.Length);
        foreach (char c in formatted)
        {
            if (c is '-' or ' ' or '\r' or '\n' or '\t')
            {
                continue;
            }

            char u = char.ToUpperInvariant(c);
            u = u switch
            {
                'I' or 'L' => '1',
                'O' => '0',
                _ => u
            };

            if (u == 'U' || Alphabet.IndexOf(u) < 0)
            {
                throw new EncryptedArchiveSecurityException();
            }

            sb.Append(u);
        }

        return sb.ToString();
    }

    internal static string Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0;
        int bits = 0;
        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                sb.Append(Alphabet[(buffer >> bits) & 31]);
            }
        }

        if (bits > 0)
        {
            sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return sb.ToString();
    }

    private static byte[] DecodeExact(string symbols, int expectedBytes)
    {
        int bitCount = symbols.Length * 5;
        byte[] raw = new byte[(bitCount + 7) / 8];
        int buffer = 0;
        int bits = 0;
        int index = 0;
        foreach (char c in symbols)
        {
            int v = Alphabet.IndexOf(c);
            if (v < 0)
            {
                throw new EncryptedArchiveSecurityException();
            }

            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                if (index < raw.Length)
                {
                    raw[index++] = (byte)((buffer >> bits) & 0xFF);
                }
            }
        }

        if (index < expectedBytes)
        {
            throw new EncryptedArchiveSecurityException();
        }

        var key = new byte[expectedBytes];
        Buffer.BlockCopy(raw, 0, key, 0, expectedBytes);
        CryptographicOperations.ZeroMemory(raw);
        return key;
    }

    private static void Sha256Prefix(ReadOnlySpan<byte> key, Span<byte> destination)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(key, hash);
        hash[..ChecksumByteSize].CopyTo(destination);
    }

    internal static string ChecksumGroups(string formatted)
    {
        string compact = Normalize(formatted);
        if (compact.Length != 60)
        {
            throw new EncryptedArchiveSecurityException();
        }

        return Group(compact[52..]);
    }

    private static string Group(string compact)
    {
        var sb = new StringBuilder(compact.Length + compact.Length / 4);
        for (int i = 0; i < compact.Length; i++)
        {
            if (i > 0 && i % 4 == 0)
            {
                sb.Append('-');
            }

            sb.Append(compact[i]);
        }

        return sb.ToString();
    }
}
