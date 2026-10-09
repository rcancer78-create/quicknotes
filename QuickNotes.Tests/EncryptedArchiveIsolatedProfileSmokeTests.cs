using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using QuickNotes.App.Data;
using QuickNotes.App.Models;
using QuickNotes.App.Services;
using QuickNotes.App.Services.EncryptedArchive;
using Xunit;

namespace QuickNotes.Tests;

/// <summary>
/// Process-level restore into a redirected LocalAppData profile. This is not a separate Windows user (SID/DPAPI).
/// </summary>
[Collection(LiveProfileSnapshotCollection.Name)]
[TestCategory(TestCategories.UiSensitive)]
public sealed class EncryptedArchiveIsolatedProfileSmokeTests
{
    [Fact]
    public void ArS21_IsolatedProfileProcess_RestoresWithRecoveryKey_WithoutTouchingLiveProfileOrDpapi()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "QuickNotes.App.exe");
        Assert.True(File.Exists(exe), "QuickNotes.App.exe should be copied next to testhost.");

        string liveRoot = QuickNotesDbContext.LiveProfileDirectory;
        string[] liveBefore = SnapshotLiveProfile(liveRoot);

        string root = Path.Combine(Path.GetTempPath(), "qn_isolated_rk_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string token = "ISOLATED_PROFILE_SMOKE_NOTE_7c91e2a4";
            string dataDir = Path.Combine(root, "source-data");
            Directory.CreateDirectory(dataDir);
            string dbPath = Path.Combine(dataDir, "quicknotes.db");
            var storage = new AttachmentStorageService(dataDir);
            using (var db = SqliteTestUtil.CreateContext(dbPath))
            {
                DbInitializer.Initialize(db);
                db.Notes.Add(new Note
                {
                    Text = token,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                });
                db.SaveChanges();
            }

            byte[] attachment = Encoding.UTF8.GetBytes("isolated-att");
            var saved = storage.SaveFromBytes(attachment, "isolated.bin", 1024 * 1024);
            using (var db = SqliteTestUtil.CreateContext(dbPath))
            {
                var note = db.Notes.Single(n => n.Text == token);
                db.NoteAttachments.Add(new NoteAttachment
                {
                    NoteId = note.Id,
                    OriginalFileName = "isolated.bin",
                    StoredFileName = saved.StoredFileName,
                    RelativePath = saved.RelativePath,
                    ContentType = saved.ContentType,
                    Size = saved.Size,
                    Sha256 = saved.Sha256,
                    CreatedAt = DateTime.Now
                });
                db.SaveChanges();
            }

            string archivePath = Path.Combine(root, "portable", "notes.qnar");
            Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
            var service = new EncryptedArchiveService();
            EncryptedArchiveCreateResult created = service.Create(new EncryptedArchiveCreateRequest
            {
                DatabasePath = dbPath,
                AttachmentsDirectory = storage.AttachmentsDirectory,
                DestinationArchivePath = archivePath,
                ArchivePassword = "isolated-archive-password-not-in-argv",
                Pbkdf2Iterations = EncryptedArchiveConstants.MinKdfIterationsUntrusted
            });

            string isolatedUser = Path.Combine(root, "fake-user");
            string isolatedLocal = Path.Combine(isolatedUser, "AppData", "Local");
            string isolatedRoaming = Path.Combine(isolatedUser, "AppData", "Roaming");
            string isolatedTemp = Path.Combine(isolatedUser, "Temp");
            Directory.CreateDirectory(isolatedLocal);
            Directory.CreateDirectory(isolatedRoaming);
            Directory.CreateDirectory(isolatedTemp);

            string dest = Path.Combine(root, "restored-new-dir");
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("--restore-archive");
            psi.ArgumentList.Add(archivePath);
            psi.ArgumentList.Add("--destination");
            psi.ArgumentList.Add(dest);
            psi.ArgumentList.Add("--recovery-key-stdin");
            psi.Environment["USERPROFILE"] = isolatedUser;
            psi.Environment["LOCALAPPDATA"] = isolatedLocal;
            psi.Environment["APPDATA"] = isolatedRoaming;
            psi.Environment["TEMP"] = isolatedTemp;
            psi.Environment["TMP"] = isolatedTemp;

            using var process = Process.Start(psi);
            Assert.NotNull(process);
            process!.StandardInput.Write(created.RecoveryKeyFormatted);
            process.StandardInput.Close();
            bool exited = process.WaitForExit(120_000);
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            if (!exited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Hang is a test failure below.
                }
            }

            Assert.True(exited, "Isolated-profile restore must exit without UI.");
            Assert.Equal(EncryptedArchiveRestoreExitCodes.Success, process.ExitCode);
            Assert.DoesNotContain(created.RecoveryKeyFormatted, stdout, StringComparison.Ordinal);
            Assert.DoesNotContain(created.RecoveryKeyFormatted, stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(created.RecoveryKeyFormatted, string.Join('\0', psi.ArgumentList), StringComparison.Ordinal);

            string restoredDb = Path.Combine(dest, EncryptedArchiveConstants.RestoredDatabaseFileName);
            Assert.True(File.Exists(restoredDb));
            using (var restored = SqliteTestUtil.CreateContext(restoredDb))
            {
                Assert.Contains(restored.Notes, n => n.Text == token);
            }

            string restoredAtt = Directory.GetFiles(
                Path.Combine(dest, EncryptedArchiveConstants.RestoredAttachmentsDirectoryName),
                "*",
                SearchOption.AllDirectories).Single();
            Assert.Equal(attachment, File.ReadAllBytes(restoredAtt));

            Assert.Equal(liveBefore, SnapshotLiveProfile(liveRoot));
            Assert.False(File.Exists(Path.Combine(isolatedLocal, "QuickNotes", "s3_credentials.dat")));
            Assert.False(File.Exists(Path.Combine(isolatedLocal, "QuickNotes", "sync_password.dat")));
            foreach (string file in Directory.GetFiles(isolatedUser, "*", SearchOption.AllDirectories))
            {
                string body = File.ReadAllText(file);
                Assert.DoesNotContain(created.RecoveryKeyFormatted, body, StringComparison.Ordinal);
            }
        }
        finally
        {
            SqliteTestUtil.TryDeleteDirectory(root);
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
