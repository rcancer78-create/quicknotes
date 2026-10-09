using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using QuickNotes.App.Models;

namespace QuickNotes.App.Helpers;

public static class WorkspaceLayoutHelper
{
    public const double MinWindowWidth = 640;
    public const double MinWindowHeight = 500;
    public const double DefaultWindowWidth = 1100;
    public const double DefaultWindowHeight = 720;
    public const double NarrowThresholdWidth = 900;

    public const double MinNavWidth = 200;
    public const double MaxNavWidth = 280;
    public const double DefaultNavWidth = 260;
    public const double CompactNavWidth = 56.0;

    // Combined with compact card-action labels, this prevents the header controls
    // from collapsing into ragged rows while preserving room for the detail pane.
    public const double MinListWidth = 350;
    public const double MaxListWidth = 480;
    public const double DefaultListWidth = 350;
    public const double SplitModeListWidth = 280;

    /// <summary>
    /// Documented minimum usable width for each pane (editor or preview) in split mode.
    /// Below this width, side-by-side editing leads to excessive line wrapping and poor usability.
    /// </summary>
    public const double MinUsableSplitPaneWidth = 350.0;

    public static bool CanPanesBeSideBySide(double availableContentWidth, double splitterWidth = 4.0)
    {
        if (availableContentWidth <= 0) return false;
        return (availableContentWidth - splitterWidth) / 2.0 >= MinUsableSplitPaneWidth;
    }

