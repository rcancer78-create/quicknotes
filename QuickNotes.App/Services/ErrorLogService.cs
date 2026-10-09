using System;
using System.IO;
using System.Text;

namespace QuickNotes.App.Services;

/// <summary>
/// Size-limited error log. Does not write note bodies.
/// </summary>
public static class ErrorLogService
{
    public const long MaxFileBytes = 512 * 1024;

    private static readonly System.Threading.AsyncLocal<string?> _scopedLogDirectory = new();
    private static string? _logDirectoryOverride;

    public static string? ScopedLogDirectory
    {
        get => _scopedLogDirectory.Value;
        set => _scopedLogDirectory.Value = value;
    }

    public static string? LogDirectoryOverride
    {
        get => _logDirectoryOverride;
        set => _logDirectoryOverride = value;
    }

    public static string LogDirectory
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_scopedLogDirectory.Value))
            {
                return _scopedLogDirectory.Value;
            }
            if (!string.IsNullOrWhiteSpace(_logDirectoryOverride))
            {
                return _logDirectoryOverride;
            }
            return Path.Combine(QuickNotes.App.Data.QuickNotesDbContext.GetDefaultProfileDirectory(), "Logs");
        }
    }

    public static string LogFilePath => Path.Combine(LogDirectory, "errors.log");

    public static IDisposable UseScopedDirectory(string directory)
    {
        var prev = _scopedLogDirectory.Value;
        _scopedLogDirectory.Value = directory;
        return new LogDirectoryScope(() => _scopedLogDirectory.Value = prev);
    }

    private sealed class LogDirectoryScope : IDisposable
    {
        private Action? _reset;
        public LogDirectoryScope(Action reset) => _reset = reset;
        public void Dispose()
        {
            System.Threading.Interlocked.Exchange(ref _reset, null)?.Invoke();
        }
    }

    public static void Write(string source, Exception ex)
    {
        Write(source, CloudErrorSanitizer.FormatExceptionForLog(ex));
    }

    public static void Write(string source, string message)
    {
        try
        {
            string targetDir = LogDirectory;
            Directory.CreateDirectory(targetDir);
            RotateIfNeeded();
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{Sanitize(source)}] {Sanitize(message)}{Environment.NewLine}";
            File.AppendAllText(LogFilePath, line, Encoding.UTF8);
        }
        catch (Exception logEx)
        {
            System.Diagnostics.Debug.WriteLine($"ErrorLogService failed: {logEx.Message}");
        }
    }

    private static void RotateIfNeeded()
    {
        if (!File.Exists(LogFilePath))
            return;

        var info = new FileInfo(LogFilePath);
        if (info.Length < MaxFileBytes)
            return;

        var archive = LogFilePath + ".1";
        if (File.Exists(archive))
            File.Delete(archive);
        File.Move(LogFilePath, archive);
    }

    public static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var compact = string.Join(" ", CloudErrorSanitizer.RedactSecrets(value)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (compact.Length > 400)
            compact = compact[..400] + "…";
        return compact;
    }
}
