using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace QuickNotes.Tools.Performance;

public sealed class DisplayEnvironmentSnapshot
{
    public string OsDescription { get; init; } = string.Empty;
    public string DisplayDescription { get; init; } = string.Empty;
    public int RefreshHz { get; init; }
    public int WidthPx { get; init; }
    public int HeightPx { get; init; }
    public int Dpi { get; init; }
}

public static class DisplayEnvironmentProbe
{
    private const int HorzRes = 8;
    private const int VertRes = 10;
    private const int LogPixelsX = 88;
    private const int VRefresh = 116;

    public static DisplayEnvironmentSnapshot Capture()
    {
        int width = 0;
        int height = 0;
        int dpi = 0;
        int hz = 0;
        IntPtr hdc = GetDC(IntPtr.Zero);
        if (hdc != IntPtr.Zero)
        {
            try
            {
                width = GetDeviceCaps(hdc, HorzRes);
                height = GetDeviceCaps(hdc, VertRes);
                dpi = GetDeviceCaps(hdc, LogPixelsX);
                hz = GetDeviceCaps(hdc, VRefresh);
            }
            finally
            {
                _ = ReleaseDC(IntPtr.Zero, hdc);
            }
        }

        string os = RuntimeInformation.OSDescription;
        string display = width > 0
            ? string.Format(
                CultureInfo.InvariantCulture,
                "{0}x{1}px, {2} Hz (GDI VREFRESH), {3} DPI",
                width,
                height,
                hz,
                dpi)
            : "display metrics unavailable";

        return new DisplayEnvironmentSnapshot
        {
            OsDescription = os,
            DisplayDescription = display,
            RefreshHz = hz,
            WidthPx = width,
            HeightPx = height,
            Dpi = dpi
        };
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int index);
}
