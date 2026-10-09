using System;

namespace QuickNotes.App.Services.Reminders;

/// <summary>
/// Due date <c>@YYYY-MM-DD</c> becomes due at 09:00 in the given local timezone (default: local OS zone).
/// Invalid DST clock times (spring-forward gap) skip forward one hour.
/// </summary>
public static class ReminderDueSemantics
{
    public static readonly TimeOnly LocalDueTime = new(9, 0);

    public static DateTime GetDueLocal(DateOnly dueDate, TimeZoneInfo? timeZone = null)
    {
        TimeZoneInfo tz = timeZone ?? TimeZoneInfo.Local;
        DateTime unspecified = dueDate.ToDateTime(LocalDueTime, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddHours(1);
        }

        return DateTime.SpecifyKind(unspecified, DateTimeKind.Unspecified);
    }

    public static bool IsDueOrOverdue(DateOnly dueDate, DateTime nowLocal, TimeZoneInfo? timeZone = null)
    {
        DateTime due = GetDueLocal(dueDate, timeZone);
        DateTime now = DateTime.SpecifyKind(nowLocal, DateTimeKind.Unspecified);
        return now >= due;
    }
}
