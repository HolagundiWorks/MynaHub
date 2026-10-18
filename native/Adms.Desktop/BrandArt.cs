using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Adms.Desktop;

internal static class BrandArt
{
    public const string Name = "O-Studio";

    public static ImageSource Mark() => Load("mark.png");

    public static ImageSource Wordmark() => Load("wordmark.png");

    public static async Task<ImageSource?> Decode(string? data)
    {
        if (string.IsNullOrEmpty(data))
            return null;
        var bytes = Convert.FromBase64String(data);
        var stream = new InMemoryRandomAccessStream();
        var writer = new DataWriter(stream);
        writer.WriteBytes(bytes);
        await writer.StoreAsync();
        writer.DetachStream();
        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    private static ImageSource Load(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", file);
        return new BitmapImage(new Uri(path));
    }
}
