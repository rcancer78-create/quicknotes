using System;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace QuickNotes.App.Services.Reminders;

/// <summary>
/// Immediate local Windows toast via <see cref="ToastNotificationManager"/>.
/// Unpackaged WPF cannot reliably register scheduled toasts or COM activation after exit.
/// Delivery only while QuickNotes is running. Click passes opaque Note.SyncId when Activated fires in-process.
/// </summary>
public sealed class WindowsToastAdapter : ILocalToastAdapter
{
    public const string AppUserModelId = "QuickNotes.Desktop";

    private readonly ToastNotifier? _notifier;
    private readonly object _gate = new();

    public WindowsToastAdapter()
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
            _notifier = ToastNotificationManager.CreateToastNotifier(AppUserModelId);
            IsAvailable = true;
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            UnavailableReason = "Уведомления Windows недоступны для этого приложения.";
            ErrorLogService.Write("WindowsToast.Init", ex.GetType().Name);
            _notifier = null;
        }
    }

    public bool IsAvailable { get; }
    public string? UnavailableReason { get; }

    public event Action<LocalToastActivation>? Activated;

    public LocalToastDeliveryResult Show(LocalToastRequest request)
    {
        if (!IsAvailable || _notifier == null)
        {
            return LocalToastDeliveryResult.Unavailable(UnavailableReason ?? "Уведомления Windows недоступны.");
        }

        try
        {
            var xml = new Windows.Data.Xml.Dom.XmlDocument();
            xml.LoadXml(BuildToastXml(request.Title, request.Body, request.LaunchArgs));
            var toast = new ToastNotification(xml);
            toast.Activated += OnToastActivated;
            _notifier.Show(toast);
            return LocalToastDeliveryResult.Shown;
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("WindowsToast.Show", ex.GetType().Name);
            return LocalToastDeliveryResult.Failed("Не удалось показать уведомление Windows.");
        }
    }

    internal static string BuildToastXml(string title, string body, string launchArgs)
    {
        var sb = new StringBuilder();
        sb.Append("<toast launch=\"");
        sb.Append(XmlEscape(launchArgs));
        sb.Append("\"><visual><binding template=\"ToastGeneric\"><text>");
        sb.Append(XmlEscape(string.IsNullOrWhiteSpace(title) ? "Напоминание QuickNotes" : title));
        sb.Append("</text><text>");
        sb.Append(XmlEscape(body ?? string.Empty));
        sb.Append("</text></binding></visual></toast>");
        return sb.ToString();
    }

    private void OnToastActivated(ToastNotification sender, object args)
    {
        try
        {
            string? launch = null;
            if (args is ToastActivatedEventArgs activated)
            {
                launch = activated.Arguments;
            }

            launch ??= sender.Content?.DocumentElement?.GetAttribute("launch");
            ReminderActivationArgs.TryParse(launch, out Guid syncId);
            lock (_gate)
            {
                Activated?.Invoke(new LocalToastActivation
                {
                    NoteSyncId = syncId == Guid.Empty ? null : syncId
                });
            }
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("WindowsToast.Activated", ex.GetType().Name);
        }
    }

    private static string XmlEscape(string value)
    {
        return SecurityElement.Escape(value) ?? string.Empty;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);
}
