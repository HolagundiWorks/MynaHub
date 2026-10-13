using System.Text.Json;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace Adms.Desktop;

internal sealed class WindowPlacement
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ADMS",
        "window.json");

    public static WindowPlacement LoadOrCenter()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var saved = JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(FilePath));
                if (saved is { Width: >= 640, Height: >= 480 } && IsUsable(saved))
                    return saved;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidCastException)
        {
        }

        var area = DisplayArea.Primary.WorkArea;
        var width = Math.Min(1280, Math.Max(640, area.Width - 80));
        var height = Math.Min(800, Math.Max(480, area.Height - 80));
        return new WindowPlacement
        {
            X = area.X + Math.Max(0, (area.Width - width) / 2),
            Y = area.Y + Math.Max(0, (area.Height - height) / 2),
            Width = width,
            Height = height,
        };
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
    }

    public void Apply(AppWindow window)
    {
        window.MoveAndResize(new RectInt32(X, Y, Width, Height));
        if (Maximized && window.Presenter is OverlappedPresenter presenter)
            presenter.Maximize();
    }

    private static bool IsUsable(WindowPlacement saved)
    {
        var areas = DisplayArea.FindAll();
        for (var i = 0; i < areas.Count; i++)
        {
            var bounds = areas[i].OuterBounds;
            var overlapW = Math.Min(saved.X + saved.Width, bounds.X + bounds.Width) - Math.Max(saved.X, bounds.X);
            var overlapH = Math.Min(saved.Y + saved.Height, bounds.Y + bounds.Height) - Math.Max(saved.Y, bounds.Y);
            if (overlapW >= 160 && overlapH >= 120)
                return true;
        }
        return false;
    }
}
