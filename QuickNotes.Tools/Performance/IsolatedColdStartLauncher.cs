using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace QuickNotes.Tools.Performance;

public sealed class IsolatedProcessRunResult
{
    public int ExitCode { get; init; }
    public string StandardOutput { get; init; } = string.Empty;
    public string StandardError { get; init; } = string.Empty;
    public int ProcessId { get; init; }
    public bool TimedOut { get; init; }
}

public static class IsolatedColdStartLauncher
{
    public static IsolatedProcessRunResult Run(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new ArgumentException("Executable cannot be empty.", nameof(executable));
        }

        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("Process executable was not found.", executable);
        }

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive.");
        }

        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (string argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = Process.Start(psi);
        if (process == null)
        {
            throw new InvalidOperationException("Failed to start process " + executable);
        }

        int pid = process.Id;
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                stdout.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                stderr.AppendLine(e.Data);
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        bool exited = process.WaitForExit((int)Math.Min(int.MaxValue, timeout.TotalMilliseconds));
        if (!exited)
        {
            TryKillCreatedProcess(process);
            process.WaitForExit(5_000);
            throw new TimeoutException(
                "Created process " + pid.ToString(CultureInfo.InvariantCulture)
                + " exceeded timeout " + timeout.TotalMilliseconds.ToString(CultureInfo.InvariantCulture)
                + " ms and was terminated.");
        }

        process.WaitForExit();
        return new IsolatedProcessRunResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = stdout.ToString(),
            StandardError = stderr.ToString(),
            ProcessId = pid,
            TimedOut = false
        };
    }

    public static double ParseStartupReadyMs(string standardOutput)
    {
        const string prefix = "QN_PERF_STARTUP_READY_MS:";
        foreach (string line in standardOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal)
                && double.TryParse(line.Substring(prefix.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out double ms))
            {
                return ms;
            }
        }

        throw new InvalidOperationException("Process stdout did not contain QN_PERF_STARTUP_READY_MS.");
    }

    public static IReadOnlyList<string> ParsePhases(string standardOutput)
    {
        const string prefix = "QN_PERF_PHASE:";
        var list = new List<string>();
        foreach (string line in standardOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                list.Add(line);
            }
        }

        return list;
    }

    public static IsolatedProcessRunResult RunPerfStartup(
        string publishedExe,
        string isolatedProfileDirectory,
        TimeSpan timeout,
        string repoRoot)
    {
        ReadyToRunPublishPathGuard.NormalizeAndValidatePublishedExe(publishedExe, repoRoot);
        string[] args = ReadyToRunPublishPathGuard.BuildPerfStartupArguments(isolatedProfileDirectory);
        return Run(publishedExe, args, timeout);
    }

    private static void TryKillCreatedProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Exception)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch
            {
                // Best-effort termination of only the process we created.
            }
        }
    }
}
