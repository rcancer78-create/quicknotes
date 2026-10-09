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
public sealed class EncryptedArchivePbkdf2BenchmarkTests
{
    [Fact]
    public void SuggestIterations_ScalesToTarget_AndClampsToHardBounds()
    {
        Assert.Equal(2_490_000, EncryptedArchivePbkdf2Benchmark.SuggestIterations(50_000, 5.015, 250));
        Assert.Equal(
            EncryptedArchiveConstants.MinKdfIterationsUntrusted,
            EncryptedArchivePbkdf2Benchmark.SuggestIterations(50_000, 10_000, 250));
        Assert.Equal(
            EncryptedArchiveConstants.MaxKdfIterations,
            EncryptedArchivePbkdf2Benchmark.SuggestIterations(50_000, 0.001, 250));
    }

    [Fact]
    public void Host_RejectsExtraArgsWithoutMeasuring()
    {
        Assert.Equal(
            EncryptedArchiveRestoreExitCodes.Usage,
            EncryptedArchivePbkdf2BenchmarkHost.Run(new[] { "--benchmark-archive-pbkdf2" }));
    }

    [Fact]
    public void WriteReport_DoesNotContainDummyPasswordOrUserSecrets()
    {
        var result = new EncryptedArchivePbkdf2BenchmarkResult
        {
            MeasuredAtUtc = new DateTimeOffset(2026, 9, 10, 9, 21, 15, TimeSpan.Zero),
            OsDescription = "test-os",
            FrameworkDescription = "test-runtime",
            ProcessorCount = 2,
            ProcessorIdentifier = "test-cpu",
            Warmups = 3,
            Repeats = 5,
            ProbeIterations = 50_000,
            ProbeMedianMs = 5.015,
            TargetMs = 250,
            AllowedMinMs = 150,
            AllowedMaxMs = 400,
            SuggestedIterations = 2_490_000,
            ConfirmIterations = 2_490_000,
            ConfirmMedianMs = 255.167,
            ConfirmMinMs = 243.591,
            ConfirmMaxMs = 260.255,
            ConfirmInAllowedRange = true
        };

        using var writer = new StringWriter();
        EncryptedArchivePbkdf2Benchmark.WriteReport(result, writer);
        string text = writer.ToString();
        Assert.Contains("local benchmark", text, StringComparison.Ordinal);
        Assert.Contains("not a universal cost", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(EncryptedArchivePbkdf2Benchmark.DummyPassword, text, StringComparison.Ordinal);
        Assert.DoesNotContain(".qnar", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Process_ConsoleExe_PrintsReport_ExitsZero_AndDoesNotLeakSecrets()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "QuickNotes.Tools.exe");
        Assert.True(File.Exists(exe), "QuickNotes.Tools.exe should be copied next to testhost.");

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(psi);
        Assert.NotNull(process);
        process!.StandardInput.Close();
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        bool exited = process.WaitForExit(90_000);
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
        Assert.True(exited, "QuickNotes.Tools.exe must stay attached, print the report, and exit.");
        Assert.True(process.HasExited);
        Assert.Equal(EncryptedArchiveRestoreExitCodes.Success, process.ExitCode);
        Assert.Equal(IntPtr.Zero, process.MainWindowHandle);
        Assert.Contains("QNAR PBKDF2-HMAC-SHA256 local benchmark", stdout, StringComparison.Ordinal);
        Assert.Contains("not a universal cost", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DateUtc:", stdout, StringComparison.Ordinal);
        Assert.Contains("Warmups: 3", stdout, StringComparison.Ordinal);
        Assert.Contains("Repeats: 5", stdout, StringComparison.Ordinal);
        Assert.Contains("ProbeIterations: 50000", stdout, StringComparison.Ordinal);
        Assert.Contains("SuggestedIterations:", stdout, StringComparison.Ordinal);
        Assert.Contains("ConfirmIterations:", stdout, StringComparison.Ordinal);
        Assert.Contains("ConfirmMedianMs:", stdout, StringComparison.Ordinal);
        Assert.Contains("ConfirmMinMs:", stdout, StringComparison.Ordinal);
        Assert.Contains("ConfirmMaxMs:", stdout, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"ConfirmInAllowedRange: (true|false)"), stdout);
        Assert.Matches(new Regex(@"SuggestedIterations: \d+"), stdout);
        Assert.DoesNotContain(EncryptedArchivePbkdf2Benchmark.DummyPassword, stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(EncryptedArchivePbkdf2Benchmark.DummyPassword, stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(".qnar", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password.txt", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MainWindow", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("MainWindow", stderr, StringComparison.Ordinal);
    }
}
