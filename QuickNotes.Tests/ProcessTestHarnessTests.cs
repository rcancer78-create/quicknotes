using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace QuickNotes.Tests;

[TestCategory(TestCategories.Integration)]
public sealed class ProcessTestHarnessTests
{
    [Fact]
    public void Run_DrainsLargeStderrBeforeStdoutCloses()
    {
        var result = ProcessTestHarness.Run(PowerShell(
            "[Console]::Error.Write(('e' * 100000)); [Console]::Out.Write('finished'); exit 0"),
            TimeSpan.FromSeconds(20));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("finished", result.StandardOutput);
        Assert.Equal(new string('e', 100000), result.StandardError);
    }

    [Fact]
    public void Run_TimeoutKillsChildWithOpenPipesAndIncludesPartialOutput()
    {
        var stopwatch = Stopwatch.StartNew();
        var error = Assert.Throws<TimeoutException>(() => ProcessTestHarness.Run(PowerShell(
            "[Console]::Out.WriteLine($PID); [Console]::Out.Flush(); " +
            "[Console]::Error.WriteLine('waiting-fixture'); [Console]::Error.Flush(); Start-Sleep -Seconds 60"),
            TimeSpan.FromSeconds(5)));

        Assert.Contains("waiting-fixture", error.Message);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), error.Message);
        string output = error.Message.Split("stdout:\n", StringSplitOptions.None)[1];
        int childId = int.Parse(output.Split('\n')[0].Trim(), CultureInfo.InvariantCulture);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(childId));
    }

    [Fact]
    public void Run_PreservesExitCodeAndBothStreams()
    {
        var result = ProcessTestHarness.Run(PowerShell(
            "[Console]::Out.Write('out-fixture'); [Console]::Error.Write('err-fixture'); exit 7"),
            TimeSpan.FromSeconds(20));

        Assert.Equal(7, result.ExitCode);
        Assert.Equal("out-fixture", result.StandardOutput);
        Assert.Equal("err-fixture", result.StandardError);
    }

    private static ProcessStartInfo PowerShell(string script)
    {
        // Console-only fixture: never starts the desktop application or opens a window.
        return new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script }
        };
    }
}
