using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using QuickNotes.App.Data;
using QuickNotes.App.Helpers;
using QuickNotes.App.Services;
using Xunit;

namespace QuickNotes.Tests;

[Collection(LiveProfileSnapshotCollection.Name)]
[TestCategory(TestCategories.UiSensitive)]
public sealed class UxC13SmokeTests
{
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    [Fact]
    public void Parse_RecognizesUxC13SmokeSwitch()
    {
        string profile = Path.Combine(Path.GetTempPath(), "qn_uxc13_cli_" + Guid.NewGuid().ToString("N"));
        var parsed = IsolatedProfileAppCli.Parse(new[]
        {
            "--isolated-profile", profile,
            "--ux-c13-smoke",
            "--output-dir", Path.Combine(Path.GetTempPath(), "qn_uxc13_out")
        });
        Assert.True(parsed.IsIsolated);
        Assert.True(parsed.UxC13Smoke);
        Assert.False(parsed.UxC12Smoke);
        Assert.False(parsed.UxC14Smoke);
        Assert.False(parsed.UxC11Smoke);
        Assert.False(parsed.UxPackageAbSmoke);
    }

    [Fact]
    public void GenerateAcceptanceScreenshots_CloudAndTransfer_FirstLevel()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);
        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_uxc13_screens_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);
        string outputDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "artifacts", "ux-c13-acceptance"));
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
                    UxC13UiSmokeRunner.Run(window, outputDir);
                    window.Close();
                }
                finally
                {
                    QuickNotesDbContext.ProfileDirectoryOverride = previous;
                }
            }, TimeSpan.FromSeconds(120));

            Assert.Equal(UxC13UiSmokeRunner.ExpectedFileNames.Length, UxC13UiSmokeRunner.ExpectedFileNames.Distinct(StringComparer.Ordinal).Count());
            foreach (var name in UxC13UiSmokeRunner.ExpectedFileNames)
            {
                string path = Path.Combine(outputDir, name);
                Assert.True(File.Exists(path), path);
                byte[] bytes = File.ReadAllBytes(path);
                Assert.True(bytes.Take(PngSignature.Length).SequenceEqual(PngSignature), path);
                UxC13UiSmokeRunner.ValidateOutput(path, name);

                string dump = File.ReadAllText(UxC13UiSmokeRunner.VisibleTextDumpPath(path));
                Assert.False(string.IsNullOrWhiteSpace(dump), name);
                if (name.StartsWith("cloud-first", StringComparison.Ordinal))
                {
                    Assert.Contains(UserTaskCopy.CloudSectionTitle, dump, StringComparison.Ordinal);
                    Assert.Contains(UserTaskCopy.CloudWizardButton, dump, StringComparison.Ordinal);
                    Assert.Contains(UserTaskCopy.CloudCryptoWarning, dump, StringComparison.Ordinal);
                    Assert.DoesNotContain("Access Key ID", dump, StringComparison.Ordinal);
                }
                else if (name.StartsWith("cloud-advanced", StringComparison.Ordinal))
                {
                    Assert.Contains("Access Key ID", dump, StringComparison.Ordinal);
                    Assert.Contains(UserTaskCopy.CloudCryptoWarning, dump, StringComparison.Ordinal);
                }
                else if (name.StartsWith("transfer-export", StringComparison.Ordinal))
                {
                    Assert.Contains(UserTaskCopy.ExportTab, dump, StringComparison.Ordinal);
                    Assert.Contains(UserTaskCopy.TransferWindowTitle, dump, StringComparison.Ordinal);
                }
                else if (name.StartsWith("transfer-archive", StringComparison.Ordinal))
                {
                    Assert.Contains(UserTaskCopy.ArchiveTab, dump, StringComparison.Ordinal);
                    Assert.Contains("Recovery key", dump, StringComparison.Ordinal);
                }
            }

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void RunIsolatedUiSmokeProcess_GeneratesValidCloudTransferScreenshots_AndExitsZero()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);
        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_uxc13_proc_profile_" + Guid.NewGuid().ToString("N"));
        string tempArtifacts = Path.Combine(Path.GetTempPath(), "qn_uxc13_proc_art_" + Guid.NewGuid().ToString("N"));
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
                Arguments = $"--isolated-profile \"{tempProfile}\" --ux-c13-smoke --output-dir \"{tempArtifacts}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            var process = ProcessTestHarness.Run(psi, TimeSpan.FromMilliseconds(120000));
            string stdout = process.StandardOutput;
            string stderr = process.StandardError;
            Assert.True(process.ExitCode == 0, stdout + stderr);
            Assert.Contains(UxC13UiSmokeRunner.SuccessMarker, stdout);

            foreach (var name in UxC13UiSmokeRunner.ExpectedFileNames)
            {
                string path = Path.Combine(tempArtifacts, name);
                Assert.True(File.Exists(path), path);
                UxC13UiSmokeRunner.ValidateOutput(path, name);
            }

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
