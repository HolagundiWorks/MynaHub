using System.Text.Json;
using Adms.Interop;
using Microsoft.UI.Dispatching;
using Osas.Core;

namespace Adms.LoginWidget;

/// <summary>Shared company session for the tray launcher and sign-in widget.</summary>
internal sealed class SuiteLauncherHost
{
    internal static SuiteLauncherHost Instance { get; } = new();

    internal AdmsSession? Session { get; private set; }
    internal bool CompanyOpen { get; private set; }
    internal bool SignedIn { get; private set; }
    internal bool Locked { get; private set; }
    internal string CompanyName { get; private set; } = "";
    internal string CompanyPath { get; private set; } = "";
    internal string Username { get; private set; } = "";

    internal event Action? Changed;

    internal void BindSession(AdmsSession session) => Session = session;

    internal void UnbindSession()
    {
        Session = null;
        CompanyOpen = false;
        SignedIn = false;
        Locked = false;
        CompanyName = "";
        CompanyPath = "";
        Username = "";
        Changed?.Invoke();
    }

    internal void ApplyStatus(JsonElement status)
    {
        if (status.ValueKind != JsonValueKind.Object
            || !status.TryGetProperty("company", out var company)
            || company.ValueKind != JsonValueKind.Object)
        {
            CompanyOpen = false;
            SignedIn = false;
            Locked = false;
            CompanyName = "";
            CompanyPath = "";
            Username = "";
            Changed?.Invoke();
            return;
        }

        CompanyOpen = true;
        CompanyName = Text(company, "name");
        CompanyPath = Text(company, "path");
        if (!string.IsNullOrWhiteSpace(CompanyPath))
            SuiteJsonHub.RememberCompany(CompanyPath, chosenBy: "O-Studio.Launcher");

        SignedIn = false;
        Locked = false;
        Username = "";

        if (status.TryGetProperty("setup_required", out var setup) && setup.ValueKind == JsonValueKind.True)
        {
            Changed?.Invoke();
            return;
        }

        if (!status.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object)
        {
            Changed?.Invoke();
            return;
        }

        Username = Text(user, "username");
        if (!string.IsNullOrWhiteSpace(CompanyPath))
            SuiteJsonHub.RememberCompany(CompanyPath, Username, "O-Studio.Launcher");

        Locked = status.TryGetProperty("locked", out var lockedFlag) && lockedFlag.ValueKind == JsonValueKind.True;
        SignedIn = !Locked;
        Changed?.Invoke();
    }

    internal bool CanLaunchSiblingApps => Session != null && SignedIn && !string.IsNullOrWhiteSpace(CompanyPath);

    internal string TrayTooltip()
    {
        if (SignedIn && !string.IsNullOrWhiteSpace(CompanyName))
            return $"{CompanyName} — {Username} (O-Studio suite)";
        if (CompanyOpen)
            return $"{CompanyName} — sign in (O-Studio suite)";
        var remembered = SuiteJsonHub.CompanyPath();
        return remembered != null
            ? "O-Studio suite — company remembered, not signed in"
            : "O-Studio suite launcher";
    }

    internal async Task LaunchAppAsync(SuiteAppDescriptor app, DispatcherQueue queue)
    {
        if (Session == null)
            throw new InvalidOperationException("The engine is not loaded.");

        if (string.Equals(app.TicketApp, "O-Studio", StringComparison.OrdinalIgnoreCase))
        {
            await HandOffToStudioAsync(queue);
            return;
        }

        if (!CanLaunchSiblingApps)
            throw new InvalidOperationException("Sign in to your company first (Company sign-in…).");

        var exe = SuiteCatalog.FindExecutable(app)
            ?? throw new InvalidOperationException($"{app.Title} is not installed.");

        string? ticket = null;
        var path = CompanyPath;
        await Task.Run(() =>
        {
            var payload = Session.Call("auth.ticket", new { app = app.TicketApp });
            ticket = SuiteCatalog.ReadTicket(payload);
        });

        if (string.IsNullOrWhiteSpace(ticket))
            throw new InvalidOperationException("Could not issue a sign-in ticket. Sign in again and retry.");

        SuiteCatalog.StartWithTicket(exe, path, ticket!);
    }

    internal async Task HandOffToStudioAsync(DispatcherQueue queue)
    {
        if (Session == null || !SignedIn || string.IsNullOrWhiteSpace(CompanyPath))
            throw new InvalidOperationException("Sign in to your company first.");

        var studio = SuiteCatalog.FindExecutable(SuiteCatalog.Apps[0])
            ?? throw new InvalidOperationException("O-Studio.exe was not found.");

        string? ticket = null;
        var path = CompanyPath;
        await Task.Run(() =>
        {
            var payload = Session!.Call("auth.ticket", new { app = "O-Studio" });
            ticket = SuiteCatalog.ReadTicket(payload);
            Session.CloseCompany();
        });

        if (string.IsNullOrWhiteSpace(ticket))
            throw new InvalidOperationException("Could not issue a sign-in ticket.");

        Session!.Dispose();
        Session = null;
        CompanyOpen = false;
        SignedIn = false;
        Changed?.Invoke();
        SuiteCatalog.StartWithTicket(studio, path, ticket!);
    }

    static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
