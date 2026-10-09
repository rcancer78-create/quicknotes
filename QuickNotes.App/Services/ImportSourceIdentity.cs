using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using QuickNotes.App.Models;

namespace QuickNotes.App.Services;

public static class ImportSourceIdentity
{
    private static readonly Regex MarkdownImage = new(@"!\[[^\]]*\]\(([^)]+)\)", RegexOptions.Compiled);

    public static string ComputeContentSha256(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string ComputeFingerprint(string relativePath, string contentSha256)
    {
        string payload = $"{NormalizeRelative(relativePath)}|{contentSha256 ?? string.Empty}";
        return ComputeContentSha256(Encoding.UTF8.GetBytes(payload));
    }

    public static string ComputePlanHash(IEnumerable<ImportItemPreview> items, IEnumerable<ImportDiagnosticItem> diagnostics)
    {
        var sb = new StringBuilder();
        foreach (var item in items.OrderBy(i => i.SourceRelativePath ?? i.SourceFileName, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(item.SourceFingerprint).Append('|')
                .Append(item.SourceLength).Append('|')
                .Append(item.SourceLastWriteUtcTicks).Append('|')
                .Append(item.SourceRelativePath).Append('\n');
        }

        foreach (var d in diagnostics.OrderBy(x => x.FileName, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append("D:").Append(d.FileName).Append('|').Append(d.Reason).Append('\n');
        }

        return ComputeContentSha256(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    public static string NormalizeRelative(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return path.Replace('\\', '/').Trim().TrimStart('/');
    }

    public static bool ShouldCommit(ImportItemPreview item)
    {
        if (item.HasBlockingIssue)
        {
            return false;
        }

        if (item.DuplicateKind == ImportDuplicateKind.None)
        {
            return true;
        }

        return item.DuplicateAction is ImportDuplicateAction.ImportSeparate or ImportDuplicateAction.Replace;
    }

    public static void CollectMarkdownAttachments(ImportItemPreview item, string importRoot)
    {
        if (string.IsNullOrWhiteSpace(item.Text) || string.IsNullOrWhiteSpace(item.SourcePath))
        {
            return;
        }

        foreach (Match match in MarkdownImage.Matches(item.Text))
        {
            string src = match.Groups[1].Value.Trim().Trim('"', '\'');
            if (string.IsNullOrWhiteSpace(src) || src.Contains("://", StringComparison.Ordinal) || src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                item.LostElements.Add("Внешняя или data-ссылка изображения в Markdown отброшена.");
                continue;
            }

            if (!ImportPathSafety.TryResolveLocalRelative(item.SourcePath, src, importRoot, out var full, out var error))
            {
                item.AttachmentIssues.Add(error ?? "Небезопасный путь вложения.");
                continue;
            }

            if (!File.Exists(full))
            {
                item.AttachmentIssues.Add($"Локальный файл вложения не найден: {Path.GetFileName(full)}");
                continue;
            }

            if (item.PendingAttachments.Any(a => string.Equals(a.SourceFullPath, full, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            item.PendingAttachments.Add(new ImportLocalAttachment
            {
                SourceFullPath = full,
                OriginalFileName = Path.GetFileName(full),
                MarkdownPlaceholder = src
            });
        }
    }

    public static void ApplyHtmlAttachments(ImportItemPreview item, HtmlConversionResult converted, string importRoot)
    {
        foreach (var att in converted.LocalAttachments)
        {
            if (!ImportPathSafety.TryResolveLocalRelative(item.SourcePath, att.RawSrc, importRoot, out var full, out var error))
            {
                item.AttachmentIssues.Add(error ?? "Небезопасный путь изображения.");
                continue;
            }

            if (!File.Exists(full))
            {
                item.AttachmentIssues.Add($"Локальное изображение не найдено: {Path.GetFileName(full)}");
                continue;
            }

            item.PendingAttachments.Add(new ImportLocalAttachment
            {
                SourceFullPath = full,
                OriginalFileName = Path.GetFileName(full),
                MarkdownPlaceholder = att.RawSrc
            });
        }
    }
}
