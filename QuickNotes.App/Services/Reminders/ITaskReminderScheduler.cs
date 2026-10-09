using System;
using System.Threading;
using System.Threading.Tasks;

namespace QuickNotes.App.Services.Reminders;

public sealed class ReminderSchedulerStatus
{
    public bool UserEnabled { get; init; }
    public bool AdapterAvailable { get; init; }
    public bool IsRunning { get; init; }
    public DateTime? NextScanLocal { get; init; }
    public string CompactText { get; init; } = string.Empty;
    public string? LastError { get; init; }
}

public interface ITaskReminderScheduler : IDisposable
{
    ReminderSchedulerStatus Status { get; }
    event Action? StatusChanged;
    event Action<Guid>? NoteActivationRequested;

    void Start();
    void Refresh();
    Task RefreshAsync(CancellationToken cancellationToken = default);
    void Stop();
    void NotifySettingsChanged();
}
