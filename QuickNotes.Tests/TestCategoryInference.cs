using System;

namespace QuickNotes.Tests;

internal static class TestCategoryInference
{
    public static string Infer(string typeName, string classBody)
    {
        if (typeName is "YandexObjectStorageLiveAcceptanceTests" or "YandexObjectStorageLiveSmokeTests")
        {
            return TestCategories.LiveCloud;
        }

        if (classBody.Contains("StaTestHarness", StringComparison.Ordinal)
            || classBody.Contains("QuickNotes.App.exe", StringComparison.Ordinal))
        {
            return TestCategories.UiSensitive;
        }

        if (classBody.Contains("SqliteConnection", StringComparison.Ordinal)
            || classBody.Contains("UseSqlite", StringComparison.Ordinal)
            || classBody.Contains("SqliteTestUtil", StringComparison.Ordinal)
            || classBody.Contains("new QuickNotesDbContext", StringComparison.Ordinal)
            || classBody.Contains("ArchiveFx", StringComparison.Ordinal)
            || classBody.Contains("Process.Start", StringComparison.Ordinal)
            || classBody.Contains("ApplicationCompositionRoot.Create", StringComparison.Ordinal)
            || classBody.Contains("DpapiS3CredentialsStorage", StringComparison.Ordinal)
            || classBody.Contains("DpapiSyncPasswordStorage", StringComparison.Ordinal)
            || classBody.Contains("ReminderLedgerStore", StringComparison.Ordinal)
            || classBody.Contains("Path.GetTempPath()", StringComparison.Ordinal))
        {
            return TestCategories.Integration;
        }

        return TestCategories.Unit;
    }
}
