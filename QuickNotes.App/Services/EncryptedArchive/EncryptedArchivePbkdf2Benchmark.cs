using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace QuickNotes.App.Services.EncryptedArchive;

/// <summary>
/// Local, reproducible PBKDF2-HMAC-SHA256 cost measurement. Dummy password and salt only;
/// no archive files, user secrets, or note data.
/// </summary>
public sealed class EncryptedArchivePbkdf2BenchmarkResult
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
    public required double TargetMs { get; init; }
    public required double AllowedMinMs { get; init; }
    public required double AllowedMaxMs { get; init; }
    public required int SuggestedIterations { get; init; }
    public required int ConfirmIterations { get; init; }
    public required double ConfirmMedianMs { get; init; }
    public required double ConfirmMinMs { get; init; }
    public required double ConfirmMaxMs { get; init; }
    public required bool ConfirmInAllowedRange { get; init; }
}

public static class EncryptedArchivePbkdf2Benchmark
{
    public const double TargetMilliseconds = 250;
    public const double AllowedMinMilliseconds = 150;
    public const double AllowedMaxMilliseconds = 400;
    public const int DefaultWarmups = 3;
    public const int DefaultRepeats = 5;
    public const int DefaultProbeIterations = 50_000;

    /// <summary>Fixed dummy password for local timing only. Not a user secret.</summary>
    internal const string DummyPassword = "qn-pbkdf2-bench";

    public static EncryptedArchivePbkdf2BenchmarkResult Run(
        int warmups = DefaultWarmups,
        int repeats = DefaultRepeats,
        int probeIterations = DefaultProbeIterations,
        double targetMs = TargetMilliseconds,
        double allowedMinMs = AllowedMinMilliseconds,
        double allowedMaxMs = AllowedMaxMilliseconds)
    {
        if (warmups < 1 || repeats < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(repeats), "Нужны положительные warmups и repeats.");
        }

        ValidateIterationBudget(probeIterations);

        byte[] salt = CreateDummySalt();
        double probeMedian = MeasureMedianMs(DummyPassword, salt, probeIterations, warmups, repeats);
        int suggested = SuggestIterations(probeIterations, probeMedian, targetMs);
        int confirmN = suggested;
        ValidateIterationBudget(confirmN);

        double[] confirmSamples = MeasureSamplesMs(DummyPassword, salt, confirmN, warmups, repeats);
        double confirmMedian = Median(confirmSamples);
        CryptographicOperations.ZeroMemory(salt);

