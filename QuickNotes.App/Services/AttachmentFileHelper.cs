using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace QuickNotes.App.Services;

public static class AttachmentFileHelper
{
    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        { ".png", "image/png" },
        { ".jpg", "image/jpeg" },
        { ".jpeg", "image/jpeg" },
        { ".gif", "image/gif" },
        { ".bmp", "image/bmp" },
        { ".webp", "image/webp" },
        { ".ico", "image/x-icon" },
        { ".svg", "image/svg+xml" },
        { ".tif", "image/tiff" },
        { ".tiff", "image/tiff" },
        { ".pdf", "application/pdf" },
        { ".txt", "text/plain" },
        { ".md", "text/markdown" },
        { ".json", "application/json" },
        { ".xml", "application/xml" },
        { ".csv", "text/csv" },
        { ".html", "text/html" },
        { ".htm", "text/html" },
        { ".zip", "application/zip" },
        { ".7z", "application/x-7z-compressed" },
        { ".rar", "application/vnd.rar" },
        { ".tar", "application/x-tar" },
        { ".gz", "application/gzip" },
        { ".doc", "application/msword" },
        { ".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document" },
        { ".xls", "application/vnd.ms-excel" },
        { ".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" },
        { ".ppt", "application/vnd.ms-powerpoint" },
        { ".pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation" },
        { ".mp3", "audio/mpeg" },
        { ".wav", "audio/wav" },
        { ".mp4", "video/mp4" },
        { ".avi", "video/x-msvideo" }
    };

    public static string GetMimeType(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return "application/octet-stream";

        var ext = Path.GetExtension(fileName);
        if (!string.IsNullOrEmpty(ext) && MimeTypes.TryGetValue(ext, out var mime))
        {
            return mime;
        }

        return "application/octet-stream";
    }

    public static bool IsImage(string? contentType, string? fileName)
    {
        if (!string.IsNullOrEmpty(contentType) && contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrEmpty(fileName))
        {
            var ext = Path.GetExtension(fileName);
            if (!string.IsNullOrEmpty(ext))
            {
                return ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".ico", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".svg", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".tif", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".tiff", StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    public static string GetDisplayTypeName(string? contentType, string? fileName)
    {
        var ext = !string.IsNullOrEmpty(fileName) ? Path.GetExtension(fileName).ToLowerInvariant() : string.Empty;
        return ext switch
        {
            ".png" => "PNG изображение",
            ".jpg" or ".jpeg" => "JPEG изображение",
            ".gif" => "GIF изображение",
            ".bmp" => "BMP изображение",
            ".webp" => "WebP изображение",
            ".svg" => "SVG векторное изображение",
            ".pdf" => "PDF документ",
            ".txt" => "Текстовый файл",
            ".md" => "Markdown документ",
            ".json" => "JSON файл",
            ".xml" => "XML файл",
            ".csv" => "CSV таблица",
            ".doc" or ".docx" => "Word документ",
            ".xls" or ".xlsx" => "Excel таблица",
            ".ppt" or ".pptx" => "PowerPoint презентация",
            ".zip" or ".7z" or ".rar" or ".tar" or ".gz" => "Архив",
            _ when !string.IsNullOrEmpty(contentType) && contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) => "Изображение",
            _ when !string.IsNullOrEmpty(ext) => ext.TrimStart('.').ToUpperInvariant(),
            _ => "Файл"
        };
    }

    public static string FormatFileSize(long bytes)
    {
        if (bytes < 0) return "0 Б";
        if (bytes < 1024)
            return $"{bytes} Б";
        if (bytes < 1024 * 1024)
            return $"{(bytes / 1024.0):0.#} КБ";
        return $"{(bytes / (1024.0 * 1024.0)):0.##} МБ";
    }

    public static string GetSafeStoredFileName(string sha256, string originalFileName)
    {
        if (string.IsNullOrWhiteSpace(sha256) || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Недопустимая контрольная сумма SHA-256.", nameof(sha256));
        }

        string rawExt = Path.GetExtension(originalFileName) ?? string.Empty;
        var cleanChars = rawExt
            .TrimStart('.')
            .Where(char.IsLetterOrDigit)
            .Take(12)
            .ToArray();

        string safeExt = cleanChars.Length > 0 ? "." + new string(cleanChars).ToLowerInvariant() : string.Empty;
        string storedFileName = $"{sha256.ToLowerInvariant()}{safeExt}";

        // Prevent any directory traversal or path separators
        if (Path.GetFileName(storedFileName) != storedFileName ||
            storedFileName.Contains('/') ||
            storedFileName.Contains('\\') ||
            storedFileName.Contains(".."))
        {
            throw new InvalidOperationException("Недопустимое хранимое имя файла.");
        }

        return storedFileName;
    }

    public static string ComputeSha256(Stream stream)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return ComputeSha256(stream);
    }

    public static string ComputeSha256(byte[] data)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(data ?? Array.Empty<byte>());
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
