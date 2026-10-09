using System;
using System.Collections.Generic;

namespace QuickNotes.App.Services.Reminders;

public enum ReminderDeliveryStatus
{
    Delivered = 1
}

public sealed class ReminderLedgerDocument
{
    public int Version { get; set; } = 1;
    public List<ReminderLedgerEntry> Entries { get; set; } = new();
}

public sealed class ReminderLedgerEntry
{
    public string Id { get; set; } = string.Empty;
    public ReminderDeliveryStatus Status { get; set; } = ReminderDeliveryStatus.Delivered;
    public DateTime UpdatedUtc { get; set; }
}
