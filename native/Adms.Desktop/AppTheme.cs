using System.Runtime.InteropServices;
using O.Ui;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Adms.Desktop;

/// <summary>Applies the suite light/dark preference (and Windows high contrast when active).</summary>
internal static class AppTheme
{
    public static bool InHighContrast => new AccessibilitySettings().HighContrast;

    public static ElementTheme Active { get; private set; } = ElementTheme.Light;

    public static string ActiveDictionary =>
        InHighContrast ? "HighContrast" : SuiteAppearance.IsDark ? "Dark" : "Light";

    public static void Apply(FrameworkElement root, AppWindow window, nint hwnd)
    {
        root.HighContrastAdjustment = ElementHighContrastAdjustment.Auto;
        if (InHighContrast)
        {
            Active = ElementTheme.Default;
            root.RequestedTheme = ElementTheme.Default;
            ResetTitleBar(window);
            ResetFrame(hwnd);
            return;
        }
        Active = SuiteAppearance.Current;
        root.RequestedTheme = Active;
        ApplyTitleBar(window, Active == ElementTheme.Dark);
        ApplyFrame(hwnd, Active == ElementTheme.Dark);
    }

    private static void ResetTitleBar(AppWindow window)
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
            return;
        var titleBar = window.TitleBar;
        titleBar.BackgroundColor = null;
        titleBar.ForegroundColor = null;
        titleBar.InactiveBackgroundColor = null;
        titleBar.InactiveForegroundColor = null;
        titleBar.ButtonBackgroundColor = null;
        titleBar.ButtonForegroundColor = null;
        titleBar.ButtonHoverBackgroundColor = null;
        titleBar.ButtonHoverForegroundColor = null;
        titleBar.ButtonPressedBackgroundColor = null;
        titleBar.ButtonPressedForegroundColor = null;
        titleBar.ButtonInactiveBackgroundColor = null;
        titleBar.ButtonInactiveForegroundColor = null;
    }

    private static void ApplyTitleBar(AppWindow window, bool dark)
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
            return;
        var titleBar = window.TitleBar;
        titleBar.PreferredTheme = dark ? TitleBarTheme.Dark : TitleBarTheme.Light;
        var background = dark ? Color(0, 0, 0) : Color(255, 255, 255);
        var foreground = dark ? Color(255, 255, 255) : Color(0, 0, 0);
        var inactiveForeground = dark ? Color(184, 184, 184) : Color(77, 77, 77);
        var hover = dark ? Color(31, 31, 31) : Color(240, 240, 240);
        var pressed = dark ? Color(51, 51, 51) : Color(217, 217, 217);
        titleBar.BackgroundColor = background;
        titleBar.ForegroundColor = foreground;
        titleBar.InactiveBackgroundColor = background;
        titleBar.InactiveForegroundColor = inactiveForeground;
        titleBar.ButtonBackgroundColor = background;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonHoverBackgroundColor = hover;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedBackgroundColor = pressed;
        titleBar.ButtonPressedForegroundColor = foreground;
        titleBar.ButtonInactiveBackgroundColor = background;
        titleBar.ButtonInactiveForegroundColor = inactiveForeground;
    }

    private static void ResetFrame(nint hwnd)
    {
        if (hwnd == 0)
            return;
        var color = unchecked((int)0xFFFFFFFF);
        _ = DwmSetWindowAttribute(hwnd, DwmCaptionColor, ref color, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, DwmTextColor, ref color, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, DwmBorderColor, ref color, sizeof(int));
    }

    private static void ApplyFrame(nint hwnd, bool dark)
    {
        if (hwnd == 0)
            return;
        var immersive = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref immersive, sizeof(int));
        var caption = dark ? ColorRef(0, 0, 0) : ColorRef(255, 255, 255);
        var text = dark ? ColorRef(255, 255, 255) : ColorRef(0, 0, 0);
        // Pure white and pure black themes (2026-11-10): the border is #767676 on white (4.5:1) and #8C8C8C on black (6.2:1), both over 3:1 (WCAG 1.4.11).
        var border = dark ? ColorRef(140, 140, 140) : ColorRef(118, 118, 118);
        _ = DwmSetWindowAttribute(hwnd, DwmCaptionColor, ref caption, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, DwmTextColor, ref text, sizeof(int));
        _ = DwmSetWindowAttribute(hwnd, DwmBorderColor, ref border, sizeof(int));
    }

    private static int ColorRef(byte red, byte green, byte blue) => red | (green << 8) | (blue << 16);

    private static Color Color(byte red, byte green, byte blue) => Windows.UI.Color.FromArgb(255, red, green, blue);

    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmBorderColor = 34;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