        return new EncryptedArchivePbkdf2BenchmarkResult
        {
            MeasuredAtUtc = DateTimeOffset.UtcNow,
            OsDescription = RuntimeInformation.OSDescription,
            FrameworkDescription = RuntimeInformation.FrameworkDescription,
            ProcessorCount = Environment.ProcessorCount,
            ProcessorIdentifier = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "(unknown)",
            Warmups = warmups,
            Repeats = repeats,
            ProbeIterations = probeIterations,
            ProbeMedianMs = probeMedian,
            TargetMs = targetMs,
            AllowedMinMs = allowedMinMs,
            AllowedMaxMs = allowedMaxMs,
            SuggestedIterations = suggested,
            ConfirmIterations = confirmN,
            ConfirmMedianMs = confirmMedian,
            ConfirmMinMs = confirmSamples.Min(),
            ConfirmMaxMs = confirmSamples.Max(),
            ConfirmInAllowedRange = confirmMedian >= allowedMinMs && confirmMedian <= allowedMaxMs
        };
    }

    public static void WriteReport(EncryptedArchivePbkdf2BenchmarkResult result, TextWriter stdout)
    {
        stdout.WriteLine("QNAR PBKDF2-HMAC-SHA256 local benchmark");
        stdout.WriteLine("This result is for this machine and runtime only. It is not a universal cost.");
        stdout.WriteLine("No archive files, user secrets, or note data were used.");
        stdout.WriteLine("DateUtc: " + result.MeasuredAtUtc.ToString("o", CultureInfo.InvariantCulture));
        stdout.WriteLine("OS: " + result.OsDescription);
        stdout.WriteLine("Runtime: " + result.FrameworkDescription);
        stdout.WriteLine("ProcessorCount: " + result.ProcessorCount.ToString(CultureInfo.InvariantCulture));
        stdout.WriteLine("ProcessorIdentifier: " + result.ProcessorIdentifier);
        stdout.WriteLine("Algorithm: PBKDF2-HMAC-SHA256 via Rfc2898DeriveBytes.Pbkdf2 (same as archive wrap)");
        stdout.WriteLine("DkLen: " + EncryptedArchiveConstants.KeyByteSize.ToString(CultureInfo.InvariantCulture));
        stdout.WriteLine("SaltLen: " + EncryptedArchiveConstants.SaltByteSize.ToString(CultureInfo.InvariantCulture));
        stdout.WriteLine("Password: fixed dummy ASCII (not a user secret)");
        stdout.WriteLine("Warmups: " + result.Warmups.ToString(CultureInfo.InvariantCulture));
        stdout.WriteLine("Repeats: " + result.Repeats.ToString(CultureInfo.InvariantCulture));
        stdout.WriteLine("TargetMs: " + FormatMs(result.TargetMs));
        stdout.WriteLine("AllowedMs: " + FormatMs(result.AllowedMinMs) + "-" + FormatMs(result.AllowedMaxMs));
        stdout.WriteLine("ProbeIterations: " + result.ProbeIterations.ToString(CultureInfo.InvariantCulture));
        stdout.WriteLine("ProbeMedianMs: " + FormatMs(result.ProbeMedianMs));
        stdout.WriteLine("SuggestedIterations: " + result.SuggestedIterations.ToString(CultureInfo.InvariantCulture));
        stdout.WriteLine("ConfirmIterations: " + result.ConfirmIterations.ToString(CultureInfo.InvariantCulture));
        stdout.WriteLine("ConfirmMedianMs: " + FormatMs(result.ConfirmMedianMs));
        stdout.WriteLine("ConfirmMinMs: " + FormatMs(result.ConfirmMinMs));
        stdout.WriteLine("ConfirmMaxMs: " + FormatMs(result.ConfirmMaxMs));
        stdout.WriteLine("ConfirmInAllowedRange: " + (result.ConfirmInAllowedRange ? "true" : "false"));
        stdout.WriteLine(
            "Justification: interactive desktop create/unlock should cost on the order of 250 ms, not a multi-second login and not a copied note/sync constant.");
    }

    internal static int SuggestIterations(int probeIterations, double probeMedianMs, double targetMs)
    {
        if (probeMedianMs <= 0)
        {
            return EncryptedArchiveConstants.MinKdfIterationsUntrusted;
        }

        double raw = probeIterations * (targetMs / probeMedianMs);
        if (double.IsNaN(raw) || double.IsInfinity(raw) || raw <= 0)
        {
            return EncryptedArchiveConstants.MinKdfIterationsUntrusted;
        }

        if (raw >= EncryptedArchiveConstants.MaxKdfIterations)
        {
            return EncryptedArchiveConstants.MaxKdfIterations;
        }

        int rounded = (int)(Math.Floor(raw / 10_000d) * 10_000d);
        if (rounded < EncryptedArchiveConstants.MinKdfIterationsUntrusted)
        {
            return EncryptedArchiveConstants.MinKdfIterationsUntrusted;
        }

        return rounded;
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
                EncryptedArchiveConstants.KeyByteSize);
            CryptographicOperations.ZeroMemory(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    private static double MeasureMedianMs(string password, byte[] salt, int iterations, int warmups, int repeats)
        => Median(MeasureSamplesMs(password, salt, iterations, warmups, repeats));

    private static double[] MeasureSamplesMs(string password, byte[] salt, int iterations, int warmups, int repeats)
    {
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
        var salt = new byte[EncryptedArchiveConstants.SaltByteSize];
        for (int i = 0; i < salt.Length; i++)
        {
            salt[i] = (byte)(0xA5 ^ (byte)i);
        }

        return salt;
    }

    private static void ValidateIterationBudget(int iterations)
    {
        if (iterations < EncryptedArchiveConstants.MinKdfIterationsUntrusted
            || iterations > EncryptedArchiveConstants.MaxKdfIterations)
        {
            throw new EncryptedArchiveFormatException("Параметры KDF архива недопустимы.");
        }
    }

    private static string FormatMs(double ms)
        => ms.ToString("0.000", CultureInfo.InvariantCulture);
}
