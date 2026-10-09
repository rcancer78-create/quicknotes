using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using QuickNotes.App.Services;
using Point = System.Windows.Point;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace QuickNotes.App.Views;

public partial class ScreenCropOverlayWindow : Window, IScreenCropOverlay
{
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int VkEscape = 0x1B;

    private bool _isDragging;
    private Point _startPoint;
    private bool _dismissed;
    private bool _completingSelection;
    private HwndSource? _hwndSource;

    public System.Drawing.Rectangle? SelectedScreenRect { get; private set; }

    public ScreenCropOverlayWindow()
    {
        InitializeComponent();

        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        SourceInitialized += Window_SourceInitialized;
    }

    public bool? ShowModal()
    {
        return ShowDialog();
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        _hwndSource = PresentationSource.FromVisual(this) as HwndSource;
        _hwndSource?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is WmKeyDown or WmSysKeyDown && wParam.ToInt32() == VkEscape)
        {
            RequestCancel();
            handled = true;
            return (IntPtr)1;
        }

        return IntPtr.Zero;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Activate();
        Focus();
        Keyboard.Focus(this);

        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            SetForegroundWindow(handle);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape || e.SystemKey == Key.Escape)
        {
            RequestCancel();
            e.Handled = true;
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape || e.SystemKey == Key.Escape)
        {
            RequestCancel();
            e.Handled = true;
        }
    }

    private void Window_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        RequestCancel();
        e.Handled = true;
    }

    public void RequestCancel()
    {
        if (!Dispatcher.CheckAccess())
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(RequestCancel));
            }
            catch
            {
                // Dispatcher may be shut down or closing.
            }
            return;
        }

        SelectedScreenRect = null;
        ForceClose();
    }

    public void ForceClose()
    {
        if (!Dispatcher.CheckAccess())
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(ForceClose));
            }
            catch
            {
                // Dispatcher may be shut down or closing.
            }
            return;
        }

        if (_dismissed)
        {
            TryHardHide();
            return;
        }

        _dismissed = true;
        _isDragging = false;
        ReleaseCaptureQuietly();
        EndOverlaySession(dialogResult: _completingSelection);
    }

    private void EndOverlaySession(bool dialogResult)
    {
        try
        {
            DialogResult = dialogResult;
        }
        catch (InvalidOperationException)
        {
            try
            {
                Close();
            }
            catch (InvalidOperationException)
            {
                // Already closing.
            }
        }

        TryHardHide();
    }

    private void TryHardHide()
    {
        try { Topmost = false; } catch { /* ignore */ }
        try { Opacity = 0; } catch { /* ignore */ }
        try { Visibility = Visibility.Collapsed; } catch { /* ignore */ }
        try { ShowInTaskbar = false; } catch { /* ignore */ }
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                ShowWindow(handle, SwHide);
            }
        }
        catch
        {
            // Ignore native hide failures.
        }
    }

    private void ReleaseCaptureQuietly()
    {
        try
        {
            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
            }
        }
        catch
        {
            // Ignore capture release failures while tearing down the overlay.
        }
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        _dismissed = true;
        TryHardHide();
    }

    private void Window_Closed(object sender, EventArgs e)
    {
        _dismissed = true;
        if (_hwndSource != null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }

        TryHardHide();
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_dismissed) return;

        _startPoint = e.GetPosition(this);
        _isDragging = true;
        CaptureMouse();

        Canvas.SetLeft(SelectionBorder, _startPoint.X);
        Canvas.SetTop(SelectionBorder, _startPoint.Y);
        SelectionBorder.Width = 0;
        SelectionBorder.Height = 0;
        SelectionBorder.Visibility = Visibility.Visible;
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || _dismissed) return;

        var currentPoint = e.GetPosition(this);

        double x = Math.Min(_startPoint.X, currentPoint.X);
        double y = Math.Min(_startPoint.Y, currentPoint.Y);
        double w = Math.Abs(currentPoint.X - _startPoint.X);
        double h = Math.Abs(currentPoint.Y - _startPoint.Y);

        Canvas.SetLeft(SelectionBorder, x);
        Canvas.SetTop(SelectionBorder, y);
        SelectionBorder.Width = w;
        SelectionBorder.Height = h;

        if (w > 20 && h > 20)
        {
            double badgeY = y >= 28 ? y - 26 : y + h + 6;
            Canvas.SetLeft(SizeBadge, x);
            Canvas.SetTop(SizeBadge, badgeY);
            SizeText.Text = $"{(int)w} × {(int)h}";
            SizeBadge.Visibility = Visibility.Visible;
        }
        else
        {
            SizeBadge.Visibility = Visibility.Collapsed;
        }
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging || _dismissed) return;

        _isDragging = false;
        ReleaseCaptureQuietly();

        double w = SelectionBorder.Width;
        double h = SelectionBorder.Height;

        if (w < 8 || h < 8)
        {
            RequestCancel();
            return;
        }

        double left = Canvas.GetLeft(SelectionBorder);
        double top = Canvas.GetTop(SelectionBorder);

        var p1 = PointToScreen(new Point(left, top));
        var p2 = PointToScreen(new Point(left + w, top + h));

        int screenLeft = (int)Math.Round(Math.Min(p1.X, p2.X));
        int screenTop = (int)Math.Round(Math.Min(p1.Y, p2.Y));
        int screenWidth = (int)Math.Round(Math.Abs(p2.X - p1.X));
        int screenHeight = (int)Math.Round(Math.Abs(p2.Y - p1.Y));

        if (screenWidth < 8 || screenHeight < 8)
        {
            RequestCancel();
            return;
        }

        SelectedScreenRect = new System.Drawing.Rectangle(screenLeft, screenTop, screenWidth, screenHeight);
        CompleteSelection();
    }

    private void CompleteSelection()
    {
        if (_dismissed) return;
        _completingSelection = true;
        ForceClose();
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SwHide = 0;
}
