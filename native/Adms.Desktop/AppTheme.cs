using System.Runtime.InteropServices;
using Hcw.Ui;
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
        var background = dark ? Color(27, 27, 27) : Color(228, 234, 242);
        var foreground = dark ? Color(243, 243, 243) : Color(26, 35, 50);
        var inactiveForeground = dark ? Color(154, 154, 154) : Color(74, 86, 104);
        var hover = dark ? Color(47, 47, 47) : Color(241, 245, 250);
        var pressed = dark ? Color(56, 56, 56) : Color(216, 222, 232);
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
        var caption = dark ? ColorRef(27, 27, 27) : ColorRef(228, 234, 242);
        var text = dark ? ColorRef(243, 243, 243) : ColorRef(26, 35, 50);
        var border = dark ? ColorRef(120, 120, 120) : ColorRef(138, 147, 163);
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
