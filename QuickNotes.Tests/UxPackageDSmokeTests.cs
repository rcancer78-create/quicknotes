using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using QuickNotes.App.Data;
using QuickNotes.App.Helpers;
using QuickNotes.App.Services;
using Xunit;

namespace QuickNotes.Tests;

[Collection(LiveProfileSnapshotCollection.Name)]
[TestCategory(TestCategories.UiSensitive)]
public sealed class UxPackageDSmokeTests
{
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    [Fact]
    public void Parse_RecognizesUxPackageDLayoutSmokeSwitch()
    {
        string profile = Path.Combine(Path.GetTempPath(), "qn_uxd_cli_" + Guid.NewGuid().ToString("N"));
        var parsed = IsolatedProfileAppCli.Parse(new[]
        {
            "--isolated-profile", profile,
            "--ux-package-d-layout-smoke",
            "--output-dir", Path.Combine(Path.GetTempPath(), "qn_uxd_out")
        });
        Assert.True(parsed.IsIsolated);
        Assert.True(parsed.UxPackageDLayoutSmoke);
        Assert.False(parsed.UxC14Smoke);
        Assert.False(parsed.ManualAcceptancePrep);
    }

    [Fact]
    public void GenerateAcceptanceArtifacts_ContrastHelpAndNames()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);
        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_uxd_screens_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);
        string outputDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "artifacts", "ux-package-d-acceptance"));
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
                    UxPackageDUiSmokeRunner.Run(window, outputDir);
                    window.Close();
                }
                finally
                {
                    QuickNotesDbContext.ProfileDirectoryOverride = previous;
                }
            }, TimeSpan.FromSeconds(120));

            foreach (var name in UxPackageDUiSmokeRunner.ExpectedPngFileNames)
            {
                string path = Path.Combine(outputDir, name);
                Assert.True(File.Exists(path), path);
                byte[] bytes = File.ReadAllBytes(path);
                Assert.True(bytes.Take(PngSignature.Length).SequenceEqual(PngSignature), path);
                UxPackageDUiSmokeRunner.ValidateOutput(path, name);
            }

            string contrastPath = Path.Combine(outputDir, UxPackageDUiSmokeRunner.ContrastReportFileName);
            string namesPath = Path.Combine(outputDir, UxPackageDUiSmokeRunner.NamesReportFileName);
            Assert.True(File.Exists(contrastPath), contrastPath);
            Assert.True(File.Exists(namesPath), namesPath);

            string contrastJson = File.ReadAllText(contrastPath);
            Assert.Contains("InkBrush", contrastJson, StringComparison.Ordinal);
            Assert.Contains("MutedBrush", contrastJson, StringComparison.Ordinal);
            Assert.Contains("physical pixel", contrastJson, StringComparison.OrdinalIgnoreCase);

            var names = JsonSerializer.Deserialize<UxPackageDUiSmokeRunner.NamesReport>(File.ReadAllText(namesPath));
            Assert.NotNull(names);
            Assert.Contains(CardGestureCopy.ExpandAutomationName, names!.CardExpandNames);
            Assert.Equal(UserTaskCopy.CloudWizardAutomationName, names.CloudWizardName);
            Assert.Equal(TagOriginCopy.RestoreAutomationName, names.RestoreAutomationName);

            string helpDump = File.ReadAllText(UxPackageDUiSmokeRunner.VisibleTextDumpPath(Path.Combine(outputDir, "help-light-wide.png")));
            Assert.Contains(CardGestureCopy.Expand, helpDump, StringComparison.Ordinal);
            Assert.Contains("Ctrl+E", helpDump, StringComparison.Ordinal);

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void RunIsolatedUiSmokeProcess_WritesContrastAndHelp_AndExitsZero()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);
        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_uxd_proc_profile_" + Guid.NewGuid().ToString("N"));
        string tempArtifacts = Path.Combine(Path.GetTempPath(), "qn_uxd_proc_art_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);
        Directory.CreateDirectory(tempArtifacts);
        try
        {
            string candidateExe = ResolveAppExe();
            Assert.True(File.Exists(candidateExe), candidateExe);
            var psi = new ProcessStartInfo
            {
                FileName = candidateExe,
                Arguments = $"--isolated-profile \"{tempProfile}\" --ux-package-d-layout-smoke --output-dir \"{tempArtifacts}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            var process = ProcessTestHarness.Run(psi, TimeSpan.FromMilliseconds(120000));
            string stdout = process.StandardOutput;
            string stderr = process.StandardError;
            Assert.True(process.ExitCode == 0, stdout + stderr);
            Assert.Contains(UxPackageDUiSmokeRunner.SuccessMarker, stdout);
            Assert.True(File.Exists(Path.Combine(tempArtifacts, UxPackageDUiSmokeRunner.ContrastReportFileName)));
            Assert.True(File.Exists(Path.Combine(tempArtifacts, UxPackageDUiSmokeRunner.NamesReportFileName)));
            foreach (var name in UxPackageDUiSmokeRunner.ExpectedPngFileNames)
            {
                UxPackageDUiSmokeRunner.ValidateOutput(Path.Combine(tempArtifacts, name), name);
            }

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
            SqliteTestUtil.TryDeleteDirectory(tempArtifacts);
        }
    }

    private static string ResolveAppExe()
    {
        string baseDir = AppContext.BaseDirectory;
        string releaseExe = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "QuickNotes.App", "bin", "Release", "net8.0-windows10.0.19041.0", "QuickNotes.App.exe"));
        if (File.Exists(releaseExe))
        {
            return releaseExe;
        }

        string candidateExe = Path.GetFullPath(Path.Combine(baseDir, "QuickNotes.App.exe"));
        if (File.Exists(candidateExe))
        {
            return candidateExe;
        }

        var matches = Directory.GetFiles(Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..")), "QuickNotes.App.exe", SearchOption.AllDirectories);
        return matches.Length > 0 ? matches[0] : releaseExe;
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
