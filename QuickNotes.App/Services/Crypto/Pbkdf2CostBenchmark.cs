using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuickNotes.App.Services.EncryptedArchive;
using QuickNotes.App.Services.NoteProtection;
using QuickNotes.App.Services.Sync;

namespace QuickNotes.App.Services.Crypto;

/// <summary>
/// Which envelope family a PBKDF2 sample belongs to. Archive is a reference circuit only.
/// </summary>
public enum Pbkdf2CircuitKind
{
    Note = 0,
    Sync = 1,
    Archive = 2
}

/// <summary>
/// Local, reproducible PBKDF2-HMAC-SHA256 cost measurement for note, sync/blob and archive.
/// Dummy password and fixed non-secret salt only; no user DB, DPAPI, live profile or cloud.
/// </summary>
public sealed class Pbkdf2CostBenchmarkOptions
{
    public IReadOnlyList<Pbkdf2CircuitKind> Circuits { get; init; } = Pbkdf2CostBenchmark.AllCircuits;

    public int Warmups { get; init; } = Pbkdf2CostBenchmark.DefaultWarmups;

    public int Repeats { get; init; } = Pbkdf2CostBenchmark.DefaultRepeats;

    public int ProbeIterations { get; init; } = Pbkdf2CostBenchmark.DefaultProbeIterations;

    public bool JsonOnly { get; init; }

    public bool MeasureCurrent { get; init; } = true;

    public bool MeasureExperimentalPoints { get; init; } = true;

    public bool Help { get; init; }
}

public sealed class Pbkdf2SampleStats
{
    public required int Iterations { get; init; }

    public required double MedianMs { get; init; }

    public required double MinMs { get; init; }

    public required double MaxMs { get; init; }
}

public sealed class Pbkdf2ExperimentalPoint
{
    public required string Id { get; init; }

    public required string Role { get; init; }

    public required double ApproximateTargetMs { get; init; }

    public required int Iterations { get; init; }

    public Pbkdf2SampleStats? Measured { get; init; }
}

public sealed class Pbkdf2SyncCycleScaleRow
{
    public required int PackageCount { get; init; }

    public required int AttachmentCount { get; init; }

    public required int DerivationCount { get; init; }

    public required int IterationsPerEnvelope { get; init; }

    public required double EstimatedMedianMs { get; init; }

    public required string Model { get; init; }
}

public sealed class Pbkdf2CircuitReport
{
    public required string Id { get; init; }

    public required int CurrentProductionIterations { get; init; }

    public required string CostModel { get; init; }

    public Pbkdf2SampleStats? Current { get; init; }

    public IReadOnlyList<Pbkdf2ExperimentalPoint> ExperimentalPoints { get; init; } =
        Array.Empty<Pbkdf2ExperimentalPoint>();

    public IReadOnlyList<Pbkdf2SyncCycleScaleRow> CycleScale { get; init; } =
        Array.Empty<Pbkdf2SyncCycleScaleRow>();
}

public sealed class Pbkdf2CloudDosNote
{
    public required int MaxIterationsPerUntrustedEnvelope { get; init; }

    public required string CostModel { get; init; }

    public required bool BatchKdfBudgetImplemented { get; init; }

    public required long CycleBudgetIterations { get; init; }

    public required string RequiredBeforeRaisingN { get; init; }
}

public sealed class Pbkdf2CostRecommendation
{
    public required string Strategy { get; init; }

    public required bool Pbkdf2RaiseAccepted { get; init; }

    public required bool Argon2idAccepted { get; init; }

    public required bool ChangeProductionConstantsInThisStage { get; init; }

    public required int ArchiveShippingIterationsUnchanged { get; init; }

    public required string Argon2id { get; init; }

    public required string Summary { get; init; }
}

public sealed class Pbkdf2CostBenchmarkResult
{
    public required DateTimeOffset MeasuredAtUtc { get; init; }

    public required string OsDescription { get; init; }

    public required string FrameworkDescription { get; init; }

    public required int ProcessorCount { get; init; }

    public required string ProcessorIdentifier { get; init; }

    public required int Warmups { get; init; }

    public required int Repeats { get; init; }

    public required int ProbeIterations { get; init; }

    public required double ProbeMedianMs { get; init; }

    public required string Algorithm { get; init; }

    public required string Primitive { get; init; }

    public required int DkLen { get; init; }

    public required int SaltLen { get; init; }

