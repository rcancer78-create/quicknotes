using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using QuickNotes.App.Data;
using QuickNotes.App.Services;
using Xunit;

namespace QuickNotes.Tests;

[Collection(LiveProfileSnapshotCollection.Name)]
[TestCategory(TestCategories.UiSensitive)]
public sealed class SyncConflictSmokeTests
{
    [Fact]
    public void GenerateAcceptanceScreenshots_CompareMergeProtectedSuccess_LightDark_WideNarrow()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);
        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_syncconf_screens_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);
        string outputDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "artifacts", "sync-conflict-acceptance"));
        Directory.CreateDirectory(outputDir);

        try
        {
            StaTestHarness.Run(() =>
            {
                var previous = QuickNotesDbContext.ProfileDirectoryOverride;
                QuickNotesDbContext.ProfileDirectoryOverride = tempProfile;
                try
                {
                    using var db = new QuickNotesDbContext();
                    DbInitializer.Initialize(db);
                    SyncConflictUiSmokeRunner.Run(outputDir);
                }
                finally
                {
                    QuickNotesDbContext.ProfileDirectoryOverride = previous;
                }
            }, TimeSpan.FromSeconds(90));

            string[] expected =
            {
                "compare-light-wide.png", "compare-dark-wide.png", "compare-light-narrow.png", "compare-dark-narrow.png",
                "merge-light-wide.png", "merge-dark-wide.png", "merge-light-narrow.png", "merge-dark-narrow.png",
                "protected-light-wide.png", "protected-dark-wide.png", "protected-light-narrow.png", "protected-dark-narrow.png",
                "success-light-wide.png", "success-dark-wide.png", "success-light-narrow.png", "success-dark-narrow.png"
            };

            foreach (var name in expected)
            {
                string path = Path.Combine(outputDir, name);
                Assert.True(File.Exists(path), path);
                int w = name.Contains("narrow", StringComparison.Ordinal) ? 640 : 780;
                int h = name.Contains("narrow", StringComparison.Ordinal) ? 560 : 720;
                ThreePaneUiSmokeRunner.ValidateScreenshot(path, w, h);
                byte[] bytes = File.ReadAllBytes(path);
                Assert.DoesNotContain(SyncConflictUiSmokeRunner.SecretPlaintext, System.Text.Encoding.UTF8.GetString(bytes));
            }

            foreach (var themeSize in new[] { "light-wide", "dark-wide", "light-narrow", "dark-narrow" })
            {
                byte[] compare = File.ReadAllBytes(Path.Combine(outputDir, "compare-" + themeSize + ".png"));
                byte[] merge = File.ReadAllBytes(Path.Combine(outputDir, "merge-" + themeSize + ".png"));
                byte[] protectedShot = File.ReadAllBytes(Path.Combine(outputDir, "protected-" + themeSize + ".png"));
                byte[] success = File.ReadAllBytes(Path.Combine(outputDir, "success-" + themeSize + ".png"));
                Assert.False(compare.SequenceEqual(merge), "compare and merge must differ for " + themeSize);
                Assert.False(merge.SequenceEqual(protectedShot), "merge and protected must differ for " + themeSize);
                Assert.False(protectedShot.SequenceEqual(success), "protected and success must differ for " + themeSize);
            }

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void RunIsolatedUiSmokeProcess_GeneratesValidAcceptanceScreenshots_AndExitsZero()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);
        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_syncconf_proc_profile_" + Guid.NewGuid().ToString("N"));
        string tempArtifacts = Path.Combine(Path.GetTempPath(), "qn_syncconf_proc_art_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);
        Directory.CreateDirectory(tempArtifacts);
        try
        {
            string baseDir = AppContext.BaseDirectory;
            string releaseExe = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "QuickNotes.App", "bin", "Release", "net8.0-windows10.0.19041.0", "QuickNotes.App.exe"));
            string candidateExe = File.Exists(releaseExe)
                ? releaseExe
                : Path.GetFullPath(Path.Combine(baseDir, "QuickNotes.App.exe"));
            if (!File.Exists(candidateExe))
            {
                var matches = Directory.GetFiles(Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..")), "QuickNotes.App.exe", SearchOption.AllDirectories);
                if (matches.Length > 0)
                {
                    candidateExe = matches[0];
                }
            }

            Assert.True(File.Exists(candidateExe), candidateExe);
            var psi = new ProcessStartInfo
            {
                FileName = candidateExe,
                Arguments = $"--isolated-profile \"{tempProfile}\" --sync-conflict-smoke --output-dir \"{tempArtifacts}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            var process = ProcessTestHarness.Run(psi, TimeSpan.FromMilliseconds(60000));
            string stdout = process.StandardOutput;
            string stderr = process.StandardError;
            Assert.True(process.ExitCode == 0, stdout + stderr);
            Assert.Contains("QN_SYNC_CONFLICT_SMOKE_SUCCESS", stdout);
            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
            SqliteTestUtil.TryDeleteDirectory(tempArtifacts);
        }
    }

    private static string[] SnapshotLiveProfile(string liveRoot)
    {
        if (!Directory.Exists(liveRoot))
        {
            return Array.Empty<string>();
        }

        return Directory.GetFileSystemEntries(liveRoot, "*", SearchOption.AllDirectories)
            .Select(p => p + "|" + (File.Exists(p) ? new FileInfo(p).Length + "|" + File.GetLastWriteTimeUtc(p).Ticks : "dir"))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
