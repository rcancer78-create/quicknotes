using System;
using System.IO;
using QuickNotes.App.Data;

namespace QuickNotes.Tools.Performance;

/// <summary>
/// Publish output may live only under artifacts/publish/cold-start/&lt;run-id&gt;/{baseline,r2r}
/// or a unique temp directory. Ordinary project bin/obj must never receive publish files.
/// </summary>
public static class ReadyToRunPublishPathGuard
{
    public const string BaselineLeaf = "baseline";
    public const string R2rLeaf = "r2r";

    public static string NormalizeAndValidatePublishDirectory(string path, string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Publish directory cannot be empty.", nameof(path));
        }

        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            throw new ArgumentException("Repository root cannot be empty.", nameof(repoRoot));
        }

        string full = Path.GetFullPath(path);
        string root = Path.GetFullPath(repoRoot);

        RejectAppBinOrObj(full, root);

        if (IsUnderTempDirectory(full))
        {
            return full;
        }

        string coldStartRoot = Path.Combine(root, "artifacts", "publish", "cold-start");
        if (IsSameOrUnder(full, coldStartRoot))
        {
            ValidateColdStartLayout(full, coldStartRoot);
            return full;
        }

        throw new InvalidOperationException(
            "Publish directory must be under artifacts/publish/cold-start/<run-id>/{baseline|r2r} or a unique temp directory.");
    }

    public static string NormalizeAndValidatePublishedExe(string exePath, string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            throw new ArgumentException("Executable path cannot be empty.", nameof(exePath));
        }

        string fullExe = Path.GetFullPath(exePath);
        string? dir = Path.GetDirectoryName(fullExe);
        if (string.IsNullOrEmpty(dir))
        {
            throw new InvalidOperationException("Executable path has no directory.");
        }

        NormalizeAndValidatePublishDirectory(dir, repoRoot);
        if (!string.Equals(Path.GetFileName(fullExe), "QuickNotes.App.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Published executable must be named QuickNotes.App.exe.");
        }

        return fullExe;
    }

    public static void RejectAppBinOrObj(string fullPath, string repoRoot)
    {
        string appBin = Path.Combine(repoRoot, "QuickNotes.App", "bin");
        string appObj = Path.Combine(repoRoot, "QuickNotes.App", "obj");
        if (IsSameOrUnder(fullPath, appBin) || IsSameOrUnder(fullPath, appObj))
        {
            throw new InvalidOperationException(
                "Publish output must not be written into QuickNotes.App/bin or QuickNotes.App/obj.");
        }
    }

    public static bool IsUnderTempDirectory(string fullPath)
    {
        string temp = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string candidate = Path.GetFullPath(fullPath);
        return IsSameOrUnder(candidate, temp);
    }

    public static void ValidateIsolatedLaunchProfile(string profileDirectory)
    {
        QuickNotesDbContext.ValidateIsolatedProfilePath(profileDirectory);
        string full = Path.GetFullPath(profileDirectory);
        if (!IsUnderTempDirectory(full))
        {
            throw new InvalidOperationException(
                "Cold-start launch profile must be a unique directory under the process temp path.");
        }
    }

    public static string[] BuildPerfStartupArguments(string isolatedProfileDirectory)
    {
        ValidateIsolatedLaunchProfile(isolatedProfileDirectory);
        string full = Path.GetFullPath(isolatedProfileDirectory);
        return new[] { "--isolated-profile", full, "--perf-startup" };
    }

    private static void ValidateColdStartLayout(string fullPublishDir, string coldStartRoot)
    {
        string relative = Path.GetRelativePath(coldStartRoot, fullPublishDir);
        string[] parts = relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 2)
        {
            throw new InvalidOperationException(
                "Cold-start publish path must be artifacts/publish/cold-start/<run-id>/{baseline|r2r}.");
        }

        string runId = parts[0];
        string leaf = parts[1];
        if (runId.Contains("..", StringComparison.Ordinal)
            || string.Equals(runId, "bin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(runId, "obj", StringComparison.OrdinalIgnoreCase)
            || runId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException("Cold-start run-id is not a safe unique directory name.");
        }

        if (!string.Equals(leaf, BaselineLeaf, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(leaf, R2rLeaf, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Cold-start publish leaf must be 'baseline' or 'r2r'.");
        }
    }

    private static bool IsSameOrUnder(string candidate, string ancestor)
    {
        string left = Path.GetFullPath(candidate)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string right = Path.GetFullPath(ancestor)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return left.StartsWith(right + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || left.StartsWith(right + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
