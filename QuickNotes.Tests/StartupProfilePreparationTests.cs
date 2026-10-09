using System;
using System.IO;
using System.Linq;
using QuickNotes.App.Composition;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public sealed class StartupProfilePreparationTests
{
    [Fact]
    public void MissingValidIsolatedProfile_CreatesDirectoryBeforeRecoveryProbe_AndInitializesUsableDb()
    {
        string profile = UniqueTempPath("missing_profile");
        Assert.False(Directory.Exists(profile));
        try
        {
            var isolated = Isolated(profile);
            var prepared = StartupProfilePreparation.Prepare(isolated);

            Assert.True(prepared.Succeeded, prepared.UserMessage);
            Assert.Equal(StartupProfilePreparationKind.Ready, prepared.Kind);
            Assert.True(Directory.Exists(prepared.ProfileDirectory));
            Assert.False(File.Exists(Path.Combine(prepared.ProfileDirectory, "quicknotes.db")));

            var backup = ApplicationCompositionRoot.CreateProfileBackupService(prepared.ProfileDirectory);
            var recovery = new DatabaseRecoveryService(backup.DbPath, backup);

            bool databaseFileExistedAndNonEmpty = File.Exists(backup.DbPath)
                && new FileInfo(backup.DbPath).Length > 0;
            Assert.False(databaseFileExistedAndNonEmpty);
            Assert.False(recovery.IsDatabaseCorrupt(out var corruptReason), corruptReason);
            Assert.False(DbInitializer.CanReusePriorIntegrityCheck(databaseFileExistedAndNonEmpty, probeReportedCorrupt: false));

            using (var init = SqliteTestUtil.CreateContext(backup.DbPath))
            {
                DbInitializer.Initialize(init);
            }

            Assert.True(File.Exists(backup.DbPath));
            using var db = SqliteTestUtil.CreateContext(backup.DbPath);
            db.Notes.Add(new Note
            {
                Title = "first-launch",
                Text = "usable",
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            });
            db.SaveChanges();
            Assert.Equal("usable", db.Notes.Single().Text);
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(profile);
        }
    }

    [Fact]
    public void UncreatableProfilePath_FailsClosed_WithoutCorruptionFlowOrDbArtifacts()
    {
        string root = UniqueTempPath("uncreatable");
        Directory.CreateDirectory(root);
        string blocker = Path.Combine(root, "not-a-directory");
        File.WriteAllText(blocker, "blocker");
        try
        {
            var prepared = StartupProfilePreparation.Prepare(Isolated(blocker));

            Assert.False(prepared.Succeeded);
            Assert.Equal(StartupProfilePreparationKind.DirectoryUnavailable, prepared.Kind);
            Assert.False(prepared.UserMessage.Contains("поврежд", StringComparison.OrdinalIgnoreCase));
            Assert.False(prepared.UserMessage.Contains("SQLite", StringComparison.OrdinalIgnoreCase));
            Assert.False(Directory.Exists(blocker));
            Assert.True(File.Exists(blocker));
            Assert.False(File.Exists(Path.Combine(blocker, "quicknotes.db")));
            Assert.False(File.Exists(blocker + ".db"));
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void IsolatedLiveProfileAndRelatives_FailClosed_WithoutCreatingDescendant()
    {
        string live = QuickNotesDbContext.LiveProfileDirectory;
        string descendant = Path.Combine(live, "qn_must_not_create_" + Guid.NewGuid().ToString("N"));
        string ancestor = Directory.GetParent(live)!.FullName;

        var liveResult = StartupProfilePreparation.Prepare(Isolated(live));
        Assert.False(liveResult.Succeeded);
        Assert.Equal(StartupProfilePreparationKind.InvalidIsolatedPath, liveResult.Kind);
        Assert.False(liveResult.UserMessage.Contains("поврежд", StringComparison.OrdinalIgnoreCase));

        var descendantResult = StartupProfilePreparation.Prepare(Isolated(descendant));
        Assert.False(descendantResult.Succeeded);
        Assert.Equal(StartupProfilePreparationKind.InvalidIsolatedPath, descendantResult.Kind);
        Assert.False(Directory.Exists(descendant));

        var ancestorResult = StartupProfilePreparation.Prepare(Isolated(ancestor));
        Assert.False(ancestorResult.Succeeded);
        Assert.Equal(StartupProfilePreparationKind.InvalidIsolatedPath, ancestorResult.Kind);
    }

    [Fact]
    public void EmptyIsolatedPath_IsRejectedWithoutDirectoryCreate()
    {
        var blank = StartupProfilePreparation.EnsureDirectoryExists("   ");
        Assert.False(blank.Succeeded);
        Assert.Equal(StartupProfilePreparationKind.DirectoryUnavailable, blank.Kind);
        Assert.False(blank.UserMessage.Contains("поврежд", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExistingCorruptDatabase_StillDetectedAfterSuccessfulPrepare()
    {
        string profile = UniqueTempPath("corrupt_after_prepare");
        Directory.CreateDirectory(profile);
        string dbPath = Path.Combine(profile, "quicknotes.db");
        string backupsDir = Path.Combine(profile, "Backups");
        Directory.CreateDirectory(backupsDir);
        try
        {
            using (var db = SqliteTestUtil.CreateContext(dbPath))
            {
                DbInitializer.Initialize(db);
                db.Notes.Add(new Note
                {
                    Text = "Pre-corruption note",
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                });
                db.SaveChanges();
            }

            var backupService = new BackupService(dbPath, backupsDir);
            string backupPath = backupService.CreateBackup();
            Assert.True(File.Exists(backupPath));

            SqliteTestUtil.ReleasePools();
            File.WriteAllBytes(dbPath, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04 });

            var prepared = StartupProfilePreparation.Prepare(Isolated(profile));
            Assert.True(prepared.Succeeded, prepared.UserMessage);

            var recovery = new DatabaseRecoveryService(dbPath, backupService);
            Assert.True(recovery.IsDatabaseCorrupt(out var corruptReason));
            Assert.False(string.IsNullOrWhiteSpace(corruptReason));

            var healthy = recovery.FindLatestHealthyBackup(out _);
            Assert.NotNull(healthy);
            var result = recovery.PerformRecovery(healthy!.FullName);
            Assert.True(result.Success, result.ErrorMessage);

            using var restored = SqliteTestUtil.CreateContext(dbPath);
            Assert.Equal("Pre-corruption note", restored.Notes.Single().Text);
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(profile);
        }
    }

    [Fact]
    public void AppStartup_PreparesProfileDirectory_BeforeRecoveryProbe()
    {
        string root = FindRepoRoot();
        string app = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "App.xaml.cs"));

        Assert.Contains("StartupProfilePreparation.Prepare", app, StringComparison.Ordinal);
        int prepare = app.IndexOf("StartupProfilePreparation.Prepare", StringComparison.Ordinal);
        int backup = app.IndexOf("CreateProfileBackupService", StringComparison.Ordinal);
        int recovery = app.IndexOf("IsDatabaseCorrupt", StringComparison.Ordinal);
        int init = app.IndexOf("DbInitializer.Initialize", StringComparison.Ordinal);
        Assert.True(prepare >= 0 && backup > prepare && recovery > backup && init > recovery);

        Assert.Contains("preparedProfile.Succeeded", app, StringComparison.Ordinal);
        Assert.DoesNotContain("IsDatabaseCorrupt", app[(prepare)..backup], StringComparison.Ordinal);
    }

    private static IsolatedProfileAppOptions Isolated(string profile) => new()
    {
        IsIsolated = true,
        ProfileDirectory = profile
    };

    private static string UniqueTempPath(string tag)
        => Path.Combine(Path.GetTempPath(), "qn_startup_prep_" + tag + "_" + Guid.NewGuid().ToString("N"));

    private static string FindRepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            if (File.Exists(Path.Combine(dir, "QuickNotes.sln")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)!.FullName;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
