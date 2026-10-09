using System;
using System.Diagnostics;
using System.IO;
using QuickNotes.App.Data;
using QuickNotes.App.Services;
using QuickNotes.App.Services.EncryptedArchive;
using QuickNotes.App.Services.NoteProtection;
using QuickNotes.App.Services.Sync;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public sealed class EncryptedArchivePbkdf2DefaultTests
{
    [Fact]
    public void ProductionDefault_IsBoundedAndIndependentOfNoteAndSyncConstants()
    {
        int shipping = EncryptedArchiveConstants.DefaultPbkdf2IterationsForNewArchives;
        Assert.InRange(
            shipping,
            EncryptedArchiveConstants.MinKdfIterationsUntrusted,
            EncryptedArchiveConstants.MaxKdfIterations);
        Assert.NotEqual(NoteCryptoService.DefaultIterationsConst, shipping);
        Assert.NotEqual(SyncCryptoService.DefaultIterations, shipping);
        Assert.Equal(shipping, EncryptedArchiveKdfLimits.Production.ResolveCreateIterations(null));
        Assert.Equal(20_000, EncryptedArchiveKdfLimits.Production.ResolveCreateIterations(20_000));
    }

    [Fact]
    public void Create_OmittingIterations_WritesShippingDefault_AndProductionReaderUsesHeaderN()
    {
        using var fx = ArchiveFx.Create(useProductionLimits: true);
        fx.AddOpenNote("default-n");
        EncryptedArchiveCreateResult created = fx.CreateArchive(pbkdf2Iterations: null);
        Assert.Equal(EncryptedArchiveConstants.DefaultPbkdf2IterationsForNewArchives, created.Pbkdf2Iterations);

        EncryptedArchiveFramedFile framed = EncryptedArchiveFraming.ReadBoundedAllowingKdfForTests(
            File.ReadAllBytes(created.ArchivePath));
        Assert.Equal(EncryptedArchiveConstants.DefaultPbkdf2IterationsForNewArchives, framed.Header.KdfIterations);

        EncryptedArchiveDryRunResult dry = new EncryptedArchiveService().DryRun(new EncryptedArchiveDryRunRequest
        {
            ArchivePath = created.ArchivePath,
            ArchivePassword = fx.ArchivePassword
        });
        Assert.Equal(created.ArchiveId, dry.ArchiveId);
    }

    [Fact]
    public void Create_ExplicitIterations_AreStoredAndRead_NotReplacedByDefault()
    {
        const int explicitN = 20_000;
        using var fx = ArchiveFx.Create(useProductionLimits: true);
        fx.AddOpenNote("explicit-n");
        EncryptedArchiveCreateResult created = fx.CreateArchive(explicitN);
        Assert.Equal(explicitN, created.Pbkdf2Iterations);
        Assert.NotEqual(EncryptedArchiveConstants.DefaultPbkdf2IterationsForNewArchives, explicitN);

        EncryptedArchiveFramedFile framed = EncryptedArchiveFraming.ReadBoundedAllowingKdfForTests(
            File.ReadAllBytes(created.ArchivePath));
        Assert.Equal(explicitN, framed.Header.KdfIterations);

        EncryptedArchiveDryRunResult dry = new EncryptedArchiveService().DryRun(new EncryptedArchiveDryRunRequest
        {
            ArchivePath = created.ArchivePath,
            ArchivePassword = fx.ArchivePassword
        });
        Assert.Equal(created.ArchiveId, dry.ArchiveId);
    }

    [Fact]
    public void BackwardRead_OldExplicitN_IsUsedByProductionReader_IgnoringShippingDefault()
    {
        const int oldN = 12_345;
        using var fx = ArchiveFx.Create(useProductionLimits: false);
        fx.AddOpenNote("old-n");
        EncryptedArchiveCreateResult created = fx.CreateArchive(oldN);
        Assert.Equal(oldN, created.Pbkdf2Iterations);

        EncryptedArchiveDryRunResult dry = new EncryptedArchiveService().DryRun(new EncryptedArchiveDryRunRequest
        {
            ArchivePath = created.ArchivePath,
            ArchivePassword = fx.ArchivePassword
        });
        Assert.Equal(created.ArchiveId, dry.ArchiveId);

        EncryptedArchiveFramedFile framed = EncryptedArchiveFraming.ReadBoundedAllowingKdfForTests(
            File.ReadAllBytes(created.ArchivePath));
        Assert.Equal(oldN, framed.Header.KdfIterations);
        Assert.NotEqual(EncryptedArchiveConstants.DefaultPbkdf2IterationsForNewArchives, framed.Header.KdfIterations);
    }

    [Fact]
    public void Create_OutOfRangeIterations_RejectedBeforePbkdf2()
    {
        var service = new EncryptedArchiveService();
        EncryptedArchiveCrypto.ResetPbkdf2Invocations();
        Assert.Throws<EncryptedArchiveFormatException>(() => service.Create(new EncryptedArchiveCreateRequest
        {
            DatabasePath = "missing.db",
            AttachmentsDirectory = "missing-att",
            DestinationArchivePath = "out.qnar",
            ArchivePassword = "password",
            Pbkdf2Iterations = EncryptedArchiveConstants.MinKdfIterationsUntrusted - 1
        }));
        Assert.Equal(0, EncryptedArchiveCrypto.Pbkdf2Invocations);

        EncryptedArchiveCrypto.ResetPbkdf2Invocations();
        Assert.Throws<EncryptedArchiveFormatException>(() => service.Create(new EncryptedArchiveCreateRequest
        {
            DatabasePath = "missing.db",
            AttachmentsDirectory = "missing-att",
            DestinationArchivePath = "out.qnar",
            ArchivePassword = "password",
            Pbkdf2Iterations = EncryptedArchiveConstants.MaxKdfIterations + 1
        }));
        Assert.Equal(0, EncryptedArchiveCrypto.Pbkdf2Invocations);
    }

    [Fact]
    public void Read_KdfIterationsBelowMin_RejectedBeforePbkdf2()
    {
        using var fx = ArchiveFx.Create(useProductionLimits: false);
        fx.AddOpenNote("low-n");
        EncryptedArchiveCreateResult created = fx.CreateArchive(20_000);
        byte[] original = File.ReadAllBytes(created.ArchivePath);
        EncryptedArchiveFramedFile framed = EncryptedArchiveFraming.ReadBoundedAllowingKdfForTests(original);
        string[] parts = framed.CanonicalHeaderText.Split('|');
        parts[6] = (EncryptedArchiveConstants.MinKdfIterationsUntrusted - 1).ToString();
        string path = Path.Combine(fx.Root, "low-kdf.qnar");
        File.WriteAllBytes(path, EncryptedArchiveFraming.ReplaceCanonicalHeader(original, string.Join('|', parts)));

        EncryptedArchiveCrypto.ResetPbkdf2Invocations();
        var sw = Stopwatch.StartNew();
        Assert.Throws<EncryptedArchiveFormatException>(() => new EncryptedArchiveService().DryRun(new EncryptedArchiveDryRunRequest
        {
            ArchivePath = path,
            ArchivePassword = fx.ArchivePassword
        }));
        sw.Stop();
        Assert.Equal(0, EncryptedArchiveCrypto.Pbkdf2Invocations);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"KDF min check took {sw.Elapsed}");
    }

    private sealed class ArchiveFx : IDisposable
    {
        public string Root { get; }
        public string DbPath { get; }
        public string ArchivePath { get; }
        public string ArchivePassword { get; } = "archive-password-kdf-" + Guid.NewGuid().ToString("N");
        public AttachmentStorageService Storage { get; }
        public EncryptedArchiveService Service { get; }

        private ArchiveFx(bool useProductionLimits)
        {
            Root = Path.Combine(Path.GetTempPath(), "qn_enc_kdf_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            DbPath = Path.Combine(Root, "data", "quicknotes.db");
            Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
            Storage = new AttachmentStorageService(Path.Combine(Root, "data"));
            ArchivePath = Path.Combine(Root, "out", "backup.qnar");
            Directory.CreateDirectory(Path.GetDirectoryName(ArchivePath)!);
            Service = useProductionLimits
                ? new EncryptedArchiveService()
                : new EncryptedArchiveService(EncryptedArchiveKdfLimits.ForTests());
            using var db = SqliteTestUtil.CreateContext(DbPath);
            DbInitializer.Initialize(db);
        }

        public static ArchiveFx Create(bool useProductionLimits) => new(useProductionLimits);

        public void AddOpenNote(string text)
        {
            using var db = SqliteTestUtil.CreateContext(DbPath);
            db.Notes.Add(new QuickNotes.App.Models.Note
            {
                Text = text,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            });
            db.SaveChanges();
        }

        public EncryptedArchiveCreateResult CreateArchive(int? pbkdf2Iterations)
            => Service.Create(new EncryptedArchiveCreateRequest
            {
                DatabasePath = DbPath,
                AttachmentsDirectory = Storage.AttachmentsDirectory,
                DestinationArchivePath = ArchivePath,
                ArchivePassword = ArchivePassword,
                Pbkdf2Iterations = pbkdf2Iterations
            });

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
