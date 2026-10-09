using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using QuickNotes.App.Services.EncryptedArchive;
using QuickNotes.Tools;
using Xunit;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public sealed class Argon2idFeasibilityBenchmarkHostTests
{
    [Fact]
    public async Task Process_Argon2idHelp_PrintsUsage_ExitsZero_AndDoesNotMeasureOrLeakSecrets()
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
            ArgumentList = { "argon2id", "--help" }
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
        Assert.True(exited, "QuickNotes.Tools.exe argon2id --help must print usage and exit.");
        Assert.Equal(EncryptedArchiveRestoreExitCodes.Success, process.ExitCode);
        Assert.Contains("QuickNotes.Tools.exe argon2id", stdout, StringComparison.Ordinal);
        Assert.Contains("ADR-016", stdout, StringComparison.Ordinal);
        Assert.Contains("--profile", stdout, StringComparison.Ordinal);
        Assert.Contains("Not a production KDF", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("MedianMs:", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(Argon2idFeasibilityBenchmark.DummyPassword, stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(Argon2idFeasibilityBenchmark.DummyPassword, stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("MainWindow", stdout, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"MedianMs: \d"), stdout);
    }
}
