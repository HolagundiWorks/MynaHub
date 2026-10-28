using System.Diagnostics;
using System.Text.Json;
using Adms.Interop;

namespace Adms.Desktop;

/// <summary>
/// Studio as the suite hub: remembers the chosen company for sibling apps and launches them
/// with <c>OSAS_COMPANY</c> + a one-time <c>OSAS_TICKET</c>. Path and env names stay OSAS for
/// compatibility with CoreOp, Draft and Form.
/// </summary>
internal static class SuiteHub
{
    internal static string SuiteDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OSAS");

    internal static string SuiteFile => Path.Combine(SuiteDirectory, "suite.json");

    /// <summary>Known suite apps launched from Studio cards. Discovery is best-effort.</summary>
    internal static IReadOnlyList<SuiteCard> Cards { get; } =
    [
        new("O-CoreOp", "Quantities, estimates and billing", "O-CoreOp", ["O-CoreOp", "AQCCore"], "O-CoreOp.exe", "AQCCore.exe"),
        new("O-Draft", "Drafting", "O-Draft", ["O-Draft", "AAD"], "O-Draft.exe", "aad-host.exe"),
        new("O-Form", "3D / BIM form", "O-Form", ["O-Form"], "O-Form.exe"),
    ];

    internal static string? CompanyPath()
    {
        try
        {
            if (!File.Exists(SuiteFile))
                return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(SuiteFile));
            return doc.RootElement.TryGetProperty("company_path", out var p)
                && p.GetString() is { Length: > 0 } path
                && File.Exists(path)
                ? path
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Records the company Studio has open so sibling apps can follow without a password.</summary>
    internal static void RememberCompany(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            Directory.CreateDirectory(SuiteDirectory);
            var payload = JsonSerializer.Serialize(new
            {
                company_path = path,
                chosen_by = "O-Studio",
                chosen_at = DateTimeOffset.UtcNow.ToString("O"),
            });
            File.WriteAllText(SuiteFile, payload);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sibling apps can still be launched with an explicit path+ticket in this session.
        }
    }

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

    /// <summary>
    /// Issues a suite ticket and starts the app with company path + ticket in the environment.
    /// Does not put the password on the command line.
    /// </summary>
    internal static void Launch(AdmsSession session, SuiteCard card, string companyPath)
    {
        if (string.IsNullOrWhiteSpace(companyPath) || !File.Exists(companyPath))
            throw new InvalidOperationException("Open a company in Studio first.");
        var exe = FindExecutable(card)
            ?? throw new InvalidOperationException($"{card.Title} is not installed. Install it, then open it from Studio.");
        var ticket = session.Call("auth.ticket", new { app = card.TicketApp });
        var secret = ticket.ValueKind == JsonValueKind.Object && ticket.TryGetProperty("ticket", out var t)
            ? t.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException("Studio could not issue a sign-in ticket. Sign in again and retry.");

        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
        };
        start.Environment["OSAS_COMPANY"] = companyPath;
        start.Environment["OSAS_TICKET"] = secret;
        Process.Start(start);
    }
}

internal sealed record SuiteCard(
    string Title,
    string Blurb,
    string TicketApp,
    string[] ProductNames,
    params string[] ExeNames);
