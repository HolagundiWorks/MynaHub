using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Adms.Desktop;

internal static class AppTheme
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ADMS",
        "theme.json");

    public static ElementTheme Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return ElementTheme.Default;
            var saved = JsonSerializer.Deserialize<SavedTheme>(File.ReadAllText(FilePath));
            return saved?.Theme switch
            {
                "light" => ElementTheme.Light,
                "dark" => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return ElementTheme.Default;
        }
    }

    public static void Save(ElementTheme theme)
    {
        var name = theme switch
        {
            ElementTheme.Light => "light",
            ElementTheme.Dark => "dark",
            _ => "system",
        };
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(new SavedTheme { Theme = name }));
    }

    public static bool InHighContrast => new AccessibilitySettings().HighContrast;

    public static ElementTheme Active { get; private set; } = ElementTheme.Light;

    public static string ActiveDictionary =>
        InHighContrast ? "HighContrast" : Active == ElementTheme.Dark ? "Dark" : "Light";

    public static void Apply(FrameworkElement root, AppWindow window, nint hwnd, ElementTheme theme)
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
        if (theme == ElementTheme.Default)
            theme = ElementTheme.Light;
        Active = theme;
        root.RequestedTheme = theme;
        ApplyTitleBar(window, theme);
        ApplyFrame(hwnd, theme == ElementTheme.Dark);
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

    public static void ApplyTitleBar(AppWindow window, ElementTheme theme)
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
            return;
        if (theme == ElementTheme.Default)
            theme = ElementTheme.Light;
        var titleBar = window.TitleBar;
        titleBar.PreferredTheme = theme == ElementTheme.Dark ? TitleBarTheme.Dark : TitleBarTheme.Light;
        var dark = theme == ElementTheme.Dark;
        var background = dark ? Color(28, 28, 26) : Color(240, 239, 234);
        var foreground = dark ? Color(240, 239, 234) : Color(26, 26, 26);
        var inactiveForeground = dark ? Color(183, 181, 174) : Color(95, 95, 95);
        var hover = dark ? Color(42, 42, 39) : Color(231, 228, 220);
        var pressed = dark ? Color(60, 59, 54) : Color(213, 210, 202);
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
        var caption = dark ? ColorRef(28, 28, 26) : ColorRef(240, 239, 234);
        var text = dark ? ColorRef(240, 239, 234) : ColorRef(26, 26, 26);
        var border = dark ? ColorRef(60, 59, 54) : ColorRef(213, 210, 202);
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

    private sealed class SavedTheme
    {
        public string Theme { get; set; } = "system";
    }
}