    public required IReadOnlyList<Pbkdf2CircuitReport> Circuits { get; init; }

    public required Pbkdf2CloudDosNote CloudEnvelopeDos { get; init; }

    public required Pbkdf2CostRecommendation Recommendation { get; init; }
}

public static class Pbkdf2CostBenchmark
{
    public const int DefaultWarmups = 3;
    public const int DefaultRepeats = 5;
    public const int DefaultProbeIterations = 50_000;
    public const int RoundingQuantum = 10_000;

    /// <summary>ADR-002 archive target. Reference only; not copied onto note/sync.</summary>
    public const double ArchiveTargetMs = EncryptedArchivePbkdf2Benchmark.TargetMilliseconds;

    /// <summary>
    /// Experimental wall-clock points for a <em>single</em> PBKDF2 on this host.
    /// Not derived from search p95, editor-open, or any other product path.
    /// Not accepted defaults.
    /// </summary>
    public static readonly IReadOnlyList<double> ExperimentalApproximateMs = new[] { 50d, 100d, 150d };

    public const int SyncScalePackageCount = 1;

    public static readonly IReadOnlyList<int> SyncScaleAttachmentCounts = new[] { 0, 1, 10, 100 };

    public const string LinearUniqueSaltModel =
        "O(packages + attachments): each QNSP/QNBA envelope has its own salt and DeriveKey";

    public const string MeasurementAcceptedNoChangeStrategy = "measurement-accepted-no-kdf-change";
    public const string Argon2idSeparateEvaluation =
        "adr-016-isolated-spike-not-production-default-not-sole-path";

    /// <summary>Fixed dummy password for local timing only. Not a user secret.</summary>
    internal const string DummyPassword = "qn-pbkdf2-bench";

    public static readonly IReadOnlyList<Pbkdf2CircuitKind> AllCircuits = new[]
    {
        Pbkdf2CircuitKind.Note,
        Pbkdf2CircuitKind.Sync,
        Pbkdf2CircuitKind.Archive
    };

