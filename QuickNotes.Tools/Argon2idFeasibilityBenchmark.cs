using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Konscious.Security.Cryptography;
using QuickNotes.App.Services.Crypto;
using QuickNotes.App.Services.EncryptedArchive;
using QuickNotes.App.Services.NoteProtection;
using QuickNotes.App.Services.Sync;

namespace QuickNotes.Tools;

/// <summary>
/// Isolated Argon2id feasibility spike (ADR-016). Dummy password/salt only.
/// Not a production KDF, not a wire-format change, not a default for new envelopes.
/// </summary>
public sealed class Argon2idFeasibilityOptions
{
    public IReadOnlyList<string> ProfileIds { get; init; } = Argon2idFeasibilityBenchmark.DefaultPublishedProfileIds;

    public int Warmups { get; init; } = Argon2idFeasibilityBenchmark.DefaultWarmups;

    public int Repeats { get; init; } = Argon2idFeasibilityBenchmark.DefaultRepeats;

    public bool JsonOnly { get; init; }

    public bool Help { get; init; }
}

public sealed class Argon2idProfile
{
    public required string Id { get; init; }

    public required string Source { get; init; }

    public required string Role { get; init; }

    public required int MemoryKiB { get; init; }

    public required int Iterations { get; init; }

    public required int Parallelism { get; init; }

    public required int SaltLen { get; init; }

    public required int DkLen { get; init; }
}

public sealed class Argon2idProfileMeasurement
{
    public required Argon2idProfile Profile { get; init; }

    public required double MedianMs { get; init; }

    public required double MinMs { get; init; }

    public required double MaxMs { get; init; }

    public required long WorkingSetDeltaBytes { get; init; }

    public IReadOnlyList<Pbkdf2SyncCycleScaleRow> SyncCycleTimeScale { get; init; } =
        Array.Empty<Pbkdf2SyncCycleScaleRow>();
}

public sealed class Argon2idFeasibilityRecommendation
{
    public required string Strategy { get; init; }

    public required bool Pbkdf2RaiseAccepted { get; init; }

    public required bool Argon2idProductionDefaultAccepted { get; init; }

    public required bool ChangeProductionConstantsInThisStage { get; init; }

    public required bool ReadyForLaterMigration { get; init; }

    public required string Summary { get; init; }
}

public sealed class Argon2idFeasibilityResult
{
    public required DateTimeOffset MeasuredAtUtc { get; init; }

    public required string OsDescription { get; init; }

    public required string FrameworkDescription { get; init; }

    public required int ProcessorCount { get; init; }

    public required string ProcessorIdentifier { get; init; }

    public required int Warmups { get; init; }

    public required int Repeats { get; init; }

    public required string Algorithm { get; init; }

    public required string Primitive { get; init; }

    public required string Library { get; init; }

    public required IReadOnlyList<Argon2idProfileMeasurement> Profiles { get; init; }

    public required Argon2idFeasibilityRecommendation Recommendation { get; init; }

    public required string ProductionPbkdf2Unchanged { get; init; }
}

public static class Argon2idFeasibilityBenchmark
{
    public const int DefaultWarmups = 1;
    public const int DefaultRepeats = 3;
    public const int MaxMemoryKiB = 131_072;
    public const int MaxIterations = 10;
    public const int MaxParallelism = 16;
    public const string AlgorithmId = "ARGON2ID";
    public const string PrimitiveName = "Konscious.Security.Cryptography.Argon2id";
    public const string LibraryName = "Konscious.Security.Cryptography.Argon2 1.3.1";
    public const string IsolatedSpikeStrategy = "adr-016-isolated-argon2id-spike-not-production-default";
    public const string LaboratoryTinyProfileId = "laboratory-tiny";
    public const string RoleExperimentalNotDefault = "experimental-timing-point-not-a-default";

    internal const string DummyPassword = "qn-argon2id-bench";

    public static readonly Argon2idProfile LaboratoryTiny = new()
    {
        Id = LaboratoryTinyProfileId,
        Source = "laboratory-only-proves-the-primitive; not OWASP/RFC",
        Role = RoleExperimentalNotDefault,
        MemoryKiB = 8,
        Iterations = 1,
        Parallelism = 1,
        SaltLen = KdfDescriptorConstants.SaltByteSize,
        DkLen = NoteCryptoService.KeyByteSize
    };

    public static readonly Argon2idProfile OwaspInteractive2023 = new()
    {
        Id = "owasp-interactive-2023",
        Source = "OWASP Password Storage Cheat Sheet (2023) Argon2id m=19456 KiB, t=2, p=1",
        Role = RoleExperimentalNotDefault,
        MemoryKiB = 19_456,
        Iterations = 2,
        Parallelism = 1,
        SaltLen = KdfDescriptorConstants.SaltByteSize,
        DkLen = NoteCryptoService.KeyByteSize
    };

