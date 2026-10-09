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
public sealed class TasksIndexSmokeTests
{
    [Fact]
    public void GenerateAcceptanceScreenshots_ListDueEmptyNavigation_LightDark_WideNarrow()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);
        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_tasks_screens_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);
        string outputDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "artifacts", "tasks-index-acceptance"));
        Directory.CreateDirectory(outputDir);

        try
        {
            StaTestHarness.Run(() =>
            {
                var previous = QuickNotesDbContext.ProfileDirectoryOverride;
                QuickNotesDbContext.ProfileDirectoryOverride = tempProfile;
                try
                {
                    using var fixture = new TestProfileComposition(tempProfile, seedNotes: 0, ownsDirectory: false);
                    var (window, _) = fixture;
                    window.Show();
                    TasksIndexUiSmokeRunner.Run(window, outputDir);
                    window.Close();
                }
                finally
                {
                    QuickNotesDbContext.ProfileDirectoryOverride = previous;
                }
            }, TimeSpan.FromSeconds(90));

            string[] expected =
            {
                "list-light-wide.png", "list-dark-wide.png", "list-light-narrow.png", "list-dark-narrow.png",
                "due-date-light-wide.png", "due-date-dark-wide.png", "due-date-light-narrow.png", "due-date-dark-narrow.png",
                "empty-or-search-light-wide.png", "empty-or-search-dark-wide.png", "empty-or-search-light-narrow.png", "empty-or-search-dark-narrow.png",
                "navigation-light-wide.png", "navigation-dark-wide.png", "navigation-light-narrow.png", "navigation-dark-narrow.png"
            };

            foreach (var name in expected)
            {
                string path = Path.Combine(outputDir, name);
                Assert.True(File.Exists(path), path);
                int w = name.Contains("narrow", StringComparison.Ordinal) ? 640 : 780;
                int h = name.Contains("narrow", StringComparison.Ordinal) ? 560 : 720;
                ThreePaneUiSmokeRunner.ValidateScreenshot(path, w, h);
                byte[] bytes = File.ReadAllBytes(path);
                Assert.DoesNotContain(TasksIndexUiSmokeRunner.SecretPlaintext, System.Text.Encoding.UTF8.GetString(bytes));
            }

            foreach (var themeSize in new[] { "light-wide", "dark-wide", "light-narrow", "dark-narrow" })
            {
                byte[] list = File.ReadAllBytes(Path.Combine(outputDir, "list-" + themeSize + ".png"));
                byte[] due = File.ReadAllBytes(Path.Combine(outputDir, "due-date-" + themeSize + ".png"));
                byte[] empty = File.ReadAllBytes(Path.Combine(outputDir, "empty-or-search-" + themeSize + ".png"));
                byte[] nav = File.ReadAllBytes(Path.Combine(outputDir, "navigation-" + themeSize + ".png"));
                Assert.False(list.SequenceEqual(due), "list and due-date must differ for " + themeSize);
                Assert.False(due.SequenceEqual(empty), "due-date and empty must differ for " + themeSize);
                Assert.False(empty.SequenceEqual(nav), "empty and navigation must differ for " + themeSize);
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
        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_tasks_proc_profile_" + Guid.NewGuid().ToString("N"));
        string tempArtifacts = Path.Combine(Path.GetTempPath(), "qn_tasks_proc_art_" + Guid.NewGuid().ToString("N"));
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
                Arguments = $"--isolated-profile \"{tempProfile}\" --tasks-index-smoke --output-dir \"{tempArtifacts}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            Assert.NotNull(process);
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            bool exited = process.WaitForExit(60000);
            Assert.True(exited, "smoke process hung");
            Assert.True(process.ExitCode == 0, stdout + stderr);
            Assert.Contains("QN_TASKS_INDEX_SMOKE_SUCCESS", stdout);
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
