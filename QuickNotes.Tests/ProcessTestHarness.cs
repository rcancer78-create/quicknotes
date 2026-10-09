using System.Diagnostics;
using System.IO;
using System.Text;

namespace QuickNotes.Tests;

/// <summary>Drains both redirected pipes while enforcing a deadline on the whole child lifetime.</summary>
internal static class ProcessTestHarness
{
    public static ProcessTestResult Run(ProcessStartInfo startInfo, TimeSpan timeout)
        => RunAsync(startInfo, timeout).GetAwaiter().GetResult();

    private static async Task<ProcessTestResult> RunAsync(ProcessStartInfo startInfo, TimeSpan timeout)
    {
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start the test child process.");
        }

        using var readCancellation = new CancellationTokenSource();
        var stdout = new CapturedOutput();
        var stderr = new CapturedOutput();
        Task stdoutRead = stdout.DrainAsync(process.StandardOutput, readCancellation.Token);
        Task stderrRead = stderr.DrainAsync(process.StandardError, readCancellation.Token);
        Task completion = Task.WhenAll(process.WaitForExitAsync(), stdoutRead, stderrRead);
        try
        {
            await completion.WaitAsync(timeout).ConfigureAwait(false);
            return new ProcessTestResult(process.ExitCode, stdout.Snapshot(), stderr.Snapshot());
        }
        catch (Exception error)
        {
            // Reading to EOF before waiting defeats the deadline, and reading the two pipes
            // sequentially can deadlock a child writing to stderr. Kill only our owned child.
            string cleanupError = string.Empty;
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
                await completion.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                cleanupError = $"\nChild cleanup: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                readCancellation.Cancel();
                // Observe faults even if a descendant kept an inherited pipe open.
                _ = completion.ContinueWith(t => _ = t.Exception,
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }

            string diagnostics = $"Child PID {process.Id}; deadline {timeout.TotalSeconds:g}s." +
                $"\nstdout:\n{stdout.Snapshot()}\nstderr:\n{stderr.Snapshot()}{cleanupError}";
            if (error is TimeoutException)
            {
                throw new TimeoutException("Test child process exceeded its deadline. " + diagnostics, error);
            }
            throw new InvalidOperationException("Test child process failed. " + diagnostics, error);
        }
    }

    private sealed class CapturedOutput
    {
        private const int MaximumCharacters = 128 * 1024;
        private readonly StringBuilder _text = new();

        public async Task DrainAsync(StreamReader reader, CancellationToken cancellationToken)
        {
            var buffer = new char[4096];
            while (true)
            {
                int count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    return;
                }
                lock (_text)
                {
                    _text.Append(buffer, 0, count);
                    if (_text.Length > MaximumCharacters)
                    {
                        _text.Remove(0, _text.Length - MaximumCharacters);
                    }
                }
            }
        }

        public string Snapshot()
        {
            lock (_text)
            {
                return _text.ToString();
            }
        }
    }
}

internal sealed record ProcessTestResult(int ExitCode, string StandardOutput, string StandardError);
