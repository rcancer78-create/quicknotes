using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using QuickNotes.App.Services.Crypto;
using QuickNotes.App.Services.EncryptedArchive;
using QuickNotes.App.Services.NoteProtection;
using QuickNotes.App.Services.Sync;
using QuickNotes.Tools;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Unit)]
public sealed class Argon2idFeasibilityBenchmarkTests
{
    [Fact]
    public void BuildRecommendation_IsSpikeOnly_AndDoesNotAdoptProductionArgon2idOrPbkdf2Raise()
    {
        Argon2idFeasibilityRecommendation rec = Argon2idFeasibilityBenchmark.BuildRecommendation();
        Assert.Equal(Argon2idFeasibilityBenchmark.IsolatedSpikeStrategy, rec.Strategy);
        Assert.False(rec.Pbkdf2RaiseAccepted);
        Assert.False(rec.Argon2idProductionDefaultAccepted);
        Assert.False(rec.ChangeProductionConstantsInThisStage);
        Assert.True(rec.ReadyForLaterMigration);
        Assert.Contains("not a production default", rec.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("49_490_000", rec.Summary, StringComparison.Ordinal);
        Assert.Contains("20_000_000", rec.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Argon2id is now the default", rec.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParseArgs_RejectsSecretsUnknownFlagsAndAcceptsTinyProfile()
    {
        Assert.True(Argon2idFeasibilityBenchmark.TryParseArgs(Array.Empty<string>(), out Argon2idFeasibilityOptions defaults, out string error));
        Assert.Equal(string.Empty, error);
        Assert.Equal(Argon2idFeasibilityBenchmark.DefaultPublishedProfileIds, defaults.ProfileIds);
        Assert.False(defaults.Help);

        Assert.True(Argon2idFeasibilityBenchmark.TryParseArgs(
            new[] { "--profile", "tiny", "--json", "--warmups", "1", "--repeats", "1" },
            out Argon2idFeasibilityOptions tiny,
            out error));
        Assert.Equal(new[] { Argon2idFeasibilityBenchmark.LaboratoryTinyProfileId }, tiny.ProfileIds);
        Assert.True(tiny.JsonOnly);
        Assert.Equal(1, tiny.Warmups);
        Assert.Equal(1, tiny.Repeats);

        Assert.False(Argon2idFeasibilityBenchmark.TryParseArgs(new[] { "--nope" }, out _, out error));
        Assert.Equal("Unknown argument.", error);
        Assert.False(Argon2idFeasibilityBenchmark.TryParseArgs(new[] { "--profile", "pbkdf2" }, out _, out error));
        Assert.Equal("Unknown or missing --profile value.", error);
        Assert.False(Argon2idFeasibilityBenchmark.TryParseArgs(new[] { "C:\\Users\\user\\QuickNotes.db" }, out _, out error));
        Assert.Equal("Benchmark does not accept file paths, databases or secrets.", error);
    }

    [Fact]
    public void Host_HelpAndBadArgs_DoNotMeasure()
    {
        Assert.Equal(EncryptedArchiveRestoreExitCodes.Success, Argon2idFeasibilityBenchmarkHost.Run(new[] { "--help" }));
        Assert.Equal(EncryptedArchiveRestoreExitCodes.Usage, Argon2idFeasibilityBenchmarkHost.Run(new[] { "--nope" }));
        Assert.Equal(EncryptedArchiveRestoreExitCodes.Usage, Argon2idFeasibilityBenchmarkHost.Run(new[] { "notes.db" }));
    }

    [Fact]
    public void LaboratoryTinyRun_Derives32ByteKey_DoesNotLeakDummyPassword_AndLeavesProductionNUnchanged()
    {
        var options = new Argon2idFeasibilityOptions
        {
            ProfileIds = new[] { Argon2idFeasibilityBenchmark.LaboratoryTinyProfileId },
            Warmups = 1,
            Repeats = 1,
            JsonOnly = false
        };

        Argon2idFeasibilityResult result = Argon2idFeasibilityBenchmark.Run(options);
        Assert.Equal(Argon2idFeasibilityBenchmark.AlgorithmId, result.Algorithm);
        Assert.Equal(Argon2idFeasibilityBenchmark.IsolatedSpikeStrategy, result.Recommendation.Strategy);
        Assert.False(result.Recommendation.Argon2idProductionDefaultAccepted);
        Assert.Single(result.Profiles);
        Assert.Equal(Argon2idFeasibilityBenchmark.LaboratoryTinyProfileId, result.Profiles[0].Profile.Id);
        Assert.True(result.Profiles[0].MedianMs >= 0);
        Assert.Contains("note=120000", result.ProductionPbkdf2Unchanged, StringComparison.Ordinal);
        Assert.Contains("sync=100000", result.ProductionPbkdf2Unchanged, StringComparison.Ordinal);
        Assert.Contains("archive=2490000", result.ProductionPbkdf2Unchanged, StringComparison.Ordinal);
        Assert.Equal(120_000, NoteCryptoService.DefaultIterationsConst);
        Assert.Equal(100_000, SyncCryptoService.DefaultIterations);
        Assert.Equal(2_490_000, EncryptedArchiveConstants.DefaultPbkdf2IterationsForNewArchives);

        using var writer = new StringWriter();
        Argon2idFeasibilityBenchmark.WriteReport(result, writer, jsonOnly: false);
        string text = writer.ToString();
        Assert.Contains("NotProductionDefault: true", text, StringComparison.Ordinal);
        Assert.Contains("Pbkdf2RaiseAccepted: false", text, StringComparison.Ordinal);
        Assert.Contains("Argon2idProductionDefaultAccepted: false", text, StringComparison.Ordinal);
        Assert.Contains("SyncCycleScale: packages=1 attachments=100", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Argon2idFeasibilityBenchmark.DummyPassword, text, StringComparison.Ordinal);
        Assert.DoesNotContain("MainWindow", text, StringComparison.Ordinal);

        string json = Argon2idFeasibilityBenchmark.FormatJson(result);
        Assert.DoesNotContain(Argon2idFeasibilityBenchmark.DummyPassword, json, StringComparison.Ordinal);
        Assert.Contains(Argon2idFeasibilityBenchmark.IsolatedSpikeStrategy, json, StringComparison.Ordinal);

        byte[] salt = Enumerable.Range(0, 32).Select(i => (byte)(0x5A ^ (byte)i)).ToArray();
        byte[] key = Argon2idFeasibilityBenchmark.DeriveOnce(
            Argon2idFeasibilityBenchmark.DummyPassword,
            salt,
            Argon2idFeasibilityBenchmark.LaboratoryTiny);
        Assert.Equal(NoteCryptoService.KeyByteSize, key.Length);
        CryptographicOperations.ZeroMemory(key);
    }

    [Fact]
    public void ProductionReaders_StillRejectArgon2idDescriptorsBeforePbkdf2()
    {
        KdfDiagnostics.ResetPbkdf2Invocations();
        Assert.False(KdfDescriptor.TryParse(
            "ARGON2ID|1|120000|32",
            KdfDescriptorLimits.LocalEnvelope,
            out _,
            out string error));
        Assert.Equal("unsupported kdf algorithm", error);

        var unknown = new KdfDescriptor("ARGON2ID", 1, 120_000, 32);
        Assert.Throws<KdfDescriptorValidationException>(() => unknown.Validate(KdfDescriptorLimits.CloudEnvelope));
        Assert.Throws<KdfDescriptorValidationException>(() => unknown.Validate(KdfDescriptorLimits.ArchiveUntrusted));
        Assert.Equal(0, KdfDiagnostics.Pbkdf2Invocations);
    }

    [Fact]
    public void AppProject_DoesNotReferenceArgon2Package()
    {
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string appCsproj = Path.Combine(repoRoot, "QuickNotes.App", "QuickNotes.App.csproj");
        Assert.True(File.Exists(appCsproj), appCsproj);
        string text = File.ReadAllText(appCsproj);
        Assert.DoesNotContain("Argon2", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Konscious", text, StringComparison.OrdinalIgnoreCase);
    }
}