    public static double ClampNavWidth(double width)
    {
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0)
        {
            return DefaultNavWidth;
        }
        return Math.Clamp(width, MinNavWidth, MaxNavWidth);
    }

    public static NavigationPanelState ClampNavigationPanelState(NavigationPanelState state, bool isFullscreenFocus = false)
    {
        if (!Enum.IsDefined(typeof(NavigationPanelState), state))
        {
            return NavigationPanelState.Normal;
        }
        if (state == NavigationPanelState.Hidden && !isFullscreenFocus)
        {
            return NavigationPanelState.Normal;
        }
        return state;
    }

    public static double ClampListWidth(double width)
    {
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0)
        {
            return DefaultListWidth;
        }
        return Math.Clamp(width, MinListWidth, MaxListWidth);
    }

    public static Rect DevicePixelsToDip(Rect devicePixels, double scaleX, double scaleY)
    {
        if (scaleX <= 0 || double.IsNaN(scaleX) || double.IsInfinity(scaleX)) scaleX = 1.0;
        if (scaleY <= 0 || double.IsNaN(scaleY) || double.IsInfinity(scaleY)) scaleY = 1.0;

        return new Rect(
            devicePixels.Left / scaleX,
            devicePixels.Top / scaleY,
            devicePixels.Width / scaleX,
            devicePixels.Height / scaleY);
    }

    public static Rect DevicePixelsToDip(Rect devicePixels, DpiScale dpiScale)
    {
        return DevicePixelsToDip(devicePixels, dpiScale.DpiScaleX, dpiScale.DpiScaleY);
    }

    public static Rect DevicePixelsToDip(Rect devicePixels, Matrix transformFromDevice)
    {
        if (transformFromDevice.IsIdentity)
        {
            return devicePixels;
        }

        if (transformFromDevice.M11 <= 0 || transformFromDevice.M22 <= 0 ||
            double.IsNaN(transformFromDevice.M11) || double.IsInfinity(transformFromDevice.M11) ||
            double.IsNaN(transformFromDevice.M22) || double.IsInfinity(transformFromDevice.M22))
        {
            return devicePixels;
        }

        var topLeft = transformFromDevice.Transform(new System.Windows.Point(devicePixels.Left, devicePixels.Top));
        var bottomRight = transformFromDevice.Transform(new System.Windows.Point(devicePixels.Right, devicePixels.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    public static Rect ClampWindowBounds(double? left, double? top, double? width, double? height, Rect workArea)
    {
        return ClampWindowBounds(left, top, width, height, new[] { workArea }, workArea);
    }

    public static Rect ClampWindowBounds(
        double? left,
        double? top,
        double? width,
        double? height,
        IEnumerable<Rect>? screens,
        Rect fallbackWorkArea = default)
    {
        if (fallbackWorkArea.IsEmpty || fallbackWorkArea.Width <= 0 || fallbackWorkArea.Height <= 0)
        {
            fallbackWorkArea = new Rect(0, 0, 1920, 1080);
        }

        var validScreens = screens?
            .Where(s => !s.IsEmpty && s.Width > 0 && s.Height > 0)
            .ToList();

        if (validScreens == null || validScreens.Count == 0)
        {
            validScreens = new List<Rect> { fallbackWorkArea };
        }

        Rect targetWorkArea;

        if (left.HasValue && top.HasValue &&
            !double.IsNaN(left.Value) && !double.IsInfinity(left.Value) &&
            !double.IsNaN(top.Value) && !double.IsInfinity(top.Value))
        {
            double testWidth = width ?? DefaultWindowWidth;
            if (double.IsNaN(testWidth) || double.IsInfinity(testWidth) || testWidth <= 0) testWidth = DefaultWindowWidth;
            double testHeight = height ?? DefaultWindowHeight;
            if (double.IsNaN(testHeight) || double.IsInfinity(testHeight) || testHeight <= 0) testHeight = DefaultWindowHeight;

            var windowRect = new Rect(left.Value, top.Value, testWidth, testHeight);

            Rect bestScreen = default;
            double maxArea = 0;
            foreach (var screen in validScreens)
            {
                var intersect = Rect.Intersect(windowRect, screen);
                if (!intersect.IsEmpty)
                {
                    double area = intersect.Width * intersect.Height;
                    if (area > maxArea)
                    {
                        maxArea = area;
                        bestScreen = screen;
                    }
                }
            }

            if (maxArea > 0)
            {
                targetWorkArea = bestScreen;
            }
            else
            {
                bestScreen = validScreens[0];
                double minDistanceSq = double.MaxValue;
                foreach (var screen in validScreens)
                {
                    double dx = left.Value < screen.Left ? screen.Left - left.Value : (left.Value > screen.Right ? left.Value - screen.Right : 0);
                    double dy = top.Value < screen.Top ? screen.Top - top.Value : (top.Value > screen.Bottom ? top.Value - screen.Bottom : 0);
                    double distSq = dx * dx + dy * dy;
                    if (distSq < minDistanceSq)
                    {
                        minDistanceSq = distSq;
                        bestScreen = screen;
                    }
                }
                targetWorkArea = bestScreen;
            }
        }
        else
        {
            targetWorkArea = validScreens[0];
        }

        double safeWidth = width ?? DefaultWindowWidth;
        if (double.IsNaN(safeWidth) || double.IsInfinity(safeWidth) || safeWidth <= 0)
        {
            safeWidth = Math.Min(DefaultWindowWidth, targetWorkArea.Width);
        }
        else
        {
            safeWidth = Math.Clamp(safeWidth, Math.Min(MinWindowWidth, targetWorkArea.Width), targetWorkArea.Width);
        }

        double safeHeight = height ?? DefaultWindowHeight;
        if (double.IsNaN(safeHeight) || double.IsInfinity(safeHeight) || safeHeight <= 0)
        {
            safeHeight = Math.Min(DefaultWindowHeight, targetWorkArea.Height);
        }
        else
        {
            safeHeight = Math.Clamp(safeHeight, Math.Min(MinWindowHeight, targetWorkArea.Height), targetWorkArea.Height);
        }

        double safeLeft;
        double safeTop;

        if (!left.HasValue || double.IsNaN(left.Value) || double.IsInfinity(left.Value) ||
            !top.HasValue || double.IsNaN(top.Value) || double.IsInfinity(top.Value))
        {
            safeLeft = targetWorkArea.Left + Math.Max(0, (targetWorkArea.Width - safeWidth) / 2);
            safeTop = targetWorkArea.Top + Math.Max(0, (targetWorkArea.Height - safeHeight) / 2);
        }
        else
        {
            double maxLeft = Math.Max(targetWorkArea.Left, targetWorkArea.Right - safeWidth);
            double maxTop = Math.Max(targetWorkArea.Top, targetWorkArea.Bottom - safeHeight);
            safeLeft = Math.Clamp(left.Value, targetWorkArea.Left, maxLeft);
            safeTop = Math.Clamp(top.Value, targetWorkArea.Top, maxTop);
        }

        return new Rect(safeLeft, safeTop, safeWidth, safeHeight);
    }

    public static WindowState NormalizeWindowState(WindowState state)
    {
        return state == WindowState.Minimized ? WindowState.Normal : state;
    }

    public static bool IsNarrowMode(double actualWidth)
    {
        return actualWidth < NarrowThresholdWidth;
    }

    public const double MinBoardCardWidth = 250.0;
    public const double MaxBoardCardWidth = 380.0;
    public const double DefaultBoardCardWidth = 320.0;
    public const double BoardCardSpacing = 12.0;

    public static int CalculateBoardColumnCount(double availableWidth, bool isNarrow = false)
    {
        if (isNarrow || double.IsNaN(availableWidth) || double.IsInfinity(availableWidth) || availableWidth <= 0)
        {
            return 1;
        }

        int cols = (int)Math.Floor((availableWidth - BoardCardSpacing) / (MinBoardCardWidth + BoardCardSpacing));
        return Math.Max(1, cols);
    }

    public static double CalculateBoardCardWidth(double availableWidth, int columns, double spacing = BoardCardSpacing)
    {
        if (double.IsNaN(availableWidth) || double.IsInfinity(availableWidth) || availableWidth <= 0)
        {
            return DefaultBoardCardWidth;
        }

        if (columns <= 1)
        {
            double singleWidth = availableWidth - 2 * spacing;
            return Math.Max(MinBoardCardWidth, singleWidth);
        }

        double totalSpacing = (columns + 1) * spacing;
        double netWidth = availableWidth - totalSpacing;
        double width = Math.Floor(((netWidth - 0.2) / columns) * 10) / 10.0;
        return Math.Max(MinBoardCardWidth, width);
    }

    public static double ClampBoardCardWidth(double width)
    {
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0)
        {
            return DefaultBoardCardWidth;
        }
        return Math.Clamp(width, MinBoardCardWidth, MaxBoardCardWidth);
    }

    /// <summary>
    /// Contractual page size for workspace views (spec §11, W6 performance baseline).
    /// Both List and Board load notes in bounded batches of 50 items and never materialize
    /// the full corpus into card ViewModels up-front.
    /// </summary>
    public const int DefaultPageSize = 50;

    /// <summary>
    /// Documented test/safety cap for automated in-memory performance baseline measurements (spec §11, W6).
    /// Prevents unbounded materialization in test environments while physical 10k + DPI remain manual leftovers.
    /// </summary>
    public const int AutomatedBenchmarkSampleCap = 1000;

    /// <summary>
    /// Calculates the bounded materialization limit for the requested page number (1-based),
    /// guaranteeing that the first page never exceeds <paramref name="pageSize"/> (default 50)
    /// and that total materialized count is clamped to <paramref name="totalNotes"/>.
    /// </summary>
    public static int CalculateMaterializedCardLimit(int totalNotes, int pageNumber = 1, int pageSize = DefaultPageSize)
    {
        if (totalNotes <= 0 || pageNumber <= 0 || pageSize <= 0)
        {
            return 0;
        }

        long requested = (long)pageNumber * pageSize;
        return (int)Math.Min(totalNotes, requested);
    }

    /// <summary>
    /// Computes the number of complete rows and total row count needed to lay out <paramref name="cardCount"/>
    /// cards across <paramref name="columns"/> columns on the Board surface.
    /// </summary>
    public static (int CompleteRows, int TotalRows) CalculateBoardRowMetrics(int cardCount, int columns)
    {
        if (cardCount <= 0 || columns <= 0)
        {
            return (0, 0);
        }

        int completeRows = cardCount / columns;
        int totalRows = (int)Math.Ceiling((double)cardCount / columns);
        return (completeRows, totalRows);
    }
}
