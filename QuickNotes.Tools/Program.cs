using QuickNotes.App.Services.EncryptedArchive;

namespace QuickNotes.Tools;

/// <summary>
/// Console host for local PBKDF2 measurement. No WPF, no archive I/O, no user secrets.
/// </summary>
public static class EncryptedArchivePbkdf2BenchmarkHost
{
    public static readonly string UsageText =
        "Usage: QuickNotes.Tools.exe"
        + Environment.NewLine
        + "Measures PBKDF2-HMAC-SHA256 on this machine with dummy password/salt only."
        + Environment.NewLine
        + "Does not read archives, notes, or user secrets. Exit codes: 0 success, 1 usage, 6 unexpected.";

    public static int Run(string[]? args)
    {
        string[] list = args ?? Array.Empty<string>();
        if (list.Length != 0)
        {
            Console.Error.WriteLine("QuickNotes.Tools.exe не принимает аргументов.");
            Console.Error.WriteLine(UsageText);
            return EncryptedArchiveRestoreExitCodes.Usage;
        }

        try
        {
            EncryptedArchivePbkdf2BenchmarkResult result = EncryptedArchivePbkdf2Benchmark.Run();
            EncryptedArchivePbkdf2Benchmark.WriteReport(result, Console.Out);
            return EncryptedArchiveRestoreExitCodes.Success;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("Benchmark failed.");
            return EncryptedArchiveRestoreExitCodes.Unexpected;
        }
    }
}

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args != null && args.Length > 0)
        {
            string first = args[0];
            if (string.Equals(first, "benchmark", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(first, "perf", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(first, "--benchmark", StringComparison.OrdinalIgnoreCase))
            {
                string[] benchmarkArgs = args.Skip(1).ToArray();
                return Performance.PerformanceBenchmarkTool.Run(benchmarkArgs);
            }

            if (string.Equals(first, "kdf", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(first, "--benchmark-kdf", StringComparison.OrdinalIgnoreCase))
            {
                string[] kdfArgs = args.Skip(1).ToArray();
                return Pbkdf2CostBenchmarkHost.Run(kdfArgs);
            }

            if (string.Equals(first, "argon2id", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(first, "--spike-argon2id", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(first, "kdf-argon2id", StringComparison.OrdinalIgnoreCase))
            {
                string[] argonArgs = args.Skip(1).ToArray();
                return Argon2idFeasibilityBenchmarkHost.Run(argonArgs);
            }

            if (string.Equals(first, "cold-start-r2r", StringComparison.OrdinalIgnoreCase))
            {
                string[] r2rArgs = args.Skip(1).ToArray();
                return Performance.PerformanceColdStartR2rTool.Run(r2rArgs);
            }

            if (string.Equals(first, "scroll-fps-parse", StringComparison.OrdinalIgnoreCase))
            {
                string[] scrollArgs = args.Skip(1).ToArray();
                return Performance.PerformanceScrollFpsTool.Run(scrollArgs);
            }

            if (string.Equals(first, "encrypt-credentials", StringComparison.OrdinalIgnoreCase))
            {
                string[] credArgs = args.Skip(1).ToArray();
                return EncryptCredentialsTool.Run(credArgs);
            }
        }

        return EncryptedArchivePbkdf2BenchmarkHost.Run(args);
    }
}