    public static Pbkdf2CostBenchmarkResult Run(Pbkdf2CostBenchmarkOptions? options = null)
    {
        Pbkdf2CostBenchmarkOptions o = options ?? new Pbkdf2CostBenchmarkOptions();
        if (o.Warmups < 1 || o.Repeats < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Нужны положительные warmups и repeats.");
        }

        ValidateProbeIterations(o.ProbeIterations);

        IReadOnlyList<Pbkdf2CircuitKind> circuits = o.Circuits.Count == 0 ? AllCircuits : o.Circuits;
        byte[] salt = CreateDummySalt();
        try
        {
            double probeMedian = MeasureMedianMs(DummyPassword, salt, o.ProbeIterations, o.Warmups, o.Repeats);
            var reports = new List<Pbkdf2CircuitReport>(circuits.Count);
            foreach (Pbkdf2CircuitKind kind in circuits.Distinct())
            {
                reports.Add(MeasureCircuit(kind, salt, o, probeMedian));
            }

            return new Pbkdf2CostBenchmarkResult
            {
                MeasuredAtUtc = DateTimeOffset.UtcNow,
                OsDescription = RuntimeInformation.OSDescription,
                FrameworkDescription = RuntimeInformation.FrameworkDescription,
                ProcessorCount = Environment.ProcessorCount,
                ProcessorIdentifier = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "(unknown)",
                Warmups = o.Warmups,
                Repeats = o.Repeats,
                ProbeIterations = o.ProbeIterations,
                ProbeMedianMs = probeMedian,
                Algorithm = KdfAlgorithmIds.Pbkdf2HmacSha256,
                Primitive = "Rfc2898DeriveBytes.Pbkdf2",
                DkLen = NoteCryptoService.KeyByteSize,
                SaltLen = KdfDescriptorConstants.SaltByteSize,
                Circuits = reports,
                CloudEnvelopeDos = BuildCloudDosNote(),
                Recommendation = BuildRecommendation()
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    public static void WriteReport(Pbkdf2CostBenchmarkResult result, TextWriter stdout, bool jsonOnly)
    {
        if (!jsonOnly)
        {
            WriteTextReport(result, stdout);
            stdout.WriteLine("JsonReport:");
        }

        stdout.WriteLine(FormatJson(result));
    }

    public static string FormatJson(Pbkdf2CostBenchmarkResult result)
    {
        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        return JsonSerializer.Serialize(result, jsonOptions);
    }

    public static double EstimateLinearCycleMs(double perDerivationMedianMs, int packageCount, int attachmentCount)
    {
        if (perDerivationMedianMs < 0 || double.IsNaN(perDerivationMedianMs) || double.IsInfinity(perDerivationMedianMs))
        {
            throw new ArgumentOutOfRangeException(nameof(perDerivationMedianMs));
        }

        if (packageCount < 0 || attachmentCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(packageCount));
        }

        int derivations = packageCount + attachmentCount;
        if (derivations < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(packageCount), "Need at least one envelope.");
        }

        return perDerivationMedianMs * derivations;
    }

    public static IReadOnlyList<Pbkdf2SyncCycleScaleRow> BuildSyncCycleScale(double perDerivationMedianMs, int iterationsPerEnvelope)
    {
        var rows = new List<Pbkdf2SyncCycleScaleRow>(SyncScaleAttachmentCounts.Count);
        foreach (int attachments in SyncScaleAttachmentCounts)
        {
            int derivations = SyncScalePackageCount + attachments;
            rows.Add(new Pbkdf2SyncCycleScaleRow
            {
                PackageCount = SyncScalePackageCount,
                AttachmentCount = attachments,
                DerivationCount = derivations,
                IterationsPerEnvelope = iterationsPerEnvelope,
                EstimatedMedianMs = EstimateLinearCycleMs(perDerivationMedianMs, SyncScalePackageCount, attachments),
                Model = LinearUniqueSaltModel
            });
        }

        return rows;
    }

    public static int SuggestIterations(
        int probeIterations,
        double probeMedianMs,
        double targetMs,
        int minIterations,
        int maxIterations,
        int roundingQuantum = RoundingQuantum)
    {
        if (roundingQuantum < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(roundingQuantum));
        }

        if (minIterations < 1 || maxIterations < minIterations)
        {
            throw new ArgumentOutOfRangeException(nameof(minIterations));
        }

        if (probeMedianMs <= 0)
        {
            return minIterations;
        }

        double raw = probeIterations * (targetMs / probeMedianMs);
        if (double.IsNaN(raw) || double.IsInfinity(raw) || raw <= 0)
        {
            return minIterations;
        }

        if (raw >= maxIterations)
        {
            return maxIterations;
        }

        int rounded = (int)(Math.Floor(raw / roundingQuantum) * roundingQuantum);
        if (rounded < minIterations)
        {
            return minIterations;
        }

        if (rounded > maxIterations)
        {
            return maxIterations;
        }

        return rounded;
    }

    public static int ClampExperimentalIterations(int suggested, int minIterations, int maxIterations)
    {
        if (suggested < minIterations)
        {
            return minIterations;
        }

        if (suggested > maxIterations)
        {
            return maxIterations;
        }

        return suggested;
    }

