using QuickNotes.App.Services.EncryptedArchive;

namespace QuickNotes.Tools;

/// <summary>
/// Console host for the isolated Argon2id feasibility spike. No WPF UI, no user secrets, no cloud.
/// </summary>
public static class Argon2idFeasibilityBenchmarkHost
{
    public static readonly string UsageText =
        "Usage: QuickNotes.Tools.exe argon2id [options]"
        + Environment.NewLine
        + "       QuickNotes.Tools.exe --spike-argon2id [options]"
        + Environment.NewLine
        + "Isolated Argon2id feasibility spike (ADR-016). Dummy password/salt only."
        + Environment.NewLine
        + "Not a production KDF and not an accepted envelope default."
        + Environment.NewLine
        + "Does not read databases, DPAPI, or live profile."
        + Environment.NewLine
        + "Options:"
        + Environment.NewLine
        + "  --profile all|tiny|owasp-interactive-2023|owasp-high-memory-2023|rfc9106-second-recommended"
        + Environment.NewLine
        + "                                Repeatable. Default: published OWASP/RFC sets (not tiny)"
        + Environment.NewLine
        + "  --json                        JSON report only"
        + Environment.NewLine
        + "  --warmups <n>                 Default 1"
        + Environment.NewLine
        + "  --repeats <n>                 Default 3"
        + Environment.NewLine
        + "  --help                        Show this text"
        + Environment.NewLine
        + "Exit codes: 0 success, 1 usage, 6 unexpected.";

    public static int Run(string[]? args)
    {
        if (!Argon2idFeasibilityBenchmark.TryParseArgs(args, out Argon2idFeasibilityOptions options, out string error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine(UsageText);
            return EncryptedArchiveRestoreExitCodes.Usage;
        }

        if (options.Help)
        {
            Console.Out.WriteLine(UsageText);
            return EncryptedArchiveRestoreExitCodes.Success;
        }

        try
        {
            Argon2idFeasibilityResult result = Argon2idFeasibilityBenchmark.Run(options);
            Argon2idFeasibilityBenchmark.WriteReport(result, Console.Out, options.JsonOnly);
            return EncryptedArchiveRestoreExitCodes.Success;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("Benchmark failed.");
            return EncryptedArchiveRestoreExitCodes.Unexpected;
        }
    }
}
