using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using QuickNotes.App.Services.Crypto;
using QuickNotes.App.Services.EncryptedArchive;
using QuickNotes.App.Services.NoteProtection;
using QuickNotes.App.Services.Sync;
using QuickNotes.Tools;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Unit)]
public sealed class Pbkdf2CostBenchmarkTests
{
    [Fact]
    public void SuggestIterations_ScalesToTarget_RoundsDown_AndClamps()
    {
        Assert.Equal(1_490_000, Pbkdf2CostBenchmark.SuggestIterations(50_000, 5.015, 150, 10_000, 5_000_000));
        Assert.Equal(490_000, Pbkdf2CostBenchmark.SuggestIterations(50_000, 5.015, 50, 10_000, 5_000_000));
        Assert.Equal(990_000, Pbkdf2CostBenchmark.SuggestIterations(50_000, 5.015, 100, 10_000, 5_000_000));
        Assert.Equal(2_490_000, Pbkdf2CostBenchmark.SuggestIterations(50_000, 5.015, 250, 10_000, 5_000_000));
        Assert.Equal(10_000, Pbkdf2CostBenchmark.SuggestIterations(50_000, 10_000, 150, 10_000, 5_000_000));
        Assert.Equal(5_000_000, Pbkdf2CostBenchmark.SuggestIterations(50_000, 0.001, 150, 10_000, 5_000_000));
        Assert.Equal(10_000, Pbkdf2CostBenchmark.SuggestIterations(50_000, 0, 150, 10_000, 5_000_000));
    }

    [Fact]
    public void ClampExperimentalIterations_HonorsBounds()
    {
        Assert.Equal(740_000, Pbkdf2CostBenchmark.ClampExperimentalIterations(740_000, 10_000, 5_000_000));
        Assert.Equal(10_000, Pbkdf2CostBenchmark.ClampExperimentalIterations(5_000, 10_000, 5_000_000));
        Assert.Equal(5_000_000, Pbkdf2CostBenchmark.ClampExperimentalIterations(9_000_000, 10_000, 5_000_000));
    }

    [Fact]
    public void EstimateLinearCycleMs_IsPackagesPlusAttachments()
    {
        Assert.Equal(10.183, Pbkdf2CostBenchmark.EstimateLinearCycleMs(10.183, 1, 0), 3);
        Assert.Equal(20.366, Pbkdf2CostBenchmark.EstimateLinearCycleMs(10.183, 1, 1), 3);
        Assert.Equal(112.013, Pbkdf2CostBenchmark.EstimateLinearCycleMs(10.183, 1, 10), 3);
        Assert.Equal(1028.483, Pbkdf2CostBenchmark.EstimateLinearCycleMs(10.183, 1, 100), 3);
        Assert.Throws<ArgumentOutOfRangeException>(() => Pbkdf2CostBenchmark.EstimateLinearCycleMs(10, 0, 0));
    }

    [Fact]
    public void BuildSyncCycleScale_CoversRequiredAttachmentCounts_WithoutAssumingTwoKdfs()
    {
        var rows = Pbkdf2CostBenchmark.BuildSyncCycleScale(10.183, 100_000);
        Assert.Equal(new[] { 0, 1, 10, 100 }, rows.Select(r => r.AttachmentCount).ToArray());
        Assert.All(rows, row => Assert.Equal(1, row.PackageCount));
        Assert.All(rows, row => Assert.Equal(row.PackageCount + row.AttachmentCount, row.DerivationCount));
        Assert.All(rows, row => Assert.Equal(100_000, row.IterationsPerEnvelope));
        Assert.All(rows, row => Assert.Equal(Pbkdf2CostBenchmark.LinearUniqueSaltModel, row.Model));
        Assert.Equal(10.183, rows[0].EstimatedMedianMs, 3);
        Assert.Equal(1028.483, rows[3].EstimatedMedianMs, 3);
        Assert.DoesNotContain(rows, row => row.DerivationCount == 2 && row.AttachmentCount != 1);
    }

