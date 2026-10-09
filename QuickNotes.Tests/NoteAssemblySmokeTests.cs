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
public sealed class NoteAssemblySmokeTests
{
    [Fact]
    public void GenerateAcceptanceScreenshots_SetupPreviewDestructiveSummary_LightDark_WideNarrow()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);
        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_asm_screens_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);
        string outputDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "artifacts", "quick-note-assembly-acceptance"));
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
                    NoteAssemblyUiSmokeRunner.Run(outputDir);
                }
                finally
                {
                    QuickNotesDbContext.ProfileDirectoryOverride = previous;
                }
            }, TimeSpan.FromSeconds(90));

            string[] expected =
            {
                "setup-light-wide.png", "setup-dark-wide.png", "setup-light-narrow.png", "setup-dark-narrow.png",
                "preview-light-wide.png", "preview-dark-wide.png", "preview-light-narrow.png", "preview-dark-narrow.png",
                "destructive-light-wide.png", "destructive-dark-wide.png", "destructive-light-narrow.png", "destructive-dark-narrow.png",
                "summary-light-wide.png", "summary-dark-wide.png", "summary-light-narrow.png", "summary-dark-narrow.png"
            };

            foreach (var name in expected)
            {
                string path = Path.Combine(outputDir, name);
                Assert.True(File.Exists(path), path);
                int w = name.Contains("narrow", StringComparison.Ordinal) ? 640 : 780;
                int h = name.Contains("narrow", StringComparison.Ordinal) ? 560 : 720;
                ThreePaneUiSmokeRunner.ValidateScreenshot(path, w, h);
            }

            foreach (var themeSize in new[] { "light-wide", "dark-wide", "light-narrow", "dark-narrow" })
            {
                byte[] setup = File.ReadAllBytes(Path.Combine(outputDir, "setup-" + themeSize + ".png"));
                byte[] preview = File.ReadAllBytes(Path.Combine(outputDir, "preview-" + themeSize + ".png"));
                byte[] destructive = File.ReadAllBytes(Path.Combine(outputDir, "destructive-" + themeSize + ".png"));
                byte[] summary = File.ReadAllBytes(Path.Combine(outputDir, "summary-" + themeSize + ".png"));
                Assert.False(setup.SequenceEqual(preview), "setup and preview shots must differ for " + themeSize);
                Assert.False(preview.SequenceEqual(destructive), "preview and destructive shots must differ for " + themeSize);
                Assert.False(destructive.SequenceEqual(summary), "destructive and summary shots must differ for " + themeSize);
                Assert.False(setup.SequenceEqual(summary), "setup and summary shots must differ for " + themeSize);
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
        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_asm_proc_profile_" + Guid.NewGuid().ToString("N"));
        string tempArtifacts = Path.Combine(Path.GetTempPath(), "qn_asm_proc_art_" + Guid.NewGuid().ToString("N"));
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
                Arguments = $"--isolated-profile \"{tempProfile}\" --quick-note-assembly-smoke --output-dir \"{tempArtifacts}\"",
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
            Assert.Contains("QN_NOTE_ASSEMBLY_SMOKE_SUCCESS", stdout);
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