    public static readonly Argon2idProfile OwaspHighMemory2023 = new()
    {
        Id = "owasp-high-memory-2023",
        Source = "OWASP Password Storage Cheat Sheet (2023) Argon2id m=47104 KiB, t=1, p=1",
        Role = RoleExperimentalNotDefault,
        MemoryKiB = 47_104,
        Iterations = 1,
        Parallelism = 1,
        SaltLen = KdfDescriptorConstants.SaltByteSize,
        DkLen = NoteCryptoService.KeyByteSize
    };

    public static readonly Argon2idProfile Rfc9106Second = new()
    {
        Id = "rfc9106-second-recommended",
        Source = "RFC 9106 second recommended (m=65536 KiB, t=3, p=4)",
        Role = RoleExperimentalNotDefault,
        MemoryKiB = 65_536,
        Iterations = 3,
        Parallelism = 4,
        SaltLen = KdfDescriptorConstants.SaltByteSize,
        DkLen = NoteCryptoService.KeyByteSize
    };

    public static readonly IReadOnlyList<Argon2idProfile> PublishedProfiles = new[]
    {
        OwaspInteractive2023,
        OwaspHighMemory2023,
        Rfc9106Second
    };

    public static readonly IReadOnlyList<string> DefaultPublishedProfileIds = new[]
    {
        OwaspInteractive2023.Id,
        OwaspHighMemory2023.Id,
        Rfc9106Second.Id
    };

