using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using QuickNotes.App.Data;
using QuickNotes.App.Services;
using Xunit;

namespace QuickNotes.Tests;

[Collection(LiveProfileSnapshotCollection.Name)]
[TestCategory(TestCategories.UiSensitive)]
public sealed class ManualAcceptancePrepTests
{
    [Fact]
    public void Parse_RecognizesManualAcceptancePrepSwitch()
    {
        string profile = Path.Combine(Path.GetTempPath(), "qn_manual_cli_" + Guid.NewGuid().ToString("N"));
        var parsed = IsolatedProfileAppCli.Parse(new[]
        {
            "--isolated-profile", profile,
            "--manual-acceptance-prep",
            "--output-dir", Path.Combine(Path.GetTempPath(), "qn_manual_out")
        });
        Assert.True(parsed.IsIsolated);
        Assert.True(parsed.ManualAcceptancePrep);
        Assert.False(parsed.UxC14Smoke);
        Assert.False(parsed.UxPackageDLayoutSmoke);
    }

    [Fact]
    public void Run_WritesManifestAndLog_WithoutTouchingLiveProfile()
    {
        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);
        string tempProfile = Path.Combine(Path.GetTempPath(), "qn_manual_prep_" + Guid.NewGuid().ToString("N"));
        string outputDir = Path.Combine(Path.GetTempPath(), "qn_manual_prep_art_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempProfile);
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
                    ManualAcceptancePrepRunner.Run(window, outputDir);
                    window.Close();
                }
                finally
                {
                    QuickNotesDbContext.ProfileDirectoryOverride = previous;
                }
            }, TimeSpan.FromSeconds(60));

            string manifestPath = Path.Combine(outputDir, ManualAcceptancePrepRunner.ManifestFileName);
            string logPath = Path.Combine(outputDir, ManualAcceptancePrepRunner.LogFileName);
            Assert.True(File.Exists(manifestPath), manifestPath);
            Assert.True(File.Exists(logPath), logPath);

            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            Assert.Contains(tempProfile, doc.RootElement.GetProperty("profileDirectory").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains(UxC14UiSmokeRunner.NoteTitle, File.ReadAllText(logPath), StringComparison.Ordinal);
            Assert.Contains(ManualAcceptancePrepRunner.InboxTitle, File.ReadAllText(logPath), StringComparison.Ordinal);

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(tempProfile);
            SqliteTestUtil.TryDeleteDirectory(outputDir);
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
