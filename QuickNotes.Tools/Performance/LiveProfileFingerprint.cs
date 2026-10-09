using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using QuickNotes.App.Data;

namespace QuickNotes.Tools.Performance;

public sealed class LiveProfileFingerprint
{
    public bool DirectoryExists { get; init; }
    public string CombinedSha256Hex { get; init; } = string.Empty;
    public int FileCount { get; init; }

    public static string LiveDirectory => QuickNotesDbContext.LiveProfileDirectory;

    public static LiveProfileFingerprint Capture()
    {
        string live = LiveDirectory;
        if (!Directory.Exists(live))
        {
            return new LiveProfileFingerprint
            {
                DirectoryExists = false,
                CombinedSha256Hex = "ABSENT",
                FileCount = 0
            };
        }

        var lines = new List<string>();
        foreach (string file in Directory.GetFiles(live, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(live, file).Replace('\\', '/');
            long length = new FileInfo(file).Length;
            string hash = HashFile(file);
            lines.Add(relative + "|" + length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + hash);
        }

        lines.Sort(StringComparer.OrdinalIgnoreCase);
        string payload = string.Join("\n", lines);
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return new LiveProfileFingerprint
        {
            DirectoryExists = true,
            CombinedSha256Hex = Convert.ToHexString(digest),
            FileCount = lines.Count
        };
    }

    public static void AssertUnchanged(LiveProfileFingerprint before, LiveProfileFingerprint after)
    {
        if (!string.Equals(before.CombinedSha256Hex, after.CombinedSha256Hex, StringComparison.Ordinal)
            || before.DirectoryExists != after.DirectoryExists
            || before.FileCount != after.FileCount)
        {
            throw new InvalidOperationException(
                "Live profile (%LOCALAPPDATA%\\QuickNotes) hash changed during the run. Measurement is invalid.");
        }
    }

    private static string HashFile(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] digest = SHA256.HashData(stream);
        return Convert.ToHexString(digest);
    }
}
