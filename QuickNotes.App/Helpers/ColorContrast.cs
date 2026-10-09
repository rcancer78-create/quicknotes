using System;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace QuickNotes.App.Helpers;

/// <summary>
/// WCAG relative-luminance contrast for theme brushes (logical pixels, not a physical meter).
/// </summary>
public static class ColorContrast
{
    public const double AaNormalText = 4.5;
    public const double AaLargeOrUi = 3.0;

    public static double RelativeLuminance(Color color)
    {
        return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
    }

    public static double Ratio(Color a, Color b)
    {
        double l1 = RelativeLuminance(a);
        double l2 = RelativeLuminance(b);
        double hi = Math.Max(l1, l2);
        double lo = Math.Min(l1, l2);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static double Channel(byte value)
    {
        double s = value / 255.0;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }
}
