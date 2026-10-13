using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.UI;

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

    public static void Apply(FrameworkElement root, AppWindow window, nint hwnd, ElementTheme theme)
    {
        if (theme == ElementTheme.Default)
            theme = ElementTheme.Light;
        root.RequestedTheme = theme;
        ApplyTitleBar(window, theme);
        ApplyFrame(hwnd, theme == ElementTheme.Dark);
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
        var background = dark ? Color(32, 32, 32) : Color(243, 243, 243);
        var foreground = dark ? Color(255, 255, 255) : Color(26, 26, 26);
        var inactiveForeground = dark ? Color(160, 160, 160) : Color(110, 110, 110);
        var hover = dark ? Color(48, 48, 48) : Color(229, 229, 229);
        var pressed = dark ? Color(64, 64, 64) : Color(214, 214, 214);
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

    private static void ApplyFrame(nint hwnd, bool dark)
    {
        if (hwnd == 0)
            return;
        var immersive = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref immersive, sizeof(int));
        var caption = dark ? ColorRef(32, 32, 32) : ColorRef(243, 243, 243);
        var text = dark ? ColorRef(255, 255, 255) : ColorRef(26, 26, 26);
        var border = dark ? ColorRef(45, 45, 45) : ColorRef(218, 218, 218);
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
