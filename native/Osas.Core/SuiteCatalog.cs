using System.Diagnostics;
using System.Text.Json;

namespace Osas.Core;

/// <summary>One installable app in the O-Studio suite (launcher cards and tray menu).</summary>
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
        new("O-Studio", "Company hub, projects, and people", "O-Studio", ["O-Studio"], "O-Studio.exe"),
        new("O-CoreOp", "Quantities, estimates and billing", "O-CoreOp", ["O-CoreOp", "AQCCore"], "O-CoreOp.exe", "AQCCore.exe"),
        new("O-Draft", "Drafting", "O-Draft", ["O-Draft", "AAD"], "O-Draft.exe", "aad-host.exe"),
        new("O-Form", "3D / BIM form", "O-Form", ["O-Form"], "O-Form.exe"),
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

    public static string? FindExecutable(SuiteAppDescriptor app)
    {
        foreach (var name in app.ProductNames)
        {
            var folder = Microsoft.Win32.Registry.GetValue(
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
    }

    public static void StartWithTicket(string exePath, string companyPath, string ticket)
    {
        var start = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
        };
        ApplySuiteLaunchEnvironment(start, companyPath, ticket);
        Process.Start(start);
    }
}
