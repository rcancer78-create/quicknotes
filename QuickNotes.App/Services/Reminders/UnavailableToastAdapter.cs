using System;

namespace QuickNotes.App.Services.Reminders;

public sealed class UnavailableToastAdapter : ILocalToastAdapter
{
    public UnavailableToastAdapter(string reason = "Уведомления Windows недоступны.")
    {
        UnavailableReason = reason;
    }

    public bool IsAvailable => false;
    public string? UnavailableReason { get; }
    public event Action<LocalToastActivation>? Activated
    {
        add { }
        remove { }
    }

    public LocalToastDeliveryResult Show(LocalToastRequest request)
        => LocalToastDeliveryResult.Unavailable(UnavailableReason ?? "Уведомления Windows недоступны.");
}
