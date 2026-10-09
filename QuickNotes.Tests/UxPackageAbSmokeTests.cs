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
public sealed class UxPackageAbSmokeTests
{
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    [Fact]
    public void Parse_RecognizesUxPackageAbSmokeSwitch()
    {
        string profile = Path.Combine(Path.GetTempPath(), "qn_uxab_cli_" + Guid.NewGuid().ToString("N"));
        var parsed = IsolatedProfileAppCli.Parse(new[]
        {
            "--isolated-profile", profile,
            "--ux-package-ab-smoke",
            "--output-dir", Path.Combine(Path.GetTempPath(), "qn_uxab_out")
        });
        Assert.True(parsed.IsIsolated);
        Assert.True(parsed.UxPackageAbSmoke);
        Assert.False(parsed.UxC08Smoke);
        Assert.False(parsed.UxC11Smoke);
        Assert.False(parsed.SyncConflictSmoke);
        Assert.False(parsed.ThreePaneSmoke);
    }

    [Fact]
    public void GenerateAcceptanceScreenshots_PackageAbScenes_LightDark_WideNarrow()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);
        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_uxab_screens_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);
        string outputDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "artifacts", "ux-package-ab-acceptance"));
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
                    UxPackageAbUiSmokeRunner.Run(window, outputDir);
                    window.Close();
                }
                finally
                {
                    QuickNotesDbContext.ProfileDirectoryOverride = previous;
                }
            }, TimeSpan.FromSeconds(120));

            Assert.Equal(UxPackageAbUiSmokeRunner.ExpectedFileNames.Length, UxPackageAbUiSmokeRunner.ExpectedFileNames.Distinct(StringComparer.Ordinal).Count());
            foreach (var name in UxPackageAbUiSmokeRunner.ExpectedFileNames)
            {
                string path = Path.Combine(outputDir, name);
                Assert.True(File.Exists(path), path);
                byte[] bytes = File.ReadAllBytes(path);
                Assert.True(bytes.Length >= PngSignature.Length, path);
                Assert.True(bytes.Take(PngSignature.Length).SequenceEqual(PngSignature), path);
                UxPackageAbUiSmokeRunner.ValidateOutput(path, name);
                AssertVisibleTextDumpHasNoSecret(path, name);
            }

            foreach (var themeSize in new[] { "light-wide", "dark-wide", "light-narrow", "dark-narrow" })
            {
                byte[] unsaved = File.ReadAllBytes(Path.Combine(outputDir, "unsaved-editor-" + themeSize + ".png"));
                byte[] tray = File.ReadAllBytes(Path.Combine(outputDir, "close-to-tray-" + themeSize + ".png"));
                byte[] firstRun = File.ReadAllBytes(Path.Combine(outputDir, "first-run-" + themeSize + ".png"));
                byte[] merge = File.ReadAllBytes(Path.Combine(outputDir, "merge-digits-" + themeSize + ".png"));
                byte[] library = File.ReadAllBytes(Path.Combine(outputDir, "empty-library-" + themeSize + ".png"));
                byte[] section = File.ReadAllBytes(Path.Combine(outputDir, "empty-section-" + themeSize + ".png"));
                byte[] search = File.ReadAllBytes(Path.Combine(outputDir, "search-empty-" + themeSize + ".png"));
                byte[] focus = File.ReadAllBytes(Path.Combine(outputDir, "focus-ring-" + themeSize + ".png"));
                byte[] discard = File.ReadAllBytes(Path.Combine(outputDir, "draft-discard-" + themeSize + ".png"));
                byte[] existingRecovery = File.ReadAllBytes(Path.Combine(outputDir, "draft-recovery-existing-" + themeSize + ".png"));
                byte[] newRecovery = File.ReadAllBytes(Path.Combine(outputDir, "draft-recovery-new-" + themeSize + ".png"));
                Assert.False(unsaved.SequenceEqual(tray), "unsaved and close-to-tray must differ for " + themeSize);
                Assert.False(tray.SequenceEqual(firstRun), "close-to-tray and first-run must differ for " + themeSize);
                Assert.False(library.SequenceEqual(section), "empty library and section must differ for " + themeSize);
                Assert.False(section.SequenceEqual(search), "empty section and search must differ for " + themeSize);
                Assert.False(search.SequenceEqual(focus), "search empty and focus ring must differ for " + themeSize);
                Assert.False(merge.SequenceEqual(library), "merge digits and empty library must differ for " + themeSize);
                Assert.False(existingRecovery.SequenceEqual(newRecovery), "existing-note and new-note draft recovery must differ for " + themeSize);
                Assert.False(discard.SequenceEqual(existingRecovery), "draft discard and existing recovery must differ for " + themeSize);
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
        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_uxab_proc_profile_" + Guid.NewGuid().ToString("N"));
        string tempArtifacts = Path.Combine(Path.GetTempPath(), "qn_uxab_proc_art_" + Guid.NewGuid().ToString("N"));
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
                Arguments = $"--isolated-profile \"{tempProfile}\" --ux-package-ab-smoke --output-dir \"{tempArtifacts}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            var process = ProcessTestHarness.Run(psi, TimeSpan.FromMilliseconds(90000));
            string stdout = process.StandardOutput;
            string stderr = process.StandardError;
            Assert.True(process.ExitCode == 0, stdout + stderr);
            Assert.Contains(UxPackageAbUiSmokeRunner.SuccessMarker, stdout);

            foreach (var name in UxPackageAbUiSmokeRunner.ExpectedFileNames)
            {
                string path = Path.Combine(tempArtifacts, name);
                Assert.True(File.Exists(path), path);
                byte[] bytes = File.ReadAllBytes(path);
                Assert.True(bytes.Take(PngSignature.Length).SequenceEqual(PngSignature), path);
                UxPackageAbUiSmokeRunner.ValidateOutput(path, name);
                AssertVisibleTextDumpHasNoSecret(path, name);
            }

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
            SqliteTestUtil.TryDeleteDirectory(tempArtifacts);
        }
    }

    private static void AssertVisibleTextDumpHasNoSecret(string pngPath, string sceneName)
    {
        string dumpPath = UxPackageAbUiSmokeRunner.VisibleTextDumpPath(pngPath);
        Assert.True(File.Exists(dumpPath), dumpPath);
        string visible = File.ReadAllText(dumpPath);
        Assert.False(string.IsNullOrWhiteSpace(visible), sceneName);
        Assert.DoesNotContain(UxPackageAbUiSmokeRunner.SecretPlaintext, visible);
        UxPackageAbUiSmokeRunner.AssertVisibleTextHasNoSecret(visible, sceneName);
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
