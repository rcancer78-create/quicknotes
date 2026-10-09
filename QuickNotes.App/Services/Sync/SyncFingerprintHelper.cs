using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using QuickNotes.App.Models;
using QuickNotes.App.Models.Sync;

namespace QuickNotes.App.Services.Sync;

/// <summary>
/// Provides deterministic canonical fingerprint calculation for synchronized entities.
/// Fingerprints are independent of UpdatedAt and take into account all synchronized fields and links.
/// </summary>
public static class SyncFingerprintHelper
{
    public static string ComputeSha256(byte[] data)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string ComputeSha256(string content)
    {
        if (content == null) throw new ArgumentNullException(nameof(content));
        return ComputeSha256(Encoding.UTF8.GetBytes(content));
    }

    public static string ComputeTombstoneFingerprint(string entityType)
    {
        return ComputeSha256($"TOMBSTONE|{entityType.Trim()}");
    }

    public static string ComputeTagFingerprint(
        string? name,
        Guid? parentTagSyncId,
        IEnumerable<string>? synonyms)
    {
        var sb = new StringBuilder();
        sb.Append("TAG\n");
        sb.Append(name ?? string.Empty).Append('\n');
        sb.Append(parentTagSyncId.HasValue ? parentTagSyncId.Value.ToString("D") : string.Empty).Append('\n');

        if (synonyms != null)
        {
            var sortedSynonyms = synonyms
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(s => s, StringComparer.Ordinal);
            foreach (var syn in sortedSynonyms)
            {
                sb.Append(syn).Append('\n');
            }
        }

        return ComputeSha256(sb.ToString());
    }

    public static string ComputeTemplateFingerprint(
        string? title,
        string? text,
        IEnumerable<Guid>? tagSyncIds)
    {
        var sb = new StringBuilder();
        sb.Append("TEMPLATE\n");
        sb.Append(title ?? string.Empty).Append('\n');
        sb.Append(text ?? string.Empty).Append('\n');

        if (tagSyncIds != null)
        {
            var sortedTagIds = tagSyncIds
                .Distinct()
                .OrderBy(g => g);
            foreach (var id in sortedTagIds)
            {
                sb.Append(id.ToString("D")).Append('\n');
            }
        }

        return ComputeSha256(sb.ToString());
    }

