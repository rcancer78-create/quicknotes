using System;
using System.IO;
using System.Runtime.CompilerServices;
using QuickNotes.App.Data;
using QuickNotes.App.Services;

namespace QuickNotes.Tests;

/// <summary>
/// Testhost-wide isolation that does not belong in production.
/// AsyncLocal error-log scopes do not flow to thread-pool/WPF dispatcher work,
/// so a process-level override keeps %LOCALAPPDATA%\QuickNotes\Logs untouched.
/// </summary>
internal static class TestHostIsolation
{
    [ModuleInitializer]
    internal static void RedirectErrorLogsAwayFromLiveProfile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "QuickNotes.Tests.DefaultProfile");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, "Logs"));
        QuickNotesDbContext.ProfileDirectoryOverride = dir;
        ErrorLogService.LogDirectoryOverride = Path.Combine(dir, "Logs");
        // Module initialization must not wait for code on another thread in this
        // assembly: dispatcher callbacks can wait for the module initializer itself.
        // UI tests start the shared dispatcher lazily through their STA harness.
    }
}
