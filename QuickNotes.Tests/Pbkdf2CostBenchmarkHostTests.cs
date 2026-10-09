using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using QuickNotes.App.Services.Crypto;
using QuickNotes.App.Services.EncryptedArchive;
using QuickNotes.Tools;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public sealed class Pbkdf2CostBenchmarkHostTests
{
    [Fact]
    public async Task Process_KdfHelp_PrintsUsage_ExitsZero_AndDoesNotMeasureOrLeakSecrets()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "QuickNotes.Tools.exe");
        Assert.True(File.Exists(exe), "QuickNotes.Tools.exe should be copied next to testhost.");

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "kdf", "--help" }
        };

        using var process = Process.Start(psi);
        Assert.NotNull(process);
        process!.StandardInput.Close();
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        bool exited = process.WaitForExit(15_000);
        if (!exited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Hang is asserted below.
            }
        }

        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        Assert.True(exited, "QuickNotes.Tools.exe kdf --help must print usage and exit.");
        Assert.Equal(EncryptedArchiveRestoreExitCodes.Success, process.ExitCode);
        Assert.Contains("QuickNotes.Tools.exe kdf", stdout, StringComparison.Ordinal);
        Assert.Contains("--circuit", stdout, StringComparison.Ordinal);
        Assert.Contains("--json", stdout, StringComparison.Ordinal);
        Assert.Contains("--skip-experimental", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfirmMedianMs:", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(Pbkdf2CostBenchmark.DummyPassword, stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(Pbkdf2CostBenchmark.DummyPassword, stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("MainWindow", stdout, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"ConfirmMedianMs: \d"), stdout);
    }
}
