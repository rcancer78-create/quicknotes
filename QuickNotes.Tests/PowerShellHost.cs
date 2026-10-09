using System;
using System.ComponentModel;
using System.Diagnostics;

namespace QuickNotes.Tests;

/// <summary>
/// Single host-selection point for tests that shell out to PowerShell scripts.
/// Prefers PowerShell 7 ("pwsh", used by GitHub CI on windows-latest) and falls
/// back to Windows PowerShell 5.1 ("powershell") on machines without PS7.
/// All invoked scripts are 5.1-compatible; CI behavior is unchanged because
/// pwsh is always preferred when present. No secrets are added to diagnostics.
/// </summary>
internal static class PowerShellHost
{
    internal const string PreferredFileName = "pwsh";
    internal const string FallbackFileName = "powershell";

    private static readonly object Gate = new();
    private static string? _cachedFileName;

    internal static string FileName
    {
        get
        {
            lock (Gate)
            {
                if (_cachedFileName != null)
                {
                    return _cachedFileName;
                }
            }

            string resolved = Resolve();
            lock (Gate)
            {
                _cachedFileName ??= resolved;
                return _cachedFileName;
            }
        }
    }

    internal readonly record struct PowerShellResult(int ExitCode, string Stdout, string Stderr);

    internal static PowerShellResult RunFile(string script, string args, string workDir, int timeoutMs = 60_000)
    {
        // -ExecutionPolicy Bypass is required for Windows PowerShell 5.1 where the
        // machine policy may be Restricted; it is a no-op equivalent for pwsh on CI.
        return Run($"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" {args}", workDir, timeoutMs);
    }

    internal static PowerShellResult RunCommand(string command, string workDir, int timeoutMs = 60_000)
    {
        return Run("-NoProfile -ExecutionPolicy Bypass -Command \"" + command + "\"", workDir, timeoutMs);
    }

    private static PowerShellResult Run(string arguments, string workDir, int timeoutMs)
    {
        string host = FileName;
        var psi = new ProcessStartInfo
        {
            FileName = host,
            Arguments = arguments,
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException(host + " failed to start");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException(host + " timed out." + stdout + stderr);
        }

        return new PowerShellResult(process.ExitCode, stdout, stderr);
    }

    private static string Resolve()
    {
        if (CanStart(PreferredFileName))
        {
            return PreferredFileName;
        }

        if (CanStart(FallbackFileName))
        {
            return FallbackFileName;
        }

        throw new InvalidOperationException(
            "Neither 'pwsh' (PowerShell 7, used by GitHub CI on windows-latest) nor " +
            "'powershell' (Windows PowerShell 5.1) is available. Install PowerShell 7 to run these tests.");
    }

    private static bool CanStart(string fileName)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = "-NoProfile -Command \"$true\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            if (process == null)
            {
                return false;
            }

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            return process.WaitForExit(15_000) && process.ExitCode == 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