    public static string ComputeAttachmentFingerprint(
        Guid noteSyncId,
        string? originalFileName,
        string? contentType,
        long size,
        string? sha256,
        bool isProtected = false,
        int protectedFormatVersion = 0,
        int protectedKdfIterations = 0,
        string? protectedSaltBase64 = null,
        string? protectedNonceBase64 = null,
        string? protectedTagBase64 = null,
        string? protectedCiphertextBase64 = null,
        string? protectedKdfDescriptor = null)
    {
        var sb = new StringBuilder();
        sb.Append("ATTACHMENT\n");
        sb.Append(noteSyncId.ToString("D")).Append('\n');
        sb.Append(originalFileName ?? string.Empty).Append('\n');
        sb.Append(contentType ?? string.Empty).Append('\n');
        sb.Append(size.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append(sha256 ?? string.Empty).Append('\n');
        sb.Append(isProtected ? "1" : "0").Append('\n');
        if (isProtected)
        {
            sb.Append(protectedFormatVersion.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append(protectedKdfIterations.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append(protectedSaltBase64 ?? string.Empty).Append('\n');
            sb.Append(protectedNonceBase64 ?? string.Empty).Append('\n');
            sb.Append(protectedTagBase64 ?? string.Empty).Append('\n');
            sb.Append(protectedCiphertextBase64 ?? string.Empty).Append('\n');
            AppendKdfDescriptor(sb, protectedKdfDescriptor);
        }

        return ComputeSha256(sb.ToString());
    }

    /// <summary>
    /// Appends the explicit versioned KDF descriptor line to a protected-envelope fingerprint.
    ///
    /// A null/empty descriptor appends nothing, so a pre-versioning row reproduces its exact
    /// historical fingerprint and keeps matching its stored sync state. A present descriptor
    /// adds one line, so a descriptor-only migration (same ciphertext, same work factor) is
    /// still detected as a local modification and republished.
    /// </summary>
    private static void AppendKdfDescriptor(StringBuilder sb, string? descriptor)
    {
        if (string.IsNullOrEmpty(descriptor))
        {
            return;
        }

        sb.Append("KDF\n");
        sb.Append(descriptor).Append('\n');
    }

    public static string ComputeNoteFingerprint(
        string? text,
        bool isPinned,
        bool isFavorite,
        bool isInbox,
        string? sourceProcessName,
        string? sourceWindowTitle,
        string? sourceUrl,
        DateTime? capturedAtUtc,
        bool isDeleted,
        IEnumerable<(Guid TagSyncId, TagOrigin Origin, bool IsSuppressed)>? tags,
        bool isProtected = false,
        int protectedFormatVersion = 0,
        int protectedKdfIterations = 0,
        string? protectedSaltBase64 = null,
        string? protectedNonceBase64 = null,
        string? protectedTagBase64 = null,
        string? protectedCiphertextBase64 = null,
        string? protectedKdfDescriptor = null,
        Guid? protectedOriginalSyncId = null)
        => ComputeNoteFingerprint(
            title: string.Empty,
            text: text,
            isPinned: isPinned,
            isFavorite: isFavorite,
            isInbox: isInbox,
            sourceProcessName: sourceProcessName,
            sourceWindowTitle: sourceWindowTitle,
            sourceUrl: sourceUrl,
            capturedAtUtc: capturedAtUtc,
            isDeleted: isDeleted,
            tags: tags,
            isProtected: isProtected,
            protectedFormatVersion: protectedFormatVersion,
            protectedKdfIterations: protectedKdfIterations,
            protectedSaltBase64: protectedSaltBase64,
            protectedNonceBase64: protectedNonceBase64,
            protectedTagBase64: protectedTagBase64,
            protectedCiphertextBase64: protectedCiphertextBase64,
            protectedKdfDescriptor: protectedKdfDescriptor,
            protectedOriginalSyncId: protectedOriginalSyncId);

    public static string ComputeNoteFingerprint(
        string? title,
        string? text,
        bool isPinned,
        bool isFavorite,
        bool isInbox,
        string? sourceProcessName,
        string? sourceWindowTitle,
        string? sourceUrl,
        DateTime? capturedAtUtc,
        bool isDeleted,
        IEnumerable<(Guid TagSyncId, TagOrigin Origin, bool IsSuppressed)>? tags,
        bool isProtected = false,
        int protectedFormatVersion = 0,
        int protectedKdfIterations = 0,
        string? protectedSaltBase64 = null,
        string? protectedNonceBase64 = null,
        string? protectedTagBase64 = null,
        string? protectedCiphertextBase64 = null,
        string? protectedKdfDescriptor = null,
        Guid? protectedOriginalSyncId = null)
    {
        if (isDeleted)
        {
            return ComputeTombstoneFingerprint("Note");
        }

        var sb = new StringBuilder();
        sb.Append("NOTE\n");
        sb.Append(isProtected ? string.Empty : (title ?? string.Empty)).Append('\n');
        sb.Append(text ?? string.Empty).Append('\n');
        sb.Append(isPinned ? "1" : "0").Append('\n');
        sb.Append(isFavorite ? "1" : "0").Append('\n');
        sb.Append(isInbox ? "1" : "0").Append('\n');
        sb.Append(sourceProcessName ?? string.Empty).Append('\n');
        sb.Append(sourceWindowTitle ?? string.Empty).Append('\n');
        sb.Append(sourceUrl ?? string.Empty).Append('\n');

        if (capturedAtUtc.HasValue)
        {
            var utc = SyncPackageExporter.NormalizeToUtc(capturedAtUtc.Value);
            // Truncate to milliseconds for deterministic representation across SQLite and memory
            var truncated = new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, utc.Second, utc.Millisecond, DateTimeKind.Utc);
            sb.Append(truncated.ToString("O", CultureInfo.InvariantCulture));
        }
        sb.Append('\n');

        if (tags != null)
        {
            var sortedTags = tags
                .DistinctBy(t => (t.TagSyncId, t.Origin, t.IsSuppressed))
                .OrderBy(t => t.TagSyncId);
            foreach (var tag in sortedTags)
            {
                sb.Append(tag.TagSyncId.ToString("D"))
                  .Append('|')
                  .Append((int)tag.Origin)
                  .Append('|')
                  .Append(tag.IsSuppressed ? "1" : "0")
                  .Append('\n');
            }
        }

        sb.Append(isProtected ? "1" : "0").Append('\n');
        if (isProtected)
        {
            sb.Append(protectedFormatVersion.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append(protectedKdfIterations.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append(protectedSaltBase64 ?? string.Empty).Append('\n');
            sb.Append(protectedNonceBase64 ?? string.Empty).Append('\n');
            sb.Append(protectedTagBase64 ?? string.Empty).Append('\n');
            sb.Append(protectedCiphertextBase64 ?? string.Empty).Append('\n');
            AppendKdfDescriptor(sb, protectedKdfDescriptor);
            sb.Append(protectedOriginalSyncId?.ToString("D") ?? string.Empty).Append('\n');
        }

        return ComputeSha256(sb.ToString());
    }
}
