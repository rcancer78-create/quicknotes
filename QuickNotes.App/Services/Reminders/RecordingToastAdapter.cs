using System;
using System.Collections.Generic;

namespace QuickNotes.App.Services.Reminders;

/// <summary>
/// In-memory adapter for tests and isolated smoke. Never shows a system toast.
/// </summary>
public sealed class RecordingToastAdapter : ILocalToastAdapter
{
    private readonly object _gate = new();
    private readonly List<LocalToastRequest> _shown = new();

    public bool IsAvailable { get; init; } = true;
    public string? UnavailableReason { get; init; }
    public LocalToastDeliveryKind ForcedResult { get; init; } = LocalToastDeliveryKind.Shown;
    public string? ForcedMessage { get; init; }

    public IReadOnlyList<LocalToastRequest> Shown
    {
        get
        {
            lock (_gate)
            {
                return _shown.ToList();
            }
        }
    }

    public event Action<LocalToastActivation>? Activated;

    public LocalToastDeliveryResult Show(LocalToastRequest request)
    {
        if (!IsAvailable)
        {
            return LocalToastDeliveryResult.Unavailable(UnavailableReason ?? "Уведомления недоступны.");
        }

        if (ForcedResult == LocalToastDeliveryKind.Failed)
        {
            return LocalToastDeliveryResult.Failed(ForcedMessage ?? "Не удалось показать уведомление.");
        }

        lock (_gate)
        {
            _shown.Add(request);
        }

        return LocalToastDeliveryResult.Shown;
    }

    public void RaiseActivated(Guid noteSyncId)
    {
        Activated?.Invoke(new LocalToastActivation { NoteSyncId = noteSyncId });
    }
}
