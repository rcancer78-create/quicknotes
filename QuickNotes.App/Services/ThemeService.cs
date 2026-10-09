using System;
using System.Windows;
using Application = System.Windows.Application;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Microsoft.Win32;
using QuickNotes.App.Models;

namespace QuickNotes.App.Services;

public static class ThemeService
{
    private static bool _listenerInitialized;

    public static AppTheme CurrentTheme { get; private set; } = AppTheme.System;
    public static bool IsDark { get; private set; }

    public static event Action<bool>? ThemeChanged;

    public static bool IsWindowsInDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int lightThemeValue)
            {
                return lightThemeValue == 0;
            }
        }
        catch
        {
            // Registry may be inaccessible in restricted/testing environments
        }

        return false;
    }

    public static bool ResolveIsDark(AppTheme theme)
    {
        return theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Green => true,
            AppTheme.Light => false,
            AppTheme.System => IsWindowsInDarkTheme(),
            _ => false
        };
    }

    public static void InitializeListener(Action onThemeRefresh)
    {
        if (_listenerInitialized) return;
        _listenerInitialized = true;

        try
        {
            SystemEvents.UserPreferenceChanged += (s, e) =>
            {
                if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.Color)
                {
                    if (CurrentTheme == AppTheme.System)
                    {
                        var app = Application.Current;
                        if (app != null && app.Dispatcher != null)
                        {
                            app.Dispatcher.InvokeAsync(onThemeRefresh);
                        }
                        else
                        {
                            onThemeRefresh();
                        }
                    }
                }
            };
        }
        catch
        {
            // In headless/test runners SystemEvents might throw or be unsupported
        }
    }

    public static void ApplyTheme(AppTheme theme)
    {
        CurrentTheme = theme;
        bool isDark = ResolveIsDark(theme);
        IsDark = isDark;
        if (theme == AppTheme.Green)
        {
            ApplyGreenThemeColors();
        }
        else
        {
            ApplyThemeColors(isDark);
        }

        ThemeChanged?.Invoke(isDark);
    }

    public static void ApplyThemeColors(bool isDark)
    {
        var app = Application.Current;
        if (app == null) return;

        void SetBrush(string key, Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            app.Resources[key] = brush;
        }

        if (isDark)
        {
            // Dark palette (slate/indigo dark theme)
            SetBrush("InkBrush", Color.FromRgb(0xF8, 0xFA, 0xFC));
            SetBrush("MutedBrush", Color.FromRgb(0x94, 0xA3, 0xB8));
            SetBrush("SurfaceBrush", Color.FromRgb(0x1E, 0x29, 0x3B));
            SetBrush("SurfaceAltBrush", Color.FromRgb(0x18, 0x22, 0x34));
            SetBrush("CanvasBrush", Color.FromRgb(0x0F, 0x17, 0x2A));
            SetBrush("LineBrush", Color.FromRgb(0x33, 0x41, 0x55));
            SetBrush("LineMutedBrush", Color.FromRgb(0x47, 0x55, 0x69));
            SetBrush("AccentBrush", Color.FromRgb(0x63, 0x66, 0xF1));
            SetBrush("AccentHoverBrush", Color.FromRgb(0x81, 0x8C, 0xF8));
            SetBrush("ButtonBgBrush", Color.FromRgb(0x33, 0x41, 0x55));
            SetBrush("ButtonFgBrush", Color.FromRgb(0xF8, 0xFA, 0xFC));
            SetBrush("BadgeBgBrush", Color.FromRgb(0x33, 0x41, 0x55));
            SetBrush("BadgeFgBrush", Color.FromRgb(0xE2, 0xE8, 0xF0));
            SetBrush("InputBgBrush", Color.FromRgb(0x1E, 0x29, 0x3B));
            SetBrush("InputFgBrush", Color.FromRgb(0xF8, 0xFA, 0xFC));
            SetBrush("InputBorderBrush", Color.FromRgb(0x47, 0x55, 0x69));
            SetBrush("MenuBgBrush", Color.FromRgb(0x1E, 0x29, 0x3B));
            SetBrush("MenuFgBrush", Color.FromRgb(0xF8, 0xFA, 0xFC));
            SetBrush("CardBgBrush", Color.FromRgb(0x1E, 0x29, 0x3B));
            SetBrush("CardBorderBrush", Color.FromRgb(0x33, 0x41, 0x55));
            SetBrush("CardSelectedBgBrush", Color.FromRgb(0x1E, 0x1B, 0x4B));
            SetBrush("CardSelectedBorderBrush", Color.FromRgb(0x81, 0x8C, 0xF8));

            // Banners & alerts
            SetBrush("InfoBannerBgBrush", Color.FromRgb(0x1E, 0x29, 0x3B));
            SetBrush("InfoBannerBorderBrush", Color.FromRgb(0x3B, 0x82, 0xF6));
            SetBrush("InfoBannerFgBrush", Color.FromRgb(0x93, 0xC5, 0xFD));
            SetBrush("WarningBannerBgBrush", Color.FromRgb(0x45, 0x1A, 0x03));
            SetBrush("WarningBannerBorderBrush", Color.FromRgb(0xB4, 0x53, 0x09));
            SetBrush("WarningBannerFgBrush", Color.FromRgb(0xFC, 0xD3, 0x4D));
            SetBrush("ErrorBannerBgBrush", Color.FromRgb(0x45, 0x0A, 0x0A));
            SetBrush("ErrorBannerBorderBrush", Color.FromRgb(0xDC, 0x26, 0x26));
            SetBrush("ErrorBannerFgBrush", Color.FromRgb(0xFC, 0xA5, 0xA5));
            SetBrush("SuccessBannerBgBrush", Color.FromRgb(0x06, 0x4E, 0x3B));
            SetBrush("SuccessBannerBorderBrush", Color.FromRgb(0x05, 0x96, 0x69));
            SetBrush("SuccessBannerFgBrush", Color.FromRgb(0x6E, 0xE7, 0xB7));
        }
        else
        {
            // Light palette
            SetBrush("InkBrush", Color.FromRgb(0x17, 0x20, 0x33));
            SetBrush("MutedBrush", Color.FromRgb(0x5B, 0x6B, 0x7F));
            SetBrush("SurfaceBrush", Color.FromRgb(0xFF, 0xFF, 0xFF));
            SetBrush("SurfaceAltBrush", Color.FromRgb(0xF8, 0xFA, 0xFC));
            SetBrush("CanvasBrush", Color.FromRgb(0xF5, 0xF7, 0xFB));
            SetBrush("LineBrush", Color.FromRgb(0xE5, 0xEA, 0xF2));
            SetBrush("LineMutedBrush", Color.FromRgb(0xCB, 0xD5, 0xE1));
            SetBrush("AccentBrush", Color.FromRgb(0x4F, 0x46, 0xE5));
            SetBrush("AccentHoverBrush", Color.FromRgb(0x43, 0x38, 0xCA));
            SetBrush("ButtonBgBrush", Color.FromRgb(0xEE, 0xF2, 0xF7));
            SetBrush("ButtonFgBrush", Color.FromRgb(0x33, 0x41, 0x55));
            SetBrush("BadgeBgBrush", Color.FromRgb(0xEE, 0xF2, 0xF6));
            SetBrush("BadgeFgBrush", Color.FromRgb(0x33, 0x41, 0x55));
            SetBrush("InputBgBrush", Color.FromRgb(0xFF, 0xFF, 0xFF));
            SetBrush("InputFgBrush", Color.FromRgb(0x17, 0x20, 0x33));
            SetBrush("InputBorderBrush", Color.FromRgb(0xDC, 0xE3, 0xEE));
            SetBrush("MenuBgBrush", Color.FromRgb(0xFF, 0xFF, 0xFF));
            SetBrush("MenuFgBrush", Color.FromRgb(0x17, 0x20, 0x33));
            SetBrush("CardBgBrush", Color.FromRgb(0xFF, 0xFF, 0xFF));
            SetBrush("CardBorderBrush", Color.FromRgb(0xE5, 0xEA, 0xF2));
            SetBrush("CardSelectedBgBrush", Color.FromRgb(0xEE, 0xF2, 0xFF));
            SetBrush("CardSelectedBorderBrush", Color.FromRgb(0x63, 0x66, 0xF1));

            // Banners & alerts
            SetBrush("InfoBannerBgBrush", Color.FromRgb(0xEF, 0xF6, 0xFF));
            SetBrush("InfoBannerBorderBrush", Color.FromRgb(0xBF, 0xDB, 0xFE));
            SetBrush("InfoBannerFgBrush", Color.FromRgb(0x1E, 0x40, 0xAF));
            SetBrush("WarningBannerBgBrush", Color.FromRgb(0xFE, 0xF3, 0xC7));
            SetBrush("WarningBannerBorderBrush", Color.FromRgb(0xFC, 0xD3, 0x4D));
            SetBrush("WarningBannerFgBrush", Color.FromRgb(0x92, 0x40, 0x0E));
            SetBrush("ErrorBannerBgBrush", Color.FromRgb(0xFE, 0xF2, 0xF2));
            SetBrush("ErrorBannerBorderBrush", Color.FromRgb(0xFC, 0xA5, 0xA5));
            SetBrush("ErrorBannerFgBrush", Color.FromRgb(0xB9, 0x1C, 0x1C));
            SetBrush("SuccessBannerBgBrush", Color.FromRgb(0xDC, 0xFC, 0xE7));
            SetBrush("SuccessBannerBorderBrush", Color.FromRgb(0x86, 0xEF, 0xAC));
            SetBrush("SuccessBannerFgBrush", Color.FromRgb(0x15, 0x80, 0x3D));
        }
    }

    private static void ApplyGreenThemeColors()
    {
        var app = Application.Current;
        if (app == null) return;

        void SetBrush(string key, Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            app.Resources[key] = brush;
        }

        // Green palette (forest/emerald dark theme)
        SetBrush("InkBrush", Color.FromRgb(0xEC, 0xFD, 0xF5));
        SetBrush("MutedBrush", Color.FromRgb(0x86, 0xA7, 0x89));
        SetBrush("SurfaceBrush", Color.FromRgb(0x13, 0x2B, 0x20));
        SetBrush("SurfaceAltBrush", Color.FromRgb(0x0F, 0x24, 0x1B));
        SetBrush("CanvasBrush", Color.FromRgb(0x0B, 0x1A, 0x13));
        SetBrush("LineBrush", Color.FromRgb(0x1E, 0x42, 0x32));
        SetBrush("LineMutedBrush", Color.FromRgb(0x2A, 0x5A, 0x44));
        SetBrush("AccentBrush", Color.FromRgb(0x10, 0xB9, 0x81));
        SetBrush("AccentHoverBrush", Color.FromRgb(0x34, 0xD3, 0x99));
        SetBrush("ButtonBgBrush", Color.FromRgb(0x1C, 0x44, 0x32));
        SetBrush("ButtonFgBrush", Color.FromRgb(0xEC, 0xFD, 0xF5));
        SetBrush("BadgeBgBrush", Color.FromRgb(0x1E, 0x47, 0x35));
        SetBrush("BadgeFgBrush", Color.FromRgb(0xA7, 0xF3, 0xD0));
        SetBrush("InputBgBrush", Color.FromRgb(0x13, 0x2B, 0x20));
        SetBrush("InputFgBrush", Color.FromRgb(0xEC, 0xFD, 0xF5));
        SetBrush("InputBorderBrush", Color.FromRgb(0x2D, 0x63, 0x4B));
        SetBrush("MenuBgBrush", Color.FromRgb(0x13, 0x2B, 0x20));
        SetBrush("MenuFgBrush", Color.FromRgb(0xEC, 0xFD, 0xF5));
        SetBrush("CardBgBrush", Color.FromRgb(0x13, 0x2B, 0x20));
        SetBrush("CardBorderBrush", Color.FromRgb(0x1E, 0x42, 0x32));
        SetBrush("CardSelectedBgBrush", Color.FromRgb(0x1B, 0x4D, 0x36));
        SetBrush("CardSelectedBorderBrush", Color.FromRgb(0x34, 0xD3, 0x99));

        // Banners & alerts
        SetBrush("InfoBannerBgBrush", Color.FromRgb(0x11, 0x34, 0x38));
        SetBrush("InfoBannerBorderBrush", Color.FromRgb(0x0D, 0x94, 0x88));
        SetBrush("InfoBannerFgBrush", Color.FromRgb(0x99, 0xF6, 0xE4));
        SetBrush("WarningBannerBgBrush", Color.FromRgb(0x3D, 0x26, 0x05));
        SetBrush("WarningBannerBorderBrush", Color.FromRgb(0xB4, 0x53, 0x09));
        SetBrush("WarningBannerFgBrush", Color.FromRgb(0xFD, 0xE6, 0x8A));
        SetBrush("ErrorBannerBgBrush", Color.FromRgb(0x40, 0x10, 0x14));
        SetBrush("ErrorBannerBorderBrush", Color.FromRgb(0xDC, 0x26, 0x26));
        SetBrush("ErrorBannerFgBrush", Color.FromRgb(0xFE, 0xCA, 0xCA));
        SetBrush("SuccessBannerBgBrush", Color.FromRgb(0x06, 0x4E, 0x3B));
        SetBrush("SuccessBannerBorderBrush", Color.FromRgb(0x05, 0x96, 0x69));
        SetBrush("SuccessBannerFgBrush", Color.FromRgb(0x6E, 0xE7, 0xB7));
    }
}
