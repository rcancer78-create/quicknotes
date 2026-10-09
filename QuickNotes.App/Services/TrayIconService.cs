using System;
using System.Drawing;
using System.Windows.Forms;

namespace QuickNotes.App.Services;

public class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private bool _disposed;

    public event Action? OpenRequested;
    public event Action? NewNoteRequested;
    public event Action? SettingsRequested;
    public event Action? ScreenOcrRequested;
    public event Action? ExitRequested;

    public TrayIconService(bool visible = true)
    {
        var executableIcon = !string.IsNullOrWhiteSpace(Environment.ProcessPath)
            ? Icon.ExtractAssociatedIcon(Environment.ProcessPath)
            : null;
        _icon = (Icon)(executableIcon ?? SystemIcons.Application).Clone();
        executableIcon?.Dispose();

        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "QuickNotes",
            Visible = visible
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть", null, (s, e) => OpenRequested?.Invoke());
        menu.Items.Add("Новая заметка", null, (s, e) => NewNoteRequested?.Invoke());
        menu.Items.Add("Снимок экрана и OCR", null, (s, e) => ScreenOcrRequested?.Invoke());
        menu.Items.Add("Настройки", null, (s, e) => SettingsRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (s, e) => ExitRequested?.Invoke());

        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.DoubleClick += (s, e) => OpenRequested?.Invoke();
    }

    public void ShowNotification(string title, string message)
    {
        try
        {
            _notifyIcon.ShowBalloonTip(3000, title, message, ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            ErrorLogService.Write("Tray.Notification", ex);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _icon.Dispose();
            _disposed = true;
        }
    }
}
