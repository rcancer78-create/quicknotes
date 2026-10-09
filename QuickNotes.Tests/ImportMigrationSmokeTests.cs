using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using QuickNotes.App.Data;
using QuickNotes.App.Services;
using Xunit;

namespace QuickNotes.Tests;

[Collection(LiveProfileSnapshotCollection.Name)]
[TestCategory(TestCategories.UiSensitive)]
public sealed class ImportMigrationSmokeTests
{
    [Fact]
    public void GenerateAcceptanceScreenshots_MappingDiagnosticsDuplicatesSummary_LightDark_WideNarrow()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);
        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_imp_screens_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);
        string outputDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "artifacts", "import-migration-acceptance"));
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
                    ImportMigrationUiSmokeRunner.Run(outputDir);
                }
                finally
                {
                    QuickNotesDbContext.ProfileDirectoryOverride = previous;
                }
            }, TimeSpan.FromSeconds(90));

            string[] expected =
            {
                "mapping-light-wide.png", "mapping-dark-wide.png", "mapping-light-narrow.png", "mapping-dark-narrow.png",
                "diagnostics-light-wide.png", "diagnostics-dark-wide.png", "diagnostics-light-narrow.png", "diagnostics-dark-narrow.png",
                "duplicates-light-wide.png", "duplicates-dark-wide.png", "duplicates-light-narrow.png", "duplicates-dark-narrow.png",
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
                byte[] mapping = File.ReadAllBytes(Path.Combine(outputDir, "mapping-" + themeSize + ".png"));
                byte[] diagnostics = File.ReadAllBytes(Path.Combine(outputDir, "diagnostics-" + themeSize + ".png"));
                byte[] duplicates = File.ReadAllBytes(Path.Combine(outputDir, "duplicates-" + themeSize + ".png"));
                byte[] summary = File.ReadAllBytes(Path.Combine(outputDir, "summary-" + themeSize + ".png"));
                Assert.False(mapping.SequenceEqual(diagnostics), "mapping and diagnostics shots must differ for " + themeSize);
                Assert.False(diagnostics.SequenceEqual(duplicates), "diagnostics and duplicates shots must differ for " + themeSize);
                Assert.False(duplicates.SequenceEqual(summary), "duplicates and summary shots must differ for " + themeSize);
                Assert.False(mapping.SequenceEqual(summary), "mapping and summary shots must differ for " + themeSize);
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
        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_imp_proc_profile_" + Guid.NewGuid().ToString("N"));
        string tempArtifacts = Path.Combine(Path.GetTempPath(), "qn_imp_proc_art_" + Guid.NewGuid().ToString("N"));
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
                Arguments = $"--isolated-profile \"{tempProfile}\" --import-migration-smoke --output-dir \"{tempArtifacts}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            var process = ProcessTestHarness.Run(psi, TimeSpan.FromMilliseconds(60000));
            string stdout = process.StandardOutput;
            string stderr = process.StandardError;
            Assert.True(process.ExitCode == 0, stdout + stderr);
            Assert.Contains("QN_IMPORT_MIGRATION_SMOKE_SUCCESS", stdout);
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
