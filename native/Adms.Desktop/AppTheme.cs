using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Adms.Desktop;

/// <summary>Architecture Office Suit is a light application. Windows high contrast still supplies its own colours.</summary>
internal static class AppTheme
{
    public static bool InHighContrast => new AccessibilitySettings().HighContrast;

    public static ElementTheme Active { get; private set; } = ElementTheme.Light;

    public static string ActiveDictionary => InHighContrast ? "HighContrast" : "Light";

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
        Active = ElementTheme.Light;
        root.RequestedTheme = ElementTheme.Light;
        ApplyTitleBar(window);
        ApplyFrame(hwnd);
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

    private static void ApplyTitleBar(AppWindow window)
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
            return;
        var titleBar = window.TitleBar;
        titleBar.PreferredTheme = TitleBarTheme.Light;
        var background = Color(245, 245, 242);
        var foreground = Color(37, 40, 43);
        var inactiveForeground = Color(78, 85, 90);
        var hover = Color(232, 232, 227);
        var pressed = Color(213, 214, 208);
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

    private static void ApplyFrame(nint hwnd)
    {
        if (hwnd == 0)
            return;
        var immersive = 0;
        _ = DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref immersive, sizeof(int));
        var caption = ColorRef(245, 245, 242);
        var text = ColorRef(37, 40, 43);
        var border = ColorRef(213, 214, 208);
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
