using System;
using System.Globalization;
using System.Text;

namespace QuickNotes.App.Services.EncryptedArchive;

internal readonly struct EncryptedArchiveCanonicalHeader
{
    public int FormatVersion { get; init; }
    public Guid ArchiveId { get; init; }
    public long CreatedAtUnixUtc { get; init; }
    public string KdfAlg { get; init; }
    public int KdfVersion { get; init; }
    public int KdfIterations { get; init; }
    public int WrapCount { get; init; }
    public string PayloadContentType { get; init; }

    public string ToCanonicalString()
    {
        return string.Join('|',
            "QNAR",
            FormatDecimal(FormatVersion),
            ArchiveId.ToString("D"),
            FormatDecimal(CreatedAtUnixUtc),
            KdfAlg,
            FormatDecimal(KdfVersion),
            FormatDecimal(KdfIterations),
            FormatDecimal(WrapCount),
            PayloadContentType);
    }

    public byte[] ToUtf8() => Encoding.UTF8.GetBytes(ToCanonicalString());

    public static EncryptedArchiveCanonicalHeader ParseV1OrThrow(
        string canonical,
        EncryptedArchiveKdfLimits limits,
        bool validateKdfAlgorithmAndVersion)
    {
        string[] parts = canonical.Split('|');
        if (parts.Length != 9 || parts[0] != "QNAR")
        {
            throw new EncryptedArchiveFormatException("Файл не является зашифрованным архивом QuickNotes.");
        }

        int formatVersion = ParseNonNegativeInt(parts[1], "formatVersion");
        if (formatVersion != EncryptedArchiveConstants.FormatVersionV1)
        {
            throw new EncryptedArchiveFormatException("Неподдерживаемая версия архива.");
        }

        if (!Guid.TryParseExact(parts[2], "D", out Guid archiveId) || archiveId == Guid.Empty)
        {
            throw new EncryptedArchiveFormatException("Файл не является зашифрованным архивом QuickNotes.");
        }

        long createdAt = ParseNonNegativeLong(parts[3], "createdAtUnixUtc");
        string kdfAlg = parts[4];
        int kdfVersion = ParseNonNegativeInt(parts[5], "kdfVersion");
        int kdfIterations = ParseNonNegativeInt(parts[6], "kdfIterations");
        int wrapCount = ParseNonNegativeInt(parts[7], "wrapCount");
        string payloadContentType = parts[8];

        if (wrapCount != EncryptedArchiveConstants.WrapCountV1)
        {
            throw new EncryptedArchiveFormatException("Файл не является зашифрованным архивом QuickNotes.");
        }

        if (payloadContentType != EncryptedArchiveConstants.PayloadContentTypeV1)
        {
            throw new EncryptedArchiveFormatException("Неподдерживаемая версия архива.");
        }

        if (validateKdfAlgorithmAndVersion)
        {
            if (kdfAlg != EncryptedArchiveConstants.KdfAlgorithmV1)
            {
                throw new EncryptedArchiveFormatException("Неподдерживаемая версия архива.");
            }

            if (kdfVersion != EncryptedArchiveConstants.KdfVersionV1)
            {
                throw new EncryptedArchiveFormatException("Неподдерживаемая версия архива.");
            }
        }

        limits.ValidateIterations(kdfIterations);

        return new EncryptedArchiveCanonicalHeader
        {
            FormatVersion = formatVersion,
            ArchiveId = archiveId,
            CreatedAtUnixUtc = createdAt,
            KdfAlg = kdfAlg,
            KdfVersion = kdfVersion,
            KdfIterations = kdfIterations,
            WrapCount = wrapCount,
            PayloadContentType = payloadContentType
        };
    }

    private static string FormatDecimal(long value)
        => value.ToString(CultureInfo.InvariantCulture);

    private static int ParseNonNegativeInt(string raw, string field)
    {
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            || value < 0
            || raw != value.ToString(CultureInfo.InvariantCulture))
        {
            throw new EncryptedArchiveFormatException("Файл не является зашифрованным архивом QuickNotes.");
        }

        _ = field;
        return value;
    }

    private static long ParseNonNegativeLong(string raw, string field)
    {
        if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out long value)
            || value < 0
            || raw != value.ToString(CultureInfo.InvariantCulture))
        {
            throw new EncryptedArchiveFormatException("Файл не является зашифрованным архивом QuickNotes.");
        }

        _ = field;
        return value;
    }
}
