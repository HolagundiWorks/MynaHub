namespace Adms.Desktop;

/// <summary>
/// Where Kivo keeps its own settings (recent companies, window place). Earlier versions used a folder named ADMS; the first run of this
/// version copies it to Kivo and leaves the old one where it is, so going back to an earlier version still finds its settings.
/// </summary>
internal static class AppFolder
{
    public const string Name = "O-Studio";
    private const string OldName = "ADMS";

    public static string Path => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);

    /// <summary>The settings folder, copied from the older one the first time.</summary>
    public static string Prepare() => Prepare(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    internal static string Prepare(string root)
    {
        var current = System.IO.Path.Combine(root, Name);
        var old = System.IO.Path.Combine(root, OldName);
        try
        {
            if (!Directory.Exists(current) && Directory.Exists(old))
                Copy(old, current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A half-copied folder would look like settings that are not there; remove it and start clean rather than mislead.
            try
            {
                if (Directory.Exists(current))
                    Directory.Delete(current, true);
            }
            catch (Exception inner) when (inner is IOException or UnauthorizedAccessException)
            {
            }
        }
        return current;
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
            File.Copy(file, System.IO.Path.Combine(to, System.IO.Path.GetFileName(file)), false);
        foreach (var dir in Directory.EnumerateDirectories(from))
            Copy(dir, System.IO.Path.Combine(to, System.IO.Path.GetFileName(dir)));
    }
}
