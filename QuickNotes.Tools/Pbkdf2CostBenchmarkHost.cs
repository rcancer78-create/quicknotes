using QuickNotes.App.Services.Crypto;
using QuickNotes.App.Services.EncryptedArchive;

namespace QuickNotes.Tools;

/// <summary>
/// Console host for note/sync/archive PBKDF2 measurement. No WPF, no user secrets, no cloud.
/// </summary>
public static class Pbkdf2CostBenchmarkHost
{
    public static readonly string UsageText =
        "Usage: QuickNotes.Tools.exe kdf [options]"
        + Environment.NewLine
        + "       QuickNotes.Tools.exe --benchmark-kdf [options]"
        + Environment.NewLine
        + "Measures PBKDF2-HMAC-SHA256 on this machine with dummy password/salt only."
        + Environment.NewLine
        + "Circuits: note, sync/blob, archive-reference. Does not read databases, DPAPI, or live profile."
        + Environment.NewLine
        + "Options:"
        + Environment.NewLine
        + "  --circuit note|sync|archive|all   Repeatable. Default: all"
        + Environment.NewLine
        + "  --json                            JSON report only"
        + Environment.NewLine
        + "  --warmups <n>                     Default 3"
        + Environment.NewLine
        + "  --repeats <n>                     Default 5"
        + Environment.NewLine
        + "  --probe <n>                       Default 50000"
        + Environment.NewLine
        + "  --skip-current                    Do not time production N"
        + Environment.NewLine
        + "  --skip-experimental               Do not time experimental N points"
        + Environment.NewLine
        + "  --skip-confirm                    Alias of --skip-experimental"
        + Environment.NewLine
        + "  --help                            Show this text"
        + Environment.NewLine
        + "Exit codes: 0 success, 1 usage, 6 unexpected.";

    public static int Run(string[]? args)
    {
        if (!Pbkdf2CostBenchmark.TryParseArgs(args, out Pbkdf2CostBenchmarkOptions options, out string error))
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
            Pbkdf2CostBenchmarkResult result = Pbkdf2CostBenchmark.Run(options);
            Pbkdf2CostBenchmark.WriteReport(result, Console.Out, options.JsonOnly);
            return EncryptedArchiveRestoreExitCodes.Success;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("Benchmark failed.");
            return EncryptedArchiveRestoreExitCodes.Unexpected;
        }
    }
}
