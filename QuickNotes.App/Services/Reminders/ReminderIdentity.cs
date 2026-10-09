using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using QuickNotes.App.Models;

namespace QuickNotes.App.Services.Reminders;

/// <summary>
/// Stable reminder identity: SHA-256 of Note.SyncId + task fingerprint + due date.
/// Never includes a separate plaintext title/body field in the digest input beyond the
/// already-hashed fingerprint (bullet + body) from <see cref="MarkdownTaskParser"/>.
/// </summary>
public static class ReminderIdentity
{
    public static string Compute(Guid noteSyncId, string fingerprint, DateOnly dueDate)
    {
        string due = dueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string payload = "v1\n" + noteSyncId.ToString("N") + "\n" + (fingerprint ?? string.Empty) + "\n" + due;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string Compute(MarkdownTaskLocator locator)
    {
        if (locator.DueDate is not DateOnly due)
        {
            throw new ArgumentException("Reminder identity requires a due date.", nameof(locator));
        }

        return Compute(locator.NoteSyncId, locator.Fingerprint, due);
    }
}
