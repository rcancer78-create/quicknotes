using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace QuickNotes.App.Helpers;

public record WindowDescriptor(string Title, bool IsActive, bool IsVisible, bool IsEnabled, bool IsMainWindow = false);

public static class WindowOwnerResolver
{
    /// <summary>
    /// Testable selection logic for choosing the best dialog owner from open windows.
    /// Operates without requiring live Win32 HWNDs or showing windows in headless test runners.
    /// </summary>
    public static T? ResolveOwner<T>(
        IEnumerable<T>? openWindows,
        Func<T, bool> isActive,
        Func<T, bool> isVisible,
        Func<T, bool> isEnabled,
        T? mainWindow = null) where T : class
    {
        if (openWindows == null)
            return mainWindow;

        var visibleWindows = openWindows.Where(isVisible).ToList();
        if (visibleWindows.Count == 0)
            return mainWindow;

        // 1. If any visible window is currently active, it is the premier owner
        var activeWindow = visibleWindows.FirstOrDefault(isActive);
        if (activeWindow != null)
            return activeWindow;

        // 2. If main window is disabled (e.g. a modal dialog like SettingsWindow is open),
        // find visible and enabled dialogs that are NOT mainWindow
        var enabledNonMainWindows = visibleWindows
            .Where(w => !ReferenceEquals(w, mainWindow) && isEnabled(w))
            .ToList();

        if (enabledNonMainWindows.Count > 0)
        {
            // Pick the topmost/most recently opened visible dialog (last in WPF Windows collection)
            return enabledNonMainWindows[^1];
        }

        // 3. Fallback to any non-main visible window
        var nonMainWindows = visibleWindows
            .Where(w => !ReferenceEquals(w, mainWindow))
            .ToList();

        if (nonMainWindows.Count > 0)
        {
            return nonMainWindows[^1];
        }

        // 4. Fallback to mainWindow if visible, or first visible
        if (mainWindow != null && isVisible(mainWindow))
            return mainWindow;

        return visibleWindows.FirstOrDefault();
    }

    /// <summary>
    /// Pure descriptor-based overload for headless unit testing without WPF Window instances.
    /// </summary>
    public static WindowDescriptor? ResolveOwnerDescriptor(
        IEnumerable<WindowDescriptor>? openWindows,
        WindowDescriptor? mainWindow = null)
    {
        var main = mainWindow ?? openWindows?.FirstOrDefault(w => w.IsMainWindow);
        return ResolveOwner(
            openWindows,
            w => w.IsActive,
            w => w.IsVisible,
            w => w.IsEnabled,
            main);
    }

    /// <summary>
    /// Resolves the active owner window at runtime using Application.Current.
    /// </summary>
    public static Window? GetActiveWindowOwner()
    {
        try
        {
            var app = System.Windows.Application.Current;
            if (app == null)
                return null;

            var windows = app.Windows.OfType<Window>().ToList();
            return ResolveOwner(
                windows,
                w => w.IsActive,
                w => w.IsVisible,
                w => w.IsEnabled,
                app.MainWindow);
        }
        catch
        {
            return null;
        }
    }
}