    public static bool TryParseArgs(string[]? args, out Pbkdf2CostBenchmarkOptions options, out string error)
    {
        options = new Pbkdf2CostBenchmarkOptions();
        error = string.Empty;
        string[] list = args ?? Array.Empty<string>();

        var circuits = new List<Pbkdf2CircuitKind>();
        int warmups = DefaultWarmups;
        int repeats = DefaultRepeats;
        int probe = DefaultProbeIterations;
        bool jsonOnly = false;
        bool measureCurrent = true;
        bool experimental = true;
        bool help = false;

        for (int i = 0; i < list.Length; i++)
        {
            string arg = list[i];
            if (LooksLikeUserSecretOrDatabasePath(arg))
            {
                error = "Benchmark does not accept file paths, databases or secrets.";
                return false;
            }

            if (string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "/?", StringComparison.OrdinalIgnoreCase))
            {
                help = true;
                continue;
            }

            if (string.Equals(arg, "--json", StringComparison.OrdinalIgnoreCase))
            {
                jsonOnly = true;
                continue;
            }

            if (string.Equals(arg, "--skip-current", StringComparison.OrdinalIgnoreCase))
            {
                measureCurrent = false;
                continue;
            }

            if (string.Equals(arg, "--skip-confirm", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--skip-experimental", StringComparison.OrdinalIgnoreCase))
            {
                experimental = false;
                continue;
            }

            if (string.Equals(arg, "--circuit", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= list.Length || !TryParseCircuit(list[++i], out Pbkdf2CircuitKind kind, out bool all))
                {
                    error = "Unknown or missing --circuit value.";
                    return false;
                }

                if (all)
                {
                    circuits.Clear();
                    circuits.AddRange(AllCircuits);
                }
                else
                {
                    circuits.Add(kind);
                }

                continue;
            }

            if (string.Equals(arg, "--warmups", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadPositiveInt(list, ref i, out warmups))
                {
                    error = "Invalid --warmups.";
                    return false;
                }

                continue;
            }

            if (string.Equals(arg, "--repeats", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadPositiveInt(list, ref i, out repeats))
                {
                    error = "Invalid --repeats.";
                    return false;
                }

                continue;
            }

            if (string.Equals(arg, "--probe", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadPositiveInt(list, ref i, out probe))
                {
                    error = "Invalid --probe.";
                    return false;
                }

                continue;
            }

            error = "Unknown argument.";
            return false;
        }

        if (circuits.Count == 0)
        {
            circuits.AddRange(AllCircuits);
        }

        options = new Pbkdf2CostBenchmarkOptions
        {
            Circuits = circuits,
            Warmups = warmups,
            Repeats = repeats,
            ProbeIterations = probe,
            JsonOnly = jsonOnly,
            MeasureCurrent = measureCurrent,
            MeasureExperimentalPoints = experimental,
            Help = help
        };
        return true;
    }

    public static Pbkdf2CloudDosNote BuildCloudDosNote()
    {
        return new Pbkdf2CloudDosNote
        {
            MaxIterationsPerUntrustedEnvelope = KdfDescriptorConstants.MaxPbkdf2Iterations,
            CostModel = LinearUniqueSaltModel,
            BatchKdfBudgetImplemented = true,
            CycleBudgetIterations = UntrustedInboundKdfWorkBudget.DefaultCycleBudgetIterations,
            RequiredBeforeRaisingN =
                "Untrusted cloud envelopes accept up to 5_000_000 iterations each before derivation. "
                + "A hostile or large batch is cumulative CPU: N envelopes × per-envelope PBKDF2. "
                + "A per-sync-cycle inbound KDF budget (20_000_000 claimed iterations) now fail-closes "
                + "before PBKDF2. Per-envelope max and production defaults are unchanged. "
                + "A PBKDF2 raise remains not accepted. Argon2id is an isolated Tools spike (ADR-016), not a production default; the iteration budget does not map to Argon2id memory+time."
        };
    }

    /// <summary>
    /// Conservative, input-independent recommendation: measurement may be recorded, but this stage
    /// accepts neither a PBKDF2 raise nor Argon2id.
    /// </summary>
    public static Pbkdf2CostRecommendation BuildRecommendation()
    {
        return new Pbkdf2CostRecommendation
        {
            Strategy = MeasurementAcceptedNoChangeStrategy,
            Pbkdf2RaiseAccepted = false,
            Argon2idAccepted = false,
            ChangeProductionConstantsInThisStage = false,
            ArchiveShippingIterationsUnchanged = EncryptedArchiveConstants.DefaultPbkdf2IterationsForNewArchives,
            Argon2id = Argon2idSeparateEvaluation,
            Summary =
                "Measurement of current production N is accepted as a local observation only. "
                + "Neither a PBKDF2 iteration raise nor Argon2id is accepted. Experimental timing points "
                + "(including ~150 ms on this host) are not product-path targets and not global defaults; "
                + "low-end machines are not covered. Sync cost is O(packages + attachments), so no single "
                + "sync candidate is recommended. 1 package + 100 attachments at experimental 490_000 would "
                + "claim 49_490_000 iterations against the 20_000_000 inbound cycle budget. Archive shipping "
                + "N stays. Argon2id (memory-hard) is an isolated Tools spike (ADR-016), not a production "
                + "default, and is not claimed to be the only path. Production constants stay unchanged."
        };
    }

    internal static void DeriveOnce(string password, byte[] salt, int iterations)
    {
        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            byte[] key = Rfc2898DeriveBytes.Pbkdf2(
                passwordBytes,
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                NoteCryptoService.KeyByteSize);
            CryptographicOperations.ZeroMemory(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    internal static bool LooksLikeUserSecretOrDatabasePath(string arg)
    {
        if (arg.IndexOfAny(new[] { '\\', '/' }) >= 0)
        {
            return true;
        }

        string lower = arg.ToLowerInvariant();
        return lower.EndsWith(".db", StringComparison.Ordinal)
            || lower.EndsWith(".qnar", StringComparison.Ordinal)
            || lower.EndsWith(".json", StringComparison.Ordinal)
            || lower.Contains("password", StringComparison.Ordinal)
            || lower.Contains("dpapi", StringComparison.Ordinal);
    }

    private static Pbkdf2CircuitReport MeasureCircuit(
        Pbkdf2CircuitKind kind,
        byte[] salt,
        Pbkdf2CostBenchmarkOptions options,
        double probeMedianMs)
    {
        DescribeCircuit(kind, out string id, out int currentN, out string costModel, out int minN, out int maxN);

        Pbkdf2SampleStats? current = null;
        if (options.MeasureCurrent)
        {
            current = MeasureStats(salt, currentN, options.Warmups, options.Repeats);
        }

        var experimental = new List<Pbkdf2ExperimentalPoint>();
        if (options.MeasureExperimentalPoints && kind != Pbkdf2CircuitKind.Archive)
        {
            foreach (double approxMs in ExperimentalApproximateMs)
            {
                int n = ClampExperimentalIterations(
                    SuggestIterations(options.ProbeIterations, probeMedianMs, approxMs, minN, maxN),
                    minN,
                    maxN);
                experimental.Add(new Pbkdf2ExperimentalPoint
                {
                    Id = id + "-approx-" + ((int)approxMs).ToString(CultureInfo.InvariantCulture) + "ms",
                    Role = "experimental-timing-point-not-a-default",
                    ApproximateTargetMs = approxMs,
                    Iterations = n,
                    Measured = MeasureStats(salt, n, options.Warmups, options.Repeats)
                });
            }
        }

        IReadOnlyList<Pbkdf2SyncCycleScaleRow> scale = Array.Empty<Pbkdf2SyncCycleScaleRow>();
        if (kind == Pbkdf2CircuitKind.Sync)
        {
            double perMs = current != null
                ? current.MedianMs
                : ExtrapolateMs(currentN, options.ProbeIterations, probeMedianMs);
            scale = BuildSyncCycleScale(perMs, currentN);
        }

        return new Pbkdf2CircuitReport
        {
            Id = id,
            CurrentProductionIterations = currentN,
            CostModel = costModel,
            Current = current,
            ExperimentalPoints = experimental,
            CycleScale = scale
        };
    }

    private static double ExtrapolateMs(int iterations, int probeIterations, double probeMedianMs)
        => probeMedianMs * (iterations / (double)probeIterations);

    private static void DescribeCircuit(
        Pbkdf2CircuitKind kind,
        out string id,
        out int currentN,
        out string costModel,
        out int minN,
        out int maxN)
    {
        maxN = KdfDescriptorConstants.MaxPbkdf2Iterations;
        minN = RoundingQuantum;
        switch (kind)
        {
            case Pbkdf2CircuitKind.Note:
                id = "note";
                currentN = NoteCryptoService.DefaultIterationsConst;
                costModel = "One PBKDF2 per note unlock; session then caches the key. Not search-as-you-type.";
                break;
            case Pbkdf2CircuitKind.Sync:
                id = "sync";
                currentN = SyncCryptoService.DefaultIterations;
                costModel = LinearUniqueSaltModel;
                break;
            default:
                id = "archive";
                currentN = EncryptedArchiveConstants.DefaultPbkdf2IterationsForNewArchives;
                minN = EncryptedArchiveConstants.MinKdfIterationsUntrusted;
                costModel = "One PBKDF2 for the archive password wrap (ADR-002 B4 reference). Shipping N unchanged.";
                break;
        }
    }

    private static Pbkdf2SampleStats MeasureStats(byte[] salt, int iterations, int warmups, int repeats)
    {
        ValidateIterationBudget(iterations);
        double[] samples = MeasureSamplesMs(DummyPassword, salt, iterations, warmups, repeats);
        return new Pbkdf2SampleStats
        {
            Iterations = iterations,
            MedianMs = Median(samples),
            MinMs = samples.Min(),
            MaxMs = samples.Max()
        };
    }

    private static double MeasureMedianMs(string password, byte[] salt, int iterations, int warmups, int repeats)
        => Median(MeasureSamplesMs(password, salt, iterations, warmups, repeats));

    private static double[] MeasureSamplesMs(string password, byte[] salt, int iterations, int warmups, int repeats)
    {
        ValidateIterationBudget(iterations);
        for (int i = 0; i < warmups; i++)
        {
            DeriveOnce(password, salt, iterations);
        }

        var samples = new double[repeats];
        var sw = new Stopwatch();
        for (int i = 0; i < repeats; i++)
        {
            sw.Restart();
            DeriveOnce(password, salt, iterations);
            sw.Stop();
            samples[i] = sw.Elapsed.TotalMilliseconds;
        }

        return samples;
    }

    private static double Median(IReadOnlyList<double> values)
    {
        double[] sorted = values.OrderBy(v => v).ToArray();
        int mid = sorted.Length / 2;
        if (sorted.Length % 2 == 1)
        {
            return sorted[mid];
        }

        return (sorted[mid - 1] + sorted[mid]) / 2d;
    }

    private static byte[] CreateDummySalt()
    {
        var salt = new byte[KdfDescriptorConstants.SaltByteSize];
        for (int i = 0; i < salt.Length; i++)
        {
            salt[i] = (byte)(0xA5 ^ (byte)i);
        }

        return salt;
    }

    private static void ValidateProbeIterations(int iterations)
    {
        if (iterations < RoundingQuantum || iterations > KdfDescriptorConstants.MaxPbkdf2Iterations)
        {
            throw new ArgumentOutOfRangeException(nameof(iterations), "Probe iterations are out of bounds.");
        }
    }

    private static void ValidateIterationBudget(int iterations)
    {
        KdfDescriptor.ValidateIterations(iterations, KdfDescriptorLimits.LocalEnvelope);
    }

    private static bool TryParseCircuit(string raw, out Pbkdf2CircuitKind kind, out bool all)
    {
        kind = Pbkdf2CircuitKind.Note;
        all = false;
        if (string.Equals(raw, "all", StringComparison.OrdinalIgnoreCase))
        {
            all = true;
            return true;
        }

        if (string.Equals(raw, "note", StringComparison.OrdinalIgnoreCase))
        {
            kind = Pbkdf2CircuitKind.Note;
            return true;
        }

        if (string.Equals(raw, "sync", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "blob", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "sync/blob", StringComparison.OrdinalIgnoreCase))
        {
            kind = Pbkdf2CircuitKind.Sync;
            return true;
        }

        if (string.Equals(raw, "archive", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "archive-reference", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "qnar", StringComparison.OrdinalIgnoreCase))
        {
            kind = Pbkdf2CircuitKind.Archive;
            return true;
        }

        return false;
    }

    private static bool TryReadPositiveInt(string[] list, ref int i, out int value)
    {
        value = 0;
        if (i + 1 >= list.Length)
        {
            return false;
        }

        if (!int.TryParse(list[++i], NumberStyles.None, CultureInfo.InvariantCulture, out value) || value < 1)
        {
            return false;
        }

        return true;
    }

    private static void WriteTextReport(Pbkdf2CostBenchmarkResult result, TextWriter stdout)
    {
        stdout.WriteLine("QuickNotes PBKDF2-HMAC-SHA256 local cost benchmark (note / sync / archive-reference)");
        stdout.WriteLine("This result is for this machine and runtime only. It is not a universal cost.");
        stdout.WriteLine("No user database, DPAPI, live profile, cloud objects, or real passwords were used.");
        stdout.WriteLine("Experimental timing points are not product-path targets and not accepted defaults.");
        stdout.WriteLine("DateUtc: " + result.MeasuredAtUtc.ToString("o", CultureInfo.InvariantCulture));
        stdout.WriteLine("OS: " + result.OsDescription);
        stdout.WriteLine("Runtime: " + result.FrameworkDescription);
        stdout.WriteLine("ProcessorCount: " + result.ProcessorCount.ToString(CultureInfo.InvariantCulture));
        stdout.WriteLine("ProcessorIdentifier: " + result.ProcessorIdentifier);
        stdout.WriteLine("Algorithm: " + result.Algorithm);
        stdout.WriteLine("Primitive: " + result.Primitive + " SHA-256 dkLen " + result.DkLen.ToString(CultureInfo.InvariantCulture));
        stdout.WriteLine("SaltLen: " + result.SaltLen.ToString(CultureInfo.InvariantCulture));
        stdout.WriteLine("Password: fixed dummy ASCII (not a user secret)");
        stdout.WriteLine("Warmups: " + result.Warmups.ToString(CultureInfo.InvariantCulture));
        stdout.WriteLine("Repeats: " + result.Repeats.ToString(CultureInfo.InvariantCulture));
        stdout.WriteLine("ProbeIterations: " + result.ProbeIterations.ToString(CultureInfo.InvariantCulture));
        stdout.WriteLine("ProbeMedianMs: " + FormatMs(result.ProbeMedianMs));
        foreach (Pbkdf2CircuitReport circuit in result.Circuits)
        {
            stdout.WriteLine("Circuit: " + circuit.Id);
            stdout.WriteLine("  CurrentProductionIterations: " + circuit.CurrentProductionIterations.ToString(CultureInfo.InvariantCulture));
            stdout.WriteLine("  CostModel: " + circuit.CostModel);
            if (circuit.Current != null)
            {
                stdout.WriteLine("  CurrentMedianMs: " + FormatMs(circuit.Current.MedianMs));
                stdout.WriteLine("  CurrentMinMs: " + FormatMs(circuit.Current.MinMs));
                stdout.WriteLine("  CurrentMaxMs: " + FormatMs(circuit.Current.MaxMs));
            }

            foreach (Pbkdf2ExperimentalPoint point in circuit.ExperimentalPoints)
            {
                stdout.WriteLine("  ExperimentalPoint: " + point.Id);
                stdout.WriteLine("    Role: " + point.Role);
                stdout.WriteLine("    ApproximateTargetMs: " + FormatMs(point.ApproximateTargetMs));
                stdout.WriteLine("    Iterations: " + point.Iterations.ToString(CultureInfo.InvariantCulture));
                if (point.Measured != null)
                {
                    stdout.WriteLine("    MeasuredMedianMs: " + FormatMs(point.Measured.MedianMs));
                    stdout.WriteLine("    MeasuredMinMs: " + FormatMs(point.Measured.MinMs));
                    stdout.WriteLine("    MeasuredMaxMs: " + FormatMs(point.Measured.MaxMs));
                }
            }

            foreach (Pbkdf2SyncCycleScaleRow row in circuit.CycleScale)
            {
                stdout.WriteLine(
                    "  SyncCycleScale: packages="
                    + row.PackageCount.ToString(CultureInfo.InvariantCulture)
                    + " attachments="
                    + row.AttachmentCount.ToString(CultureInfo.InvariantCulture)
                    + " derivations="
                    + row.DerivationCount.ToString(CultureInfo.InvariantCulture)
                    + " estimatedMedianMs="
                    + FormatMs(row.EstimatedMedianMs)
                    + " atN="
                    + row.IterationsPerEnvelope.ToString(CultureInfo.InvariantCulture));
            }
        }

        stdout.WriteLine("CloudDosMaxIterationsPerEnvelope: " + result.CloudEnvelopeDos.MaxIterationsPerUntrustedEnvelope.ToString(CultureInfo.InvariantCulture));
        stdout.WriteLine("CloudDosCostModel: " + result.CloudEnvelopeDos.CostModel);
        stdout.WriteLine("CloudDosBatchKdfBudgetImplemented: " + (result.CloudEnvelopeDos.BatchKdfBudgetImplemented ? "true" : "false"));
        stdout.WriteLine("CloudDosCycleBudgetIterations: " + result.CloudEnvelopeDos.CycleBudgetIterations.ToString(CultureInfo.InvariantCulture));
        stdout.WriteLine("CloudDosRequiredBeforeRaisingN: " + result.CloudEnvelopeDos.RequiredBeforeRaisingN);
        stdout.WriteLine("RecommendationStrategy: " + result.Recommendation.Strategy);
        stdout.WriteLine("Pbkdf2RaiseAccepted: false");
        stdout.WriteLine("Argon2idAccepted: false");
        stdout.WriteLine("ChangeProductionConstantsInThisStage: false");
        stdout.WriteLine("ArchiveShippingIterationsUnchanged: " + result.Recommendation.ArchiveShippingIterationsUnchanged.ToString(CultureInfo.InvariantCulture));
        stdout.WriteLine("Argon2id: " + result.Recommendation.Argon2id);
        stdout.WriteLine("Recommendation: " + result.Recommendation.Summary);
        stdout.WriteLine("NotUniversal: true");
    }

    private static string FormatMs(double ms)
        => ms.ToString("0.000", CultureInfo.InvariantCulture);
}
