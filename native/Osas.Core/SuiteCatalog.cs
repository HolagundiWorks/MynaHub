using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace Osas.Core;

/// <summary>One installable app of the Myna suite (MynaHub starts the others by path).</summary>
public sealed record SuiteAppDescriptor(
    string Title,
    string Blurb,
    string TicketApp,
    string[] ProductNames,
    params string[] ExeNames);

/// <summary>Discover suite executables and start them. Nothing about a person or a company is passed to an app.</summary>
public static class SuiteCatalog
{
    public static IReadOnlyList<SuiteAppDescriptor> Apps { get; } =
    [
        new("MynaHub", "Company hub, projects, and people", "O-Studio", ["MynaHub", "O-Studio"], "O-Studio.exe"),
        new("MynaOps", "Quantities, estimates and billing", "O-CoreOp", ["MynaOps", "O-CoreOp", "AQCCore"], "O-CoreOp.exe", "AQCCore.exe"),
        new("MynaCAD", "Drafting", "O-Draft", ["MynaCAD", "O-Draft", "AAD"], "ODraft.WinUi.exe", "O-Draft.exe", "aad-host.exe"),
        new("Myna3D", "3D / BIM form", "O-Form", ["Myna3D", "MynaMod", "O-Form"], "O-Form.exe"),
    ];

    static string? FindInstallLocationFromRegistry(string productHint)
    {
        const string root = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        using var uninstall = Registry.CurrentUser.OpenSubKey(root);
        if (uninstall == null)
            return null;
        foreach (var sub in uninstall.GetSubKeyNames())
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{root}\{sub}");
            var name = key?.GetValue("DisplayName") as string;
            var loc = key?.GetValue("InstallLocation") as string;
            if (string.IsNullOrWhiteSpace(loc) || string.IsNullOrWhiteSpace(name))
                continue;
            if (name.Contains(productHint, StringComparison.OrdinalIgnoreCase))
                return loc.TrimEnd('\\');
        }

        return null;
    }

    public static string? FindExecutable(SuiteAppDescriptor app)
    {
        foreach (var hint in app.ProductNames)
        {
            var folder = FindInstallLocationFromRegistry(hint);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                foreach (var exe in app.ExeNames)
                {
                    var path = Path.Combine(folder, exe);
                    if (File.Exists(path))
                        return path;
                }
            }
        }

        foreach (var name in app.ProductNames)
        {
            var folder = Registry.GetValue(
                $@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall\{name}",
                "InstallLocation",
                null) as string;
            if (!string.IsNullOrWhiteSpace(folder))
            {
                foreach (var exe in app.ExeNames)
                {
                    var path = Path.Combine(folder, exe);
                    if (File.Exists(path))
                        return path;
                }
            }
        }

        var programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        foreach (var name in app.ProductNames)
        {
            foreach (var exe in app.ExeNames)
            {
                var path = Path.Combine(programs, name, exe);
                if (File.Exists(path))
                    return path;
            }
        }

        if (string.Equals(app.TicketApp, "O-Studio", StringComparison.OrdinalIgnoreCase))
        {
            var beside = Path.Combine(AppContext.BaseDirectory, "O-Studio.exe");
            if (File.Exists(beside))
                return beside;
        }

        return null;
    }

    /// <summary>
    /// True when the app at <paramref name="exePath"/> is already running (same executable file) and its window was brought to the front, so nothing
    /// new needs starting. Each suite app is one window: a second click on its tile, or a second launch request, must not open a second copy.
    /// </summary>
    public static bool FocusRunning(string exePath)
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(exePath);
            var full = Path.GetFullPath(exePath);
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    string? path = null;
                    try { path = p.MainModule?.FileName; } catch { }
                    if (path == null || !string.Equals(Path.GetFullPath(path), full, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (p.HasExited)
                        continue;
                    var h = p.MainWindowHandle;
                    if (h != IntPtr.Zero)
                    {
                        if (IsIconic(h))
                            ShowWindow(h, 9); // SW_RESTORE
                        SetForegroundWindow(h);
                    }

                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        return false;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);

    /// <summary>
    /// Start an app by path with optional arguments. Nothing is passed in its environment: no sign-in, no company and no AI settings (the apps are
    /// independent of MynaHub; MynaOps hosts the AI agent for itself and MynaCAD). An app that is already running is brought to the front instead
    /// (unless it is started with arguments, which must reach the running copy through its own single-instance handling).
    /// </summary>
    public static void StartApp(string exePath, IReadOnlyDictionary<string, string>? extraEnvironment = null, params string[] arguments)
    {
        if (arguments.Length == 0 && FocusRunning(exePath))
            return;
        var start = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
        };
        foreach (var a in arguments)
            start.ArgumentList.Add(a);
        foreach (var (key, value) in extraEnvironment ?? new Dictionary<string, string>())
            start.Environment[key] = value;
        Process.Start(start);
    }
}