    [Fact]
    public void TryParseArgs_DefaultIsAllCircuits_AndRejectsSecretsAndUnknown()
    {
        Assert.True(Pbkdf2CostBenchmark.TryParseArgs(Array.Empty<string>(), out Pbkdf2CostBenchmarkOptions options, out string error));
        Assert.Equal(string.Empty, error);
        Assert.Equal(3, options.Circuits.Count);
        Assert.False(options.JsonOnly);
        Assert.True(options.MeasureCurrent);
        Assert.True(options.MeasureExperimentalPoints);
        Assert.Equal(3, options.Warmups);
        Assert.Equal(5, options.Repeats);
        Assert.Equal(50_000, options.ProbeIterations);

        Assert.True(Pbkdf2CostBenchmark.TryParseArgs(
            new[] { "--circuit", "note", "--circuit", "sync", "--json", "--warmups", "2", "--repeats", "4", "--probe", "20000", "--skip-current", "--skip-experimental" },
            out options,
            out error));
        Assert.Equal(string.Empty, error);
        Assert.Equal(new[] { Pbkdf2CircuitKind.Note, Pbkdf2CircuitKind.Sync }, options.Circuits);
        Assert.True(options.JsonOnly);
        Assert.False(options.MeasureCurrent);
        Assert.False(options.MeasureExperimentalPoints);
        Assert.Equal(2, options.Warmups);
        Assert.Equal(4, options.Repeats);
        Assert.Equal(20_000, options.ProbeIterations);

        Assert.True(Pbkdf2CostBenchmark.TryParseArgs(new[] { "--skip-confirm" }, out options, out error));
        Assert.False(options.MeasureExperimentalPoints);

        Assert.False(Pbkdf2CostBenchmark.TryParseArgs(new[] { "--nope" }, out _, out error));
        Assert.Equal("Unknown argument.", error);

        Assert.False(Pbkdf2CostBenchmark.TryParseArgs(new[] { "--circuit", "argon2" }, out _, out error));
        Assert.Equal("Unknown or missing --circuit value.", error);

        Assert.False(Pbkdf2CostBenchmark.TryParseArgs(new[] { "C:\\Users\\user\\QuickNotes.db" }, out _, out error));
        Assert.Equal("Benchmark does not accept file paths, databases or secrets.", error);

        Assert.False(Pbkdf2CostBenchmark.TryParseArgs(new[] { "--password" }, out _, out error));
        Assert.Equal("Benchmark does not accept file paths, databases or secrets.", error);

        Assert.True(Pbkdf2CostBenchmark.LooksLikeUserSecretOrDatabasePath("notes.qnar"));
        Assert.True(Pbkdf2CostBenchmark.LooksLikeUserSecretOrDatabasePath("dpapi.bin"));
        Assert.False(Pbkdf2CostBenchmark.LooksLikeUserSecretOrDatabasePath("--json"));
    }

