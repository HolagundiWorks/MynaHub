using System.Diagnostics;
using System.Text.Json;
using Adms.Interop;
using Osas.Core;

namespace Adms.Desktop;

/// <summary>
/// MynaHub as the suite hub: remembers the chosen company and starts MynaOps, MynaCAD and MynaMod by path. They are independent of MynaHub: no sign-in
/// ticket, no company path, no locked project and no AI settings are passed (MynaOps hosts the AI agent for itself and MynaCAD).
/// </summary>
internal static class SuiteHub
{
    internal static string SuiteDirectory => SuiteWire.LocalDirectory;

    internal static string SuiteFile => SuiteJsonHub.SuiteFile;

    /// <summary>Known suite apps launched from Studio cards. Discovery is best-effort.</summary>
    internal static IReadOnlyList<SuiteCard> Cards { get; } =
    [
        new("MynaOps", "Quantities, estimates and billing", "O-CoreOp", ["O-CoreOp", "AQCCore"], "O-CoreOp.exe", "AQCCore.exe"),
        new("MynaCAD", "Drafting", "O-Draft", ["O-Draft", "AAD"], "ODraft.WinUi.exe", "O-Draft.exe"),
        new("MynaMod", "3D / BIM form", "O-Form", ["O-Form"], "O-Form.exe"),
    ];

    internal static string? CompanyPath() => SuiteJsonHub.CompanyPath();

    /// <summary>Last signed-in username remembered for the suite (never a password).</summary>
    internal static string? RememberedUsername() => SuiteJsonHub.RememberedUsername();

    /// <summary>Records the company Studio has open so sibling apps can follow without a password. Username is optional and never a secret.</summary>
    internal static void RememberCompany(string path, string? username = null) =>
        SuiteJsonHub.RememberCompany(path, username);

    internal static string? FindExecutable(SuiteCard card)
    {
        foreach (var name in card.ProductNames)
        {
            var folder = Microsoft.Win32.Registry.GetValue(
                $@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall\{name}",
                "InstallLocation",
                null) as string;
            if (!string.IsNullOrWhiteSpace(folder))
            {
                foreach (var exe in card.ExeNames)
                {
                    var path = Path.Combine(folder, exe);
                    if (File.Exists(path))
                        return path;
                }
            }
        }

        var programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        foreach (var name in card.ProductNames)
        {
            foreach (var exe in card.ExeNames)
            {
                var path = Path.Combine(programs, name, exe);
                if (File.Exists(path))
                    return path;
            }
        }

        return null;
    }

    /// <summary>Starts the app. Nothing about the company, a person or the AI agent is passed to it.</summary>
    internal static void Launch(SuiteCard card)
    {
        var exe = FindExecutable(card)
            ?? throw new InvalidOperationException($"{card.Title} is not installed. Install it, then open it from Studio.");
        if (SuiteCatalog.FocusRunning(exe))
            return;

        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
        };
        Process.Start(start);
    }
}

internal sealed record SuiteCard(
    string Title,
    string Blurb,
    string TicketApp,
    string[] ProductNames,
    params string[] ExeNames);
