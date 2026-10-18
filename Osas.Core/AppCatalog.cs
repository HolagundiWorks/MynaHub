using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace Osas.Core;

/// <summary>The apps of OSAS, the Open Source Architecture Suit.</summary>
public enum SuiteApp
{
    Studio,
    Metra,
    Draft,
}

/// <summary>One app as found on this computer.</summary>
/// <param name="ExePath">The program to start, or null when the app was not found.</param>
/// <param name="Source">How it was found, in words: "chosen by you", "registered by its installer", "in the usual folder" or "on the path".</param>
public sealed record AppInfo(SuiteApp App, string Name, string Purpose, string ExeName, string? ExePath, string? Version, string? Source)
{
    public bool Installed => ExePath != null;
}

/// <summary>What the catalogue needs to know about the computer. A test supplies its own.</summary>
public interface IProbe
{
    string? RegistryValue(string key, string name);

    bool FileExists(string path);

    string? FileVersion(string path);

    string? ExpandedFolder(string name);

    IEnumerable<string> PathFolders();

    string? SavedPath(SuiteApp app);
}

/// <summary>Finds which of the suite's apps are installed. It never starts or changes anything.</summary>
public sealed class AppCatalog
{
    private sealed record Spec(SuiteApp App, string Name, string Purpose, string ExeName, string[] Registry, string[] Folders, string[]? OlderExeNames = null);

    private static readonly Spec[] Specs =
    [
        new(SuiteApp.Studio, "O-Studio", "Office and project management", "Adms.Desktop.exe",
            [@"HKCU\Software\Human Centric Works\OStudio|InstallDir", @"HKCU\Software\Human Centric Works\AOS|InstallDir"],
            ["%LOCALAPPDATA%\\Programs\\O-Studio", "%LOCALAPPDATA%\\Programs\\Architecture Office Suit"]),
        new(SuiteApp.Metra, "O-Metra", "Quantities, estimates and billing", "AQCCore.exe",
            [@"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\{A8F3C2E1-9B4D-4E6A-8C1F-2D7E5A9B0C3D}_is1|InstallLocation"],
            ["%LOCALAPPDATA%\\Programs\\O-Metra", "%LOCALAPPDATA%\\Programs\\AQC-Core"]),
        new(SuiteApp.Draft, "O-Draft", "Drafting", "o-draft.exe",
            [],
            ["%LOCALAPPDATA%\\Programs\\O-Draft", "%LOCALAPPDATA%\\Programs\\AAD"], ["aad.exe"]),
    ];

    private readonly IProbe _probe;

    public AppCatalog(IProbe probe) => _probe = probe;

    public IReadOnlyList<AppInfo> Detect() => Specs.Select(Find).ToList();

    private AppInfo Find(Spec spec)
    {
        string? path = null;
        string? source = null;
        void Try(string? candidate, string how)
        {
            if (path == null && !string.IsNullOrWhiteSpace(candidate) && _probe.FileExists(candidate))
            {
                path = candidate;
                source = how;
            }
        }

        var saved = _probe.SavedPath(spec.App);
        // The current program name is tried first, then the name used before the rename.
        var names = new[] { spec.ExeName }.Concat(spec.OlderExeNames ?? []).ToArray();
        if (saved != null && Path.GetFileName(saved).EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            Try(saved, "chosen by you");
        foreach (var entry in spec.Registry)
        {
            var split = entry.Split('|');
            var folder = _probe.RegistryValue(split[0], split[1]);
            if (!string.IsNullOrWhiteSpace(folder))
                foreach (var name in names)
                    Try(Path.Combine(folder.Trim().Trim('"'), name), "registered by its installer");
        }
        foreach (var folder in spec.Folders)
            foreach (var name in names)
                Try(Path.Combine(_probe.ExpandedFolder(folder) ?? folder, name), "in the usual folder");
        foreach (var folder in _probe.PathFolders())
            foreach (var name in names)
                Try(Path.Combine(folder, name), "on the path");

        return new AppInfo(spec.App, spec.Name, spec.Purpose, spec.ExeName, path, path == null ? null : _probe.FileVersion(path), source);
    }
}

/// <summary>The paths a person chose for apps that were not found, kept in %LOCALAPPDATA%\OSAS\paths.json.</summary>
public sealed class SavedPaths
{
    private readonly string _file;

    public SavedPaths(string file) => _file = file;

    public static string DefaultFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OSAS", "paths.json");

    public Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(_file))
                return new();
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_file)) ?? new();
            return loaded.Where(p => Valid(p.Value)).ToDictionary(p => p.Key, p => p.Value);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    /// <summary>A program the person picked: an absolute path to an .exe, and no more than 520 characters.</summary>
    public static bool Valid(string path) =>
        path.Length is > 4 and <= 520 && Path.IsPathRooted(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !path.Contains('\0');

    public bool Save(SuiteApp app, string path)
    {
        if (!Valid(path))
            return false;
        var all = Load();
        all[app.ToString()] = path;
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        return true;
    }
}

/// <summary>The real computer: the registry, the file system and the PATH.</summary>
public sealed class WindowsProbe : IProbe
{
    private readonly SavedPaths _saved;

    public WindowsProbe(SavedPaths? saved = null) => _saved = saved ?? new SavedPaths(SavedPaths.DefaultFile);

    public string? RegistryValue(string key, string name)
    {
        try
        {
            var slash = key.IndexOf('\\');
            using var root = key[..slash] switch { "HKCU" => Registry.CurrentUser, "HKLM" => Registry.LocalMachine, _ => null };
            using var sub = root?.OpenSubKey(key[(slash + 1)..]);
            return sub?.GetValue(name) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public bool FileExists(string path) => File.Exists(path);

    public string? FileVersion(string path)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(path).ProductVersion?.Split('+')[0];
        }
        catch (Exception ex) when (ex is IOException or FileNotFoundException)
        {
            return null;
        }
    }

    public string? ExpandedFolder(string name) => Environment.ExpandEnvironmentVariables(name);

    public IEnumerable<string> PathFolders() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim().Trim('"')).Where(p => p.Length > 0);

    public string? SavedPath(SuiteApp app) => _saved.Load().GetValueOrDefault(app.ToString());
}