    [Fact]
    public void BuildRecommendation_NeverRaisesPbkdf2OrAdoptsArgon2id()
    {
        Pbkdf2CostRecommendation rec = Pbkdf2CostBenchmark.BuildRecommendation();
        Assert.Equal(Pbkdf2CostBenchmark.MeasurementAcceptedNoChangeStrategy, rec.Strategy);
        Assert.False(rec.Pbkdf2RaiseAccepted);
        Assert.False(rec.Argon2idAccepted);
        Assert.False(rec.ChangeProductionConstantsInThisStage);
        Assert.Equal(EncryptedArchiveConstants.DefaultPbkdf2IterationsForNewArchives, rec.ArchiveShippingIterationsUnchanged);
        Assert.Equal(Pbkdf2CostBenchmark.Argon2idSeparateEvaluation, rec.Argon2id);
        Assert.DoesNotContain("keep-pbkdf2-new-defaults-after-review", rec.Strategy, StringComparison.Ordinal);
        Assert.DoesNotContain("adopt the measured candidates", rec.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Neither a PBKDF2 iteration raise nor Argon2id is accepted", rec.Summary, StringComparison.Ordinal);
        Assert.Contains("O(packages + attachments)", rec.Summary, StringComparison.Ordinal);
        Assert.Contains("isolated Tools spike (ADR-016), not a production default", rec.Summary, StringComparison.Ordinal);
        Assert.Contains("not claimed to be the only path", rec.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("low-end", rec.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildCloudDosNote_DocumentsPerEnvelopeCapAndCycleBudget()
    {
        Pbkdf2CloudDosNote dos = Pbkdf2CostBenchmark.BuildCloudDosNote();
        Assert.Equal(5_000_000, dos.MaxIterationsPerUntrustedEnvelope);
        Assert.True(dos.BatchKdfBudgetImplemented);
        Assert.Equal(UntrustedInboundKdfWorkBudget.DefaultCycleBudgetIterations, dos.CycleBudgetIterations);
        Assert.Equal(20_000_000, dos.CycleBudgetIterations);
        Assert.Contains("20_000_000", dos.RequiredBeforeRaisingN, StringComparison.Ordinal);
        Assert.Contains("cumulative", dos.RequiredBeforeRaisingN, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Pbkdf2CostBenchmark.LinearUniqueSaltModel, dos.CostModel);
    }

    [Fact]
    public void WriteReport_IncludesMachineFieldsJson_AndDoesNotLeakDummyPasswordOrRejectedStrategy()
    {
        var syncCurrent = new Pbkdf2SampleStats
        {
            Iterations = 100_000,
            MedianMs = 10.183,
            MinMs = 10.090,
            MaxMs = 10.259
        };
        var result = new Pbkdf2CostBenchmarkResult
        {
            MeasuredAtUtc = new DateTimeOffset(2026, 9, 13, 4, 0, 0, TimeSpan.Zero),
            OsDescription = "test-os",
            FrameworkDescription = "test-runtime",
            ProcessorCount = 12,
            ProcessorIdentifier = "test-cpu",
            Warmups = 3,
            Repeats = 5,
            ProbeIterations = 50_000,
            ProbeMedianMs = 5.015,
            Algorithm = KdfAlgorithmIds.Pbkdf2HmacSha256,
            Primitive = "Rfc2898DeriveBytes.Pbkdf2",
            DkLen = 32,
            SaltLen = 32,
            Circuits = new[]
            {
                new Pbkdf2CircuitReport
                {
                    Id = "note",
                    CurrentProductionIterations = NoteCryptoService.DefaultIterationsConst,
                    CostModel = "One PBKDF2 per note unlock",
                    Current = new Pbkdf2SampleStats
                    {
                        Iterations = 120_000,
                        MedianMs = 12.580,
                        MinMs = 12.432,
                        MaxMs = 13.110
                    },
                    ExperimentalPoints = new[]
                    {
                        new Pbkdf2ExperimentalPoint
                        {
                            Id = "note-approx-150ms",
                            Role = "experimental-timing-point-not-a-default",
                            ApproximateTargetMs = 150,
                            Iterations = 1_440_000,
                            Measured = new Pbkdf2SampleStats
                            {
                                Iterations = 1_440_000,
                                MedianMs = 148.160,
                                MinMs = 147.339,
                                MaxMs = 148.758
                            }
                        }
                    }
                },
                new Pbkdf2CircuitReport
                {
                    Id = "sync",
                    CurrentProductionIterations = SyncCryptoService.DefaultIterations,
                    CostModel = Pbkdf2CostBenchmark.LinearUniqueSaltModel,
                    Current = syncCurrent,
                    CycleScale = Pbkdf2CostBenchmark.BuildSyncCycleScale(10.183, 100_000)
                }
            },
            CloudEnvelopeDos = Pbkdf2CostBenchmark.BuildCloudDosNote(),
            Recommendation = Pbkdf2CostBenchmark.BuildRecommendation()
        };

        using var writer = new StringWriter();
        Pbkdf2CostBenchmark.WriteReport(result, writer, jsonOnly: false);
        string text = writer.ToString();
        Assert.Contains("not a universal cost", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not product-path targets", text, StringComparison.Ordinal);
        Assert.Contains("DateUtc:", text, StringComparison.Ordinal);
        Assert.Contains("Warmups: 3", text, StringComparison.Ordinal);
        Assert.Contains("Repeats: 5", text, StringComparison.Ordinal);
        Assert.Contains("ProbeIterations: 50000", text, StringComparison.Ordinal);
        Assert.Contains("JsonReport:", text, StringComparison.Ordinal);
        Assert.Contains("\"circuits\"", text, StringComparison.Ordinal);
        Assert.Contains("SyncCycleScale: packages=1 attachments=0", text, StringComparison.Ordinal);
        Assert.Contains("SyncCycleScale: packages=1 attachments=100", text, StringComparison.Ordinal);
        Assert.Contains("Pbkdf2RaiseAccepted: false", text, StringComparison.Ordinal);
        Assert.Contains("Argon2idAccepted: false", text, StringComparison.Ordinal);
        Assert.Contains("CloudDosBatchKdfBudgetImplemented: true", text, StringComparison.Ordinal);
        Assert.Contains("CloudDosCycleBudgetIterations: 20000000", text, StringComparison.Ordinal);
        Assert.DoesNotContain("AssumedDerivationsPerCycle", text, StringComparison.Ordinal);
        Assert.DoesNotContain("CandidateIterations", text, StringComparison.Ordinal);
        Assert.DoesNotContain("keep-pbkdf2-new-defaults-after-review", text, StringComparison.Ordinal);
        Assert.DoesNotContain("search p95", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Pbkdf2CostBenchmark.DummyPassword, text, StringComparison.Ordinal);
        Assert.DoesNotContain("LOCALAPPDATA", text, StringComparison.OrdinalIgnoreCase);

        using var jsonWriter = new StringWriter();
        Pbkdf2CostBenchmark.WriteReport(result, jsonWriter, jsonOnly: true);
        string json = jsonWriter.ToString();
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal("PBKDF2-HMAC-SHA256", doc.RootElement.GetProperty("algorithm").GetString());
        Assert.Equal(32, doc.RootElement.GetProperty("dkLen").GetInt32());
        Assert.Equal(
            Pbkdf2CostBenchmark.MeasurementAcceptedNoChangeStrategy,
            doc.RootElement.GetProperty("recommendation").GetProperty("strategy").GetString());
        Assert.False(doc.RootElement.GetProperty("recommendation").GetProperty("pbkdf2RaiseAccepted").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("recommendation").GetProperty("argon2idAccepted").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("cloudEnvelopeDos").GetProperty("batchKdfBudgetImplemented").GetBoolean());
        Assert.Equal(20_000_000, doc.RootElement.GetProperty("cloudEnvelopeDos").GetProperty("cycleBudgetIterations").GetInt64());
        Assert.DoesNotContain(Pbkdf2CostBenchmark.DummyPassword, json, StringComparison.Ordinal);
        Assert.DoesNotContain("keep-pbkdf2-new-defaults-after-review", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Host_HelpAndBadArgs_DoNotMeasure()
    {
        Assert.Equal(EncryptedArchiveRestoreExitCodes.Success, Pbkdf2CostBenchmarkHost.Run(new[] { "--help" }));
        Assert.Equal(EncryptedArchiveRestoreExitCodes.Usage, Pbkdf2CostBenchmarkHost.Run(new[] { "--nope" }));
        Assert.Equal(EncryptedArchiveRestoreExitCodes.Usage, Pbkdf2CostBenchmarkHost.Run(new[] { "notes.db" }));
    }

    [Fact]
    public void Run_TinyProbeWithoutExperimental_DoesNotAssertTimingAndDoesNotLeakSecrets()
    {
        var options = new Pbkdf2CostBenchmarkOptions
        {
            Circuits = new[] { Pbkdf2CircuitKind.Note, Pbkdf2CircuitKind.Sync },
            Warmups = 1,
            Repeats = 1,
            ProbeIterations = 10_000,
            MeasureCurrent = false,
            MeasureExperimentalPoints = false,
            JsonOnly = true
        };

        Pbkdf2CostBenchmarkResult result = Pbkdf2CostBenchmark.Run(options);
        Assert.Equal(KdfAlgorithmIds.Pbkdf2HmacSha256, result.Algorithm);
        Assert.Equal("Rfc2898DeriveBytes.Pbkdf2", result.Primitive);
        Assert.Equal(32, result.DkLen);
        Assert.True(result.ProbeMedianMs >= 0);
        Assert.Equal(2, result.Circuits.Count);
        Assert.Equal("note", result.Circuits[0].Id);
        Assert.Equal(NoteCryptoService.DefaultIterationsConst, result.Circuits[0].CurrentProductionIterations);
        Assert.Empty(result.Circuits[0].ExperimentalPoints);
        Assert.Equal("sync", result.Circuits[1].Id);
        Assert.Equal(4, result.Circuits[1].CycleScale.Count);
        Assert.Equal(new[] { 0, 1, 10, 100 }, result.Circuits[1].CycleScale.Select(r => r.AttachmentCount).ToArray());
        Assert.Equal(Pbkdf2CostBenchmark.MeasurementAcceptedNoChangeStrategy, result.Recommendation.Strategy);
        Assert.False(result.Recommendation.Pbkdf2RaiseAccepted);
        Assert.False(result.Recommendation.ChangeProductionConstantsInThisStage);
        Assert.True(result.CloudEnvelopeDos.BatchKdfBudgetImplemented);
        Assert.Equal(20_000_000, result.CloudEnvelopeDos.CycleBudgetIterations);
        string json = Pbkdf2CostBenchmark.FormatJson(result);
        Assert.DoesNotContain(Pbkdf2CostBenchmark.DummyPassword, json, StringComparison.Ordinal);
        Assert.DoesNotContain("keep-pbkdf2-new-defaults-after-review", json, StringComparison.Ordinal);
        Assert.Equal(120_000, NoteCryptoService.DefaultIterationsConst);
        Assert.Equal(100_000, SyncCryptoService.DefaultIterations);
        Assert.Equal(2_490_000, EncryptedArchiveConstants.DefaultPbkdf2IterationsForNewArchives);
    }
}
