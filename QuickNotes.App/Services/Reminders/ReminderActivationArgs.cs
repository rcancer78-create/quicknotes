using System;

namespace QuickNotes.App.Services.Reminders;

/// <summary>
/// Opaque toast launch payload: prefix + Note.SyncId ("N" format). No task text.
/// </summary>
public static class ReminderActivationArgs
{
    public const string Prefix = "qn1.";

    public static string Create(Guid noteSyncId) => Prefix + noteSyncId.ToString("N");

    public static bool TryParse(string? value, out Guid noteSyncId)
    {
        noteSyncId = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string trimmed = value.Trim();
        if (trimmed.Length != Prefix.Length + 32
            || !trimmed.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        return Guid.TryParseExact(trimmed.AsSpan(Prefix.Length), "N", out noteSyncId);
    }
}
