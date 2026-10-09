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
public sealed class UxC11SmokeTests
{
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    [Fact]
    public void Parse_RecognizesUxC11SmokeSwitch()
    {
        string profile = Path.Combine(Path.GetTempPath(), "qn_uxc11_cli_" + Guid.NewGuid().ToString("N"));
        var parsed = IsolatedProfileAppCli.Parse(new[]
        {
            "--isolated-profile", profile,
            "--ux-c11-smoke",
            "--output-dir", Path.Combine(Path.GetTempPath(), "qn_uxc11_out")
        });
        Assert.True(parsed.IsIsolated);
        Assert.True(parsed.UxC11Smoke);
        Assert.False(parsed.UxC08Smoke);
        Assert.False(parsed.UxPackageAbSmoke);
        Assert.False(parsed.ThreePaneSmoke);
    }

    [Fact]
    public void GenerateAcceptanceScreenshots_HumanStatus_LightDark()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);
        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_uxc11_screens_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);
        string outputDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "artifacts", "ux-c11-acceptance"));
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
                    UxC11UiSmokeRunner.Run(window, outputDir);
                    window.Close();
                }
                finally
                {
                    QuickNotesDbContext.ProfileDirectoryOverride = previous;
                }
            }, TimeSpan.FromSeconds(120));

            Assert.Equal(UxC11UiSmokeRunner.ExpectedFileNames.Length, UxC11UiSmokeRunner.ExpectedFileNames.Distinct(StringComparer.Ordinal).Count());
            byte[]? previousBytes = null;
            foreach (var name in UxC11UiSmokeRunner.ExpectedFileNames)
            {
                string path = Path.Combine(outputDir, name);
                Assert.True(File.Exists(path), path);
                byte[] bytes = File.ReadAllBytes(path);
                Assert.True(bytes.Length >= PngSignature.Length, path);
                Assert.True(bytes.Take(PngSignature.Length).SequenceEqual(PngSignature), path);
                UxC11UiSmokeRunner.ValidateOutput(path, name);

                string dump = File.ReadAllText(UxC11UiSmokeRunner.VisibleTextDumpPath(path));
                Assert.False(string.IsNullOrWhiteSpace(dump), name);
                if (name.Contains("local", StringComparison.Ordinal))
                {
                    Assert.Contains(PublicationStatusCopy.SavedOnThisPc, dump, StringComparison.Ordinal);
                    Assert.Contains(UxC11UiSmokeRunner.LocalTitle, dump, StringComparison.Ordinal);
                }
                else if (name.Contains("pending", StringComparison.Ordinal))
                {
                    Assert.Contains(PublicationStatusCopy.WaitingToSend, dump, StringComparison.Ordinal);
                    Assert.Contains(UxC11UiSmokeRunner.PendingTitle, dump, StringComparison.Ordinal);
                }
                else if (name.Contains("cloud", StringComparison.Ordinal))
                {
                    Assert.Contains(PublicationStatusCopy.InCloud, dump, StringComparison.Ordinal);
                    Assert.Contains(UxC11UiSmokeRunner.CloudTitle, dump, StringComparison.Ordinal);
                }
                else if (name.Contains("conflict", StringComparison.Ordinal))
                {
                    Assert.Contains(PublicationStatusCopy.Conflict, dump, StringComparison.Ordinal);
                    Assert.Contains(UxC11UiSmokeRunner.ConflictTitle, dump, StringComparison.Ordinal);
                }
                else if (name.Contains("error", StringComparison.Ordinal))
                {
                    Assert.Contains(PublicationStatusCopy.Error, dump, StringComparison.Ordinal);
                    Assert.Contains(UxC11UiSmokeRunner.ErrorTitle, dump, StringComparison.Ordinal);
                }

                Assert.DoesNotContain("Облако: Отключено", dump, StringComparison.Ordinal);
                Assert.DoesNotContain("Облако: Синхронизировано", dump, StringComparison.Ordinal);
                Assert.DoesNotContain("Сохранено локально", dump, StringComparison.Ordinal);
                if (previousBytes != null)
                {
                    Assert.False(previousBytes.SequenceEqual(bytes), "adjacent status screenshots must differ: " + name);
                }

                previousBytes = bytes;
            }

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
        }
    }

    [Fact]
    public void RunIsolatedUiSmokeProcess_GeneratesValidStatusScreenshots_AndExitsZero()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);
        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_uxc11_proc_profile_" + Guid.NewGuid().ToString("N"));
        string tempArtifacts = Path.Combine(Path.GetTempPath(), "qn_uxc11_proc_art_" + Guid.NewGuid().ToString("N"));
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
                Arguments = $"--isolated-profile \"{tempProfile}\" --ux-c11-smoke --output-dir \"{tempArtifacts}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            Assert.NotNull(process);
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            bool exited = process.WaitForExit(120000);
            Assert.True(exited, "smoke process hung");
            Assert.True(process.ExitCode == 0, stdout + stderr);
            Assert.Contains(UxC11UiSmokeRunner.SuccessMarker, stdout);

            foreach (var name in UxC11UiSmokeRunner.ExpectedFileNames)
            {
                string path = Path.Combine(tempArtifacts, name);
                Assert.True(File.Exists(path), path);
                byte[] bytes = File.ReadAllBytes(path);
                Assert.True(bytes.Take(PngSignature.Length).SequenceEqual(PngSignature), path);
                UxC11UiSmokeRunner.ValidateOutput(path, name);
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
