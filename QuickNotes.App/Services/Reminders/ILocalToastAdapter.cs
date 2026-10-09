using System;

namespace QuickNotes.App.Services.Reminders;

public sealed class LocalToastRequest
{
    public string ReminderId { get; init; } = string.Empty;
    public Guid NoteSyncId { get; init; }
    public string Title { get; init; } = "Напоминание QuickNotes";
    public string Body { get; init; } = string.Empty;
    public string LaunchArgs { get; init; } = string.Empty;
}

public sealed class LocalToastActivation
{
    public Guid? NoteSyncId { get; init; }
}

public enum LocalToastDeliveryKind
{
    Shown = 0,
    Unavailable = 1,
    Failed = 2
}

public sealed class LocalToastDeliveryResult
{
    public LocalToastDeliveryKind Kind { get; init; }
    public string? UserMessage { get; init; }

    public static LocalToastDeliveryResult Shown { get; } = new() { Kind = LocalToastDeliveryKind.Shown };

    public static LocalToastDeliveryResult Unavailable(string message) => new()
    {
        Kind = LocalToastDeliveryKind.Unavailable,
        UserMessage = message
    };

    public static LocalToastDeliveryResult Failed(string message) => new()
    {
        Kind = LocalToastDeliveryKind.Failed,
        UserMessage = message
    };
}

public interface ILocalToastAdapter
{
    bool IsAvailable { get; }
    string? UnavailableReason { get; }
    LocalToastDeliveryResult Show(LocalToastRequest request);
    event Action<LocalToastActivation>? Activated;
}
