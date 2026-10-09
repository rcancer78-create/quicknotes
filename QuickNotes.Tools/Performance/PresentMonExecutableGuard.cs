using System;
using System.Diagnostics;
using System.IO;

namespace QuickNotes.Tools.Performance;

public static class PresentMonExecutableGuard
{
    public static string NormalizeAndValidate(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException(MissingMessage());
        }

        string trimmed = path.Trim();
        if (trimmed.Contains("://", StringComparison.Ordinal)
            || trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "PresentMon path must be a local executable that is already installed. Download URLs are refused.");
        }

        string full = Path.GetFullPath(trimmed);
        if (!File.Exists(full))
        {
            throw new InvalidOperationException(MissingMessage() + " Missing file: " + full);
        }

        string name = Path.GetFileName(full);
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || name.IndexOf("PresentMon", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException(
                "Expected a local PresentMon*.exe. Refusing '" + name + "'. " + MissingMessage());
        }

        return full;
    }

    public static string ReadFileVersion(string validatedExe)
    {
        try
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(validatedExe);
            if (!string.IsNullOrWhiteSpace(info.FileVersion))
            {
                return info.FileVersion;
            }

            if (!string.IsNullOrWhiteSpace(info.ProductVersion))
            {
                return info.ProductVersion;
            }
        }
        catch (Exception ex)
        {
            return "unreadable (" + ex.GetType().Name + ")";
        }

        return "unknown";
    }

    public static string MissingMessage()
    {
        return "PresentMon is not installed at the given path. Install Intel PresentMon (or another PresentMon.exe) locally, then pass -PresentMonPath. "
            + "This harness will not download tools, will not use the network, and will not substitute CompositionTarget.Rendering timings for presentation FPS.";
    }
}
