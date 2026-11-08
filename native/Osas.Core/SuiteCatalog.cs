using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace Osas.Core;

/// <summary>One installable app in the Kivo suite (launcher cards and tray menu).</summary>
public sealed record SuiteAppDescriptor(
    string Title,
    string Blurb,
    string TicketApp,
    string[] ProductNames,
    params string[] ExeNames);

/// <summary>Discover suite executables and start them with a one-time ticket (never a password on the command line).</summary>
public static class SuiteCatalog
{
    public static IReadOnlyList<SuiteAppDescriptor> Apps { get; } =
    [
        new("Kivo", "Company hub, projects, and people", "O-Studio", ["O-Studio"], "O-Studio.exe"),
        new("Falcon", "Quantities, estimates and billing", "O-CoreOp", ["O-CoreOp", "AQCCore"], "O-CoreOp.exe", "AQCCore.exe"),
        new("Tren", "Drafting", "O-Draft", ["O-Draft", "AAD"], "ODraft.WinUi.exe", "O-Draft.exe", "aad-host.exe"),
        new("Finch", "3D / BIM form", "O-Form", ["O-Form"], "O-Form.exe"),
    ];

    public static SuiteAppDescriptor? FindByTicketApp(string ticketApp)
    {
        foreach (var app in Apps)
        {
            if (string.Equals(app.TicketApp, ticketApp, StringComparison.OrdinalIgnoreCase))
                return app;
        }

        return null;
    }

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

    public static string? ReadTicket(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("ticket", out var t)
            ? t.GetString()
            : null;

    public static void ApplySuiteLaunchEnvironment(ProcessStartInfo start, string companyPath, string ticket)
    {
        start.Environment[SuiteWire.EnvCompany] = companyPath;
        start.Environment[SuiteWire.EnvTicket] = ticket;
        start.Environment[SuiteWire.EnvAiRole] = "client";
        start.Environment[SuiteWire.EnvAiHost] = "127.0.0.1";
        start.Environment[SuiteWire.EnvAiPort] = "8742";
        start.Environment[SuiteWire.EnvDbUrl] = SuiteWire.DbUrl;
        foreach (var (key, value) in SelectedProjectStore.EnvironmentFor(companyPath))
            start.Environment[key] = value;
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

    /// <summary>The environment every app the launcher starts gets: the AI Core and the database service on loopback. No company, no ticket, no password.</summary>
    public static void ApplyServiceEnvironment(ProcessStartInfo start)
    {
        start.Environment[SuiteWire.EnvAiRole] = "client";
        start.Environment[SuiteWire.EnvAiHost] = "127.0.0.1";
        start.Environment[SuiteWire.EnvAiPort] = "8742";
        start.Environment[SuiteWire.EnvDbUrl] = SuiteWire.DbUrl;
    }

    /// <summary>
    /// Start an app with the service environment and optional arguments, no sign-in: each app asks Kivo for a session itself when it needs the database.
    /// An app that is already running is brought to the front instead (unless it is started with arguments, for example Kivo's <c>--broker --signin</c>, which
    /// must reach the running copy through its own single-instance handling).
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
        ApplyServiceEnvironment(start);
        foreach (var (key, value) in extraEnvironment ?? new Dictionary<string, string>())
            start.Environment[key] = value;
        Process.Start(start);
    }

    public static void StartWithTicket(string exePath, string companyPath, string ticket)
    {
        if (FocusRunning(exePath))
            return;
        var start = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
        };
        ApplySuiteLaunchEnvironment(start, companyPath, ticket);
        Process.Start(start);
    }
}
