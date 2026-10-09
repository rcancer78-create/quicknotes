using System;
using System.Windows;
using System.Windows.Interop;
using QuickNotes.App.Views;

namespace QuickNotes.App.Helpers;

public static class WindowActivationHelper
{
    /// <summary>
    /// Configures owner and startup location, attaches activation handlers,
    /// and shows the NoteEditorWindow dialog.
    /// </summary>
    public static bool? PrepareAndShowEditor(NoteEditorWindow window, Window? preferredOwner = null)
    {
        Window? owner = preferredOwner;
        if (owner == null || !owner.IsVisible || owner.WindowState == WindowState.Minimized)
        {
            owner = WindowOwnerResolver.GetActiveWindowOwner();
        }

        if (owner != null && owner.IsVisible && owner.WindowState != WindowState.Minimized && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
        }
        else
        {
            window.Owner = null;
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        window.Loaded += (_, _) =>
        {
            ActivateToForeground(window);
        };

        return window.ShowDialog();
    }

    /// <summary>
    /// Brings the specified window to the foreground, restores it from Minimized state,
    /// promotes its Z-order via a brief Topmost toggle, and invokes Win32 SetForegroundWindow.
    /// </summary>
    public static void ActivateToForeground(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();

        // Temporarily promote window to the top of Z-order, then release Topmost
        // so it does not stay permanently floating over other applications.
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();

        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero)
            {
                Win32Helper.ShowWindow(handle, Win32Helper.SW_RESTORE);
                Win32Helper.SetForegroundWindow(handle);
            }
        }
        catch
        {
            // Ignore HWND or interop errors in unit tests/headless runs.
        }

        if (window is NoteEditorWindow editorWin)
        {
            editorWin.Dispatcher.BeginInvoke(new Action(() =>
            {
                editorWin.NoteTextBox.Focus();
                editorWin.NoteTextBox.CaretIndex = editorWin.NoteTextBox.Text.Length;
            }), System.Windows.Threading.DispatcherPriority.Input);
        }
    }
}
