using System;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public sealed class ColdStartInitializationTests
{
    [Fact]
    public void CurrentSchema_SecondInitialize_SkipsUpgradeAndFtsRebuild_KeepsFts()
    {
        string dbPath = NewDbPath("current_skip");
        try
        {
            using (var db = new QuickNotesDbContext(dbPath))
            {
                DbInitializer.Initialize(db);
            }

            using (var db = new QuickNotesDbContext(dbPath))
            {
                var secondEmpty = DbInitializer.Initialize(db, backupBeforeUpgrade: () => Assert.Fail("empty current schema must not request backup"));
                Assert.True(secondEmpty.SkippedSchemaUpgradePass);
                Assert.True(secondEmpty.SkippedFtsRebuild);
            }

            using (var db = new QuickNotesDbContext(dbPath))
            {
                db.Notes.Add(Unprotected("Ready note", "plaintext body for fts"));
                db.SaveChanges();
            }

            long rowidBefore;
            using (var conn = Open(dbPath))
            {
                rowidBefore = ScalarLong(conn, "SELECT rowid FROM NotesFts WHERE Title = 'Ready note';");
            }

            using (var db = new QuickNotesDbContext(dbPath))
            {
                var outcome = DbInitializer.Initialize(db, backupBeforeUpgrade: () => Assert.Fail("current schema must not request backup"));
                Assert.True(outcome.SkippedSchemaUpgradePass);
                Assert.True(outcome.SkippedFtsRebuild);
                Assert.True(outcome.SkippedEnsureCreated);
                Assert.False(outcome.SkippedIntegrityCheck);
            }

            using var after = Open(dbPath);
            Assert.Equal(rowidBefore, ScalarLong(after, "SELECT rowid FROM NotesFts WHERE Title = 'Ready note';"));
            AssertFtsExactlyMatchesUnprotectedNotes(after);
            Assert.Equal(3L, ScalarLong(after,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name IN ('Notes_ai','Notes_ad','Notes_au');"));
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Fact]
    public void IntegrityAlreadyVerified_SkipsSecondPragma_ButStillInitializes()
    {
        string dbPath = NewDbPath("integrity_skip");
        try
        {
            using (var db = new QuickNotesDbContext(dbPath))
            {
                var first = DbInitializer.Initialize(db, integrityAlreadyVerified: true);
                Assert.True(first.SkippedIntegrityCheck);
            }

            using (var db = new QuickNotesDbContext(dbPath))
            {
                var second = DbInitializer.Initialize(db, integrityAlreadyVerified: true);
                Assert.True(second.SkippedIntegrityCheck);
                Assert.True(second.SkippedSchemaUpgradePass);
                Assert.True(second.SkippedFtsRebuild);
                Assert.True(second.SkippedEnsureCreated);
            }
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Fact]
    public void MissingFtsRows_AreBackfilled_AndProtectedNotesStayOut()
    {
        string dbPath = NewDbPath("fts_backfill");
        try
        {
            using (var db = new QuickNotesDbContext(dbPath))
            {
                DbInitializer.Initialize(db);
                db.Notes.Add(Unprotected("Open", "visible fts text"));
                db.Notes.Add(Protected());
                db.SaveChanges();
                db.Database.ExecuteSqlRaw("DELETE FROM NotesFts;");
                var outcome = DbInitializer.Initialize(db);
                Assert.False(outcome.SkippedFtsRebuild);
            }

            using var check = Open(dbPath);
            AssertFtsExactlyMatchesUnprotectedNotes(check);
            Assert.Equal(1L, ScalarLong(check, "SELECT COUNT(*) FROM NotesFts WHERE Title = 'Open';"));
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Fact]
    public void EqualCounts_WrongNoteId_RebuildsExactContent()
    {
        string dbPath = NewDbPath("fts_wrong_id");
        try
        {
            int idA;
            int idB;
            using (var db = new QuickNotesDbContext(dbPath))
            {
                DbInitializer.Initialize(db);
                db.Notes.Add(Unprotected("Alpha", "body-a"));
                db.Notes.Add(Unprotected("Beta", "body-b"));
                db.SaveChanges();
                idA = db.Notes.Single(n => n.Title == "Alpha").Id;
                idB = db.Notes.Single(n => n.Title == "Beta").Id;
                db.Database.ExecuteSqlRaw(
                    "UPDATE NotesFts SET NoteId = CASE NoteId WHEN {0} THEN {1} WHEN {1} THEN {0} END WHERE NoteId IN ({0}, {1});",
                    idA, idB);
                var outcome = DbInitializer.Initialize(db);
                Assert.False(outcome.SkippedFtsRebuild);
            }

            using var check = Open(dbPath);
            AssertFtsExactlyMatchesUnprotectedNotes(check);
            Assert.Equal("Alpha", ScalarString(check, $"SELECT Title FROM NotesFts WHERE NoteId = {idA};"));
            Assert.Equal("Beta", ScalarString(check, $"SELECT Title FROM NotesFts WHERE NoteId = {idB};"));
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Fact]
    public void SameNoteId_StaleTitle_Rebuilds()
    {
        string dbPath = NewDbPath("fts_stale_title");
        try
        {
            using (var db = new QuickNotesDbContext(dbPath))
            {
                SeedTwoNotesAndInitialize(db);
                db.Database.ExecuteSqlRaw("UPDATE NotesFts SET Title = 'stale-title' WHERE Title = 'Alpha';");
                Assert.False(DbInitializer.Initialize(db).SkippedFtsRebuild);
            }

            using var check = Open(dbPath);
            AssertFtsExactlyMatchesUnprotectedNotes(check);
            Assert.Equal(0L, ScalarLong(check, "SELECT COUNT(*) FROM NotesFts WHERE Title = 'stale-title';"));
            Assert.Equal(1L, ScalarLong(check, "SELECT COUNT(*) FROM NotesFts WHERE Title = 'Alpha';"));
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Fact]
    public void SameNoteId_StaleText_Rebuilds()
    {
        string dbPath = NewDbPath("fts_stale_text");
        try
        {
            using (var db = new QuickNotesDbContext(dbPath))
            {
                SeedTwoNotesAndInitialize(db);
                db.Database.ExecuteSqlRaw("UPDATE NotesFts SET Text = 'stale-text' WHERE Title = 'Alpha';");
                Assert.False(DbInitializer.Initialize(db).SkippedFtsRebuild);
            }

            using var check = Open(dbPath);
            AssertFtsExactlyMatchesUnprotectedNotes(check);
            Assert.Equal(0L, ScalarLong(check, "SELECT COUNT(*) FROM NotesFts WHERE Text = 'stale-text';"));
            Assert.Equal(1L, ScalarLong(check, "SELECT COUNT(*) FROM NotesFts WHERE Text = 'body-a';"));
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Fact]
    public void ExtraFtsRow_BalancedByMissingNoteId_Rebuilds()
    {
        string dbPath = NewDbPath("fts_balanced_extra");
        try
        {
            int realId;
            using (var db = new QuickNotesDbContext(dbPath))
            {
                SeedTwoNotesAndInitialize(db);
                realId = db.Notes.Single(n => n.Title == "Alpha").Id;
                db.Database.ExecuteSqlRaw("DELETE FROM NotesFts WHERE NoteId = {0};", realId);
                db.Database.ExecuteSqlRaw(
                    "INSERT INTO NotesFts(NoteId, Title, Text) VALUES ({0}, 'Alpha', 'body-a');",
                    99999);
                Assert.False(DbInitializer.Initialize(db).SkippedFtsRebuild);
            }

            using var check = Open(dbPath);
            AssertFtsExactlyMatchesUnprotectedNotes(check);
            Assert.Equal(0L, ScalarLong(check, "SELECT COUNT(*) FROM NotesFts WHERE NoteId = 99999;"));
            Assert.Equal(1L, ScalarLong(check, $"SELECT COUNT(*) FROM NotesFts WHERE NoteId = {realId} AND Title = 'Alpha';"));
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Fact]
    public void ProtectedLeak_IsRemovedOnRebuild()
    {
        string dbPath = NewDbPath("fts_protected_leak");
        try
        {
            int protectedId;
            using (var db = new QuickNotesDbContext(dbPath))
            {
                DbInitializer.Initialize(db);
                db.Notes.Add(Unprotected("Open", "visible"));
                db.Notes.Add(Protected());
                db.SaveChanges();
                protectedId = db.Notes.Single(n => n.IsProtected).Id;
                db.Database.ExecuteSqlRaw(
                    "INSERT INTO NotesFts(NoteId, Title, Text) VALUES ({0}, 'leaked', 'secret');",
                    protectedId);
                Assert.False(DbInitializer.Initialize(db).SkippedFtsRebuild);
            }

            using var check = Open(dbPath);
            AssertFtsExactlyMatchesUnprotectedNotes(check);
            Assert.Equal(0L, ScalarLong(check, $"SELECT COUNT(*) FROM NotesFts WHERE NoteId = {protectedId};"));
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Fact]
    public void DroppedFtsTriggers_AreRecreated()
    {
        string dbPath = NewDbPath("fts_triggers");
        try
        {
            using (var db = new QuickNotesDbContext(dbPath))
            {
                DbInitializer.Initialize(db);
                db.Database.ExecuteSqlRaw("DROP TRIGGER IF EXISTS Notes_ai;");
                Assert.False(DbInitializer.Initialize(db).SkippedFtsRebuild);
            }

            using var check = Open(dbPath);
            Assert.Equal(3L, ScalarLong(check,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name IN ('Notes_ai','Notes_ad','Notes_au');"));
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Fact]
    public void FullyCurrentIndex_SkipsRebuild_PreservingFtsRowids()
    {
        string dbPath = NewDbPath("fts_current_skip");
        try
        {
            using (var db = new QuickNotesDbContext(dbPath))
            {
                SeedTwoNotesAndInitialize(db);
            }

            string fingerprintBefore;
            using (var conn = Open(dbPath))
            {
                fingerprintBefore = FtsRowidFingerprint(conn);
            }

            using (var db = new QuickNotesDbContext(dbPath))
            {
                Assert.True(DbInitializer.Initialize(db).SkippedFtsRebuild);
            }

            using var after = Open(dbPath);
            Assert.Equal(fingerprintBefore, FtsRowidFingerprint(after));
            AssertFtsExactlyMatchesUnprotectedNotes(after);
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Fact]
    public void CorruptDatabase_StillThrows_WhenIntegrityIsNotPreVerified()
    {
        string dbPath = NewDbPath("corrupt");
        try
        {
            File.WriteAllBytes(dbPath, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01 });

            using var db = new QuickNotesDbContext(dbPath);
            var ex = Assert.ThrowsAny<Exception>(() => DbInitializer.Initialize(db));
            Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Fact]
    public void CanReusePriorIntegrityCheck_OnlyWhenExistingHealthyFileWasJustProbed()
    {
        Assert.True(DbInitializer.CanReusePriorIntegrityCheck(databaseFileExistedAndNonEmpty: true, probeReportedCorrupt: false));
        Assert.False(DbInitializer.CanReusePriorIntegrityCheck(databaseFileExistedAndNonEmpty: false, probeReportedCorrupt: false));
        Assert.False(DbInitializer.CanReusePriorIntegrityCheck(databaseFileExistedAndNonEmpty: true, probeReportedCorrupt: true));
        Assert.False(DbInitializer.CanReusePriorIntegrityCheck(databaseFileExistedAndNonEmpty: false, probeReportedCorrupt: true));
    }

    [Fact]
    public void AppStartup_ReusesRecoveryIntegrityCheck_OnlyForHealthyExistingFile_AndDoesNotMoveReadySignal()
    {
        string root = FindRepoRoot();
        string app = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "App.xaml.cs"));
        string cli = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "Services", "IsolatedProfileAppCli.cs"));
        string csproj = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "QuickNotes.App.csproj"));
        string initializer = File.ReadAllText(Path.Combine(root, "QuickNotes.App", "Data", "DbInitializer.cs"));

        Assert.Contains("StartupProfilePreparation.Prepare", app, StringComparison.Ordinal);
        Assert.Contains("IsDatabaseCorrupt", app, StringComparison.Ordinal);
        Assert.Contains("CanReusePriorIntegrityCheck", app, StringComparison.Ordinal);
        Assert.Contains("integrityAlreadyVerified: reuseRecoveryIntegrity", app, StringComparison.Ordinal);
        Assert.DoesNotContain("integrityAlreadyVerified: true", app, StringComparison.Ordinal);
        Assert.DoesNotContain("LastSkippedFtsRebuild", initializer, StringComparison.Ordinal);
        Assert.DoesNotContain("LastSkippedEnsureCreated", initializer, StringComparison.Ordinal);
        Assert.Contains("EXCEPT", initializer, StringComparison.Ordinal);
        Assert.Contains("coalesce(Title, '')", initializer, StringComparison.Ordinal);

        int prepare = app.IndexOf("StartupProfilePreparation.Prepare", StringComparison.Ordinal);
        int fileExisted = app.IndexOf("databaseFileExistedAndNonEmpty", StringComparison.Ordinal);
        int recovery = app.IndexOf("IsDatabaseCorrupt", StringComparison.Ordinal);
        int reuse = app.IndexOf("CanReusePriorIntegrityCheck", StringComparison.Ordinal);
        int init = app.IndexOf("integrityAlreadyVerified: reuseRecoveryIntegrity", StringComparison.Ordinal);
        Assert.True(prepare >= 0 && fileExisted > prepare && recovery > fileExisted && reuse > recovery && init > reuse);

        Assert.Contains("isolatedOptions.PerfStartup", app, StringComparison.Ordinal);
        Assert.Contains("BeginStartupPhases", app, StringComparison.Ordinal);

        int focus = cli.IndexOf("window.SearchBox.Focus()", StringComparison.Ordinal);
        int ready = cli.IndexOf("QN_PERF_STARTUP_READY_MS:", StringComparison.Ordinal);
        Assert.True(focus >= 0 && ready > focus);
        Assert.Contains("DispatcherPriority.Input", cli, StringComparison.Ordinal);

        var parsed = IsolatedProfileAppCli.Parse(new[]
        {
            "--isolated-profile",
            Path.Combine(Path.GetTempPath(), "qn_perf_cli_" + Guid.NewGuid().ToString("N")),
            "--perf-startup"
        });
        Assert.True(parsed.IsIsolated);
        Assert.True(parsed.PerfStartup);

        Assert.DoesNotContain("ApplyReadyToRunToBuildOutput", csproj, StringComparison.Ordinal);
        Assert.DoesNotContain("PublishReadyToRun", csproj, StringComparison.Ordinal);
        Assert.DoesNotContain("<ReadyToRun>", csproj, StringComparison.Ordinal);
    }

    private static void SeedTwoNotesAndInitialize(QuickNotesDbContext db)
    {
        DbInitializer.Initialize(db);
        db.Notes.Add(Unprotected("Alpha", "body-a"));
        db.Notes.Add(Unprotected("Beta", "body-b"));
        db.SaveChanges();
    }

    private static Note Unprotected(string title, string text) => new()
    {
        Title = title,
        Text = text,
        CreatedAt = DateTime.Now,
        UpdatedAt = DateTime.Now,
        ProtectedSaltBase64 = string.Empty,
        ProtectedNonceBase64 = string.Empty,
        ProtectedTagBase64 = string.Empty,
        ProtectedCiphertextBase64 = string.Empty
    };

    private static Note Protected() => new()
    {
        Title = string.Empty,
        Text = string.Empty,
        IsProtected = true,
        ProtectedCiphertextBase64 = "Yw==",
        ProtectedSaltBase64 = string.Empty,
        ProtectedNonceBase64 = string.Empty,
        ProtectedTagBase64 = string.Empty,
        CreatedAt = DateTime.Now,
        UpdatedAt = DateTime.Now
    };

    private static void AssertFtsExactlyMatchesUnprotectedNotes(SqliteConnection conn)
    {
        Assert.Equal(0L, ScalarLong(conn, @"
SELECT EXISTS (
  SELECT 1 FROM (
    SELECT NoteId, Title, Text FROM NotesFts
    EXCEPT
    SELECT Id, coalesce(Title, ''), Text FROM Notes WHERE IsProtected = 0
  )
);"));
        Assert.Equal(0L, ScalarLong(conn, @"
SELECT EXISTS (
  SELECT 1 FROM (
    SELECT Id, coalesce(Title, ''), Text FROM Notes WHERE IsProtected = 0
    EXCEPT
    SELECT NoteId, Title, Text FROM NotesFts
  )
);"));
        Assert.Equal(0L, ScalarLong(conn, "SELECT EXISTS (SELECT 1 FROM NotesFts GROUP BY NoteId HAVING COUNT(*) > 1);"));
        Assert.Equal(0L, ScalarLong(conn, @"
SELECT EXISTS (
  SELECT 1 FROM NotesFts f INNER JOIN Notes n ON n.Id = f.NoteId WHERE n.IsProtected = 1
);"));
    }

    private static string FtsRowidFingerprint(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT group_concat(rowid || ':' || NoteId, ',') FROM (SELECT rowid, NoteId FROM NotesFts ORDER BY NoteId, rowid);";
        return cmd.ExecuteScalar()?.ToString() ?? string.Empty;
    }

    private static SqliteConnection Open(string dbPath)
    {
        var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        conn.Open();
        return conn;
    }

    private static long ScalarLong(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0);
    }

    private static string ScalarString(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString() ?? string.Empty;
    }

    private static string NewDbPath(string tag)
    {
        string dir = Path.Combine(Path.GetTempPath(), "qn_coldstart_" + tag + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "quicknotes.db");
    }

    private static void Cleanup(string dbPath)
    {
        string? dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
        {
            SqliteTestUtil.TryDeleteDirectory(dir);
        }
    }

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