    public static Argon2idFeasibilityResult Run(Argon2idFeasibilityOptions? options = null)
    {
        Argon2idFeasibilityOptions o = options ?? new Argon2idFeasibilityOptions();
        if (o.Warmups < 1 || o.Repeats < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Нужны положительные warmups и repeats.");
        }

        IReadOnlyList<Argon2idProfile> profiles = ResolveProfiles(o.ProfileIds);
        byte[] salt = CreateDummySalt();
        try
        {
            var measurements = new List<Argon2idProfileMeasurement>(profiles.Count);
            foreach (Argon2idProfile profile in profiles)
            {
                measurements.Add(MeasureProfile(profile, salt, o.Warmups, o.Repeats));
            }

            return new Argon2idFeasibilityResult
            {
                MeasuredAtUtc = DateTimeOffset.UtcNow,
                OsDescription = RuntimeInformation.OSDescription,
                FrameworkDescription = RuntimeInformation.FrameworkDescription,
                ProcessorCount = Environment.ProcessorCount,
                ProcessorIdentifier = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "(unknown)",
                Warmups = o.Warmups,
                Repeats = o.Repeats,
                Algorithm = AlgorithmId,
                Primitive = PrimitiveName,
                Library = LibraryName,
                Profiles = measurements,
                Recommendation = BuildRecommendation(),
                ProductionPbkdf2Unchanged =
                    "note=" + NoteCryptoService.DefaultIterationsConst.ToString(CultureInfo.InvariantCulture)
                    + " sync=" + SyncCryptoService.DefaultIterations.ToString(CultureInfo.InvariantCulture)
                    + " archive=" + EncryptedArchiveConstants.DefaultPbkdf2IterationsForNewArchives.ToString(CultureInfo.InvariantCulture)
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    public static Argon2idFeasibilityRecommendation BuildRecommendation()
    {
        return new Argon2idFeasibilityRecommendation
        {
            Strategy = IsolatedSpikeStrategy,
            Pbkdf2RaiseAccepted = false,
            Argon2idProductionDefaultAccepted = false,
            ChangeProductionConstantsInThisStage = false,
            ReadyForLaterMigration = true,
            Summary =
                "PBKDF2 raise is not accepted: experimental ~50 ms note/sync N is not a global default, "
                + "low-end hardware was not measured, and 1 package + 100 attachments at 490_000 would claim "
                + "49_490_000 inbound iterations against the 20_000_000 cycle budget. Archive shipping "
                + "2_490_000 stays in the measured ~250 ms band. Argon2id is not a production default. "
                + "This isolated Tools spike plus ADR-016 is the adoption package for a later migration: "
                + "new descriptor/alg id, mixed-fleet readers that already fail closed on unknown KDF, "
                + "memory+time untrusted caps (iteration budget does not map), and an independent review. "
                + "Opening or syncing existing ciphertext must not rewrite it."
        };
    }

    public static void WriteReport(Argon2idFeasibilityResult result, TextWriter stdout, bool jsonOnly)
    {
        if (!jsonOnly)
        {
            WriteTextReport(result, stdout);
            stdout.WriteLine("JsonReport:");
        }

        stdout.WriteLine(FormatJson(result));
    }

    public static string FormatJson(Argon2idFeasibilityResult result)
    {
        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        return JsonSerializer.Serialize(result, jsonOptions);
    }

    public static bool TryParseArgs(string[]? args, out Argon2idFeasibilityOptions options, out string error)
    {
        options = new Argon2idFeasibilityOptions();
        error = string.Empty;
        string[] list = args ?? Array.Empty<string>();

        var profileIds = new List<string>();
        int warmups = DefaultWarmups;
        int repeats = DefaultRepeats;
        bool jsonOnly = false;
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

            if (string.Equals(arg, "--profile", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= list.Length || !TryParseProfileToken(list[++i], profileIds))
                {
                    error = "Unknown or missing --profile value.";
                    return false;
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

            error = "Unknown argument.";
            return false;
        }

        if (profileIds.Count == 0)
        {
            profileIds.AddRange(DefaultPublishedProfileIds);
        }

        options = new Argon2idFeasibilityOptions
        {
            ProfileIds = profileIds,
            Warmups = warmups,
            Repeats = repeats,
            JsonOnly = jsonOnly,
            Help = help
        };
        return true;
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

    internal static byte[] DeriveOnce(string password, byte[] salt, Argon2idProfile profile)
    {
        ValidateProfile(profile);
        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            using var argon2 = new Argon2id(passwordBytes)
            {
                Salt = salt,
                DegreeOfParallelism = profile.Parallelism,
                Iterations = profile.Iterations,
                MemorySize = profile.MemoryKiB
            };
            return argon2.GetBytes(profile.DkLen);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    private static Argon2idProfileMeasurement MeasureProfile(
        Argon2idProfile profile,
        byte[] salt,
        int warmups,
        int repeats)
    {
        ValidateProfile(profile);
        long before = Process.GetCurrentProcess().WorkingSet64;
        for (int i = 0; i < warmups; i++)
        {
            byte[] warmupKey = DeriveOnce(DummyPassword, salt, profile);
            CryptographicOperations.ZeroMemory(warmupKey);
        }

        var samples = new double[repeats];
        var sw = new Stopwatch();
        for (int i = 0; i < repeats; i++)
        {
            sw.Restart();
            byte[] key = DeriveOnce(DummyPassword, salt, profile);
            sw.Stop();
            CryptographicOperations.ZeroMemory(key);
            samples[i] = sw.Elapsed.TotalMilliseconds;
        }

        long after = Process.GetCurrentProcess().WorkingSet64;
        double median = Median(samples);
        return new Argon2idProfileMeasurement
        {
            Profile = profile,
            MedianMs = median,
            MinMs = samples.Min(),
            MaxMs = samples.Max(),
            WorkingSetDeltaBytes = Math.Max(0, after - before),
            SyncCycleTimeScale = Pbkdf2CostBenchmark.BuildSyncCycleScale(median, 0)
        };
    }

    private static IReadOnlyList<Argon2idProfile> ResolveProfiles(IReadOnlyList<string> ids)
    {
        var list = new List<Argon2idProfile>();
        foreach (string id in ids.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            list.Add(FindProfile(id));
        }

        if (list.Count == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ids), "Need at least one Argon2id profile.");
        }

        return list;
    }

    private static Argon2idProfile FindProfile(string id)
    {
        if (string.Equals(id, LaboratoryTiny.Id, StringComparison.OrdinalIgnoreCase))
        {
            return LaboratoryTiny;
        }

        foreach (Argon2idProfile profile in PublishedProfiles)
        {
            if (string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return profile;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(id), "Unknown Argon2id profile.");
    }

    private static bool TryParseProfileToken(string raw, List<string> ids)
    {
        if (string.Equals(raw, "all", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "published", StringComparison.OrdinalIgnoreCase))
        {
            ids.Clear();
            ids.AddRange(DefaultPublishedProfileIds);
            return true;
        }

        if (string.Equals(raw, LaboratoryTiny.Id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "tiny", StringComparison.OrdinalIgnoreCase))
        {
            ids.Add(LaboratoryTiny.Id);
            return true;
        }

        foreach (Argon2idProfile profile in PublishedProfiles)
        {
            if (string.Equals(profile.Id, raw, StringComparison.OrdinalIgnoreCase))
            {
                ids.Add(profile.Id);
                return true;
            }
        }

        return false;
    }

    private static void ValidateProfile(Argon2idProfile profile)
    {
        if (profile.MemoryKiB < 8 || profile.MemoryKiB > MaxMemoryKiB)
        {
            throw new ArgumentOutOfRangeException(nameof(profile), "Argon2id memory is out of spike bounds.");
        }

        if (profile.Iterations < 1 || profile.Iterations > MaxIterations)
        {
            throw new ArgumentOutOfRangeException(nameof(profile), "Argon2id iterations are out of spike bounds.");
        }

        if (profile.Parallelism < 1 || profile.Parallelism > MaxParallelism)
        {
            throw new ArgumentOutOfRangeException(nameof(profile), "Argon2id parallelism is out of spike bounds.");
        }

        if (profile.SaltLen != KdfDescriptorConstants.SaltByteSize || profile.DkLen != NoteCryptoService.KeyByteSize)
        {
            throw new ArgumentOutOfRangeException(nameof(profile), "Argon2id salt/dkLen must match production sizes.");
        }
    }

    private static byte[] CreateDummySalt()
    {
        var salt = new byte[KdfDescriptorConstants.SaltByteSize];
        for (int i = 0; i < salt.Length; i++)
        {
            salt[i] = (byte)(0x5A ^ (byte)i);
        }

        return salt;
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

    private static void WriteTextReport(Argon2idFeasibilityResult result, TextWriter stdout)
    {
        stdout.WriteLine("QuickNotes Argon2id isolated feasibility spike (ADR-016)");
        stdout.WriteLine("This is not a production KDF and not an accepted default.");
        stdout.WriteLine("This result is for this machine and runtime only. It is not a universal cost.");
        stdout.WriteLine("No user database, DPAPI, live profile, cloud objects, or real passwords were used.");
        stdout.WriteLine("DateUtc: " + result.MeasuredAtUtc.ToString("o", CultureInfo.InvariantCulture));
        stdout.WriteLine("OS: " + result.OsDescription);
        stdout.WriteLine("Runtime: " + result.FrameworkDescription);
        stdout.WriteLine("ProcessorCount: " + result.ProcessorCount.ToString(CultureInfo.InvariantCulture));
        stdout.WriteLine("ProcessorIdentifier: " + result.ProcessorIdentifier);
        stdout.WriteLine("Algorithm: " + result.Algorithm);
        stdout.WriteLine("Primitive: " + result.Primitive);
        stdout.WriteLine("Library: " + result.Library);
        stdout.WriteLine("Password: fixed dummy ASCII (not a user secret)");
        stdout.WriteLine("Warmups: " + result.Warmups.ToString(CultureInfo.InvariantCulture));
        stdout.WriteLine("Repeats: " + result.Repeats.ToString(CultureInfo.InvariantCulture));
        stdout.WriteLine("ProductionPbkdf2Unchanged: " + result.ProductionPbkdf2Unchanged);
        foreach (Argon2idProfileMeasurement row in result.Profiles)
        {
            stdout.WriteLine("Profile: " + row.Profile.Id);
            stdout.WriteLine("  Source: " + row.Profile.Source);
            stdout.WriteLine("  Role: " + row.Profile.Role);
            stdout.WriteLine("  MemoryKiB: " + row.Profile.MemoryKiB.ToString(CultureInfo.InvariantCulture));
            stdout.WriteLine("  Iterations: " + row.Profile.Iterations.ToString(CultureInfo.InvariantCulture));
            stdout.WriteLine("  Parallelism: " + row.Profile.Parallelism.ToString(CultureInfo.InvariantCulture));
            stdout.WriteLine("  MedianMs: " + FormatMs(row.MedianMs));
            stdout.WriteLine("  MinMs: " + FormatMs(row.MinMs));
            stdout.WriteLine("  MaxMs: " + FormatMs(row.MaxMs));
            stdout.WriteLine("  WorkingSetDeltaBytes: " + row.WorkingSetDeltaBytes.ToString(CultureInfo.InvariantCulture));
            foreach (Pbkdf2SyncCycleScaleRow scale in row.SyncCycleTimeScale)
            {
                stdout.WriteLine(
                    "  SyncCycleScale: packages="
                    + scale.PackageCount.ToString(CultureInfo.InvariantCulture)
                    + " attachments="
                    + scale.AttachmentCount.ToString(CultureInfo.InvariantCulture)
                    + " derivations="
                    + scale.DerivationCount.ToString(CultureInfo.InvariantCulture)
                    + " estimatedMedianMs="
                    + FormatMs(scale.EstimatedMedianMs));
            }
        }

        stdout.WriteLine("RecommendationStrategy: " + result.Recommendation.Strategy);
        stdout.WriteLine("Pbkdf2RaiseAccepted: false");
        stdout.WriteLine("Argon2idProductionDefaultAccepted: false");
        stdout.WriteLine("ChangeProductionConstantsInThisStage: false");
        stdout.WriteLine("ReadyForLaterMigration: " + (result.Recommendation.ReadyForLaterMigration ? "true" : "false"));
        stdout.WriteLine("Recommendation: " + result.Recommendation.Summary);
        stdout.WriteLine("NotUniversal: true");
        stdout.WriteLine("NotProductionDefault: true");
    }

    private static string FormatMs(double ms)
        => ms.ToString("0.000", CultureInfo.InvariantCulture);
}
