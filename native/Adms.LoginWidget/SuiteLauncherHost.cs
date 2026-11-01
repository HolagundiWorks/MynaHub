using System.Text.Json;
using Adms.Interop;
using Microsoft.UI.Dispatching;
using Osas.Core;

namespace Adms.LoginWidget;

/// <summary>One line of the project drop-down.</summary>
internal sealed record ProjectEntry(long Id, string Code, string Name, string Client, string Status)
{
    public override string ToString() => $"{Code}  {Name}  ({Client}){(Status == "active" ? "" : " - " + Status.Replace('_', ' '))}";
}

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
    internal string CompanyUuid { get; private set; } = "";
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
        CompanyUuid = Text(company, "uuid");
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

    /// <summary>The project locked for this company, or none.</summary>
    internal SelectedProject? Project => string.IsNullOrWhiteSpace(CompanyPath) ? null : SelectedProjectStore.Read(CompanyPath);

    internal string ProjectLine() => Project is { } p ? $"{p.Code} {p.Name} (loaded)" : "none loaded";

    /// <summary>The apps that work on the locked project and are running now, by name.</summary>
    internal static IReadOnlyList<string> AppsHoldingProject()
    {
        var held = new List<string>();
        foreach (var app in SuiteCatalog.Apps)
        {
            if (string.Equals(app.TicketApp, "O-Studio", StringComparison.OrdinalIgnoreCase))
                continue;
            if (app.ExeNames.Any(exe => System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)).Length > 0))
                held.Add(app.Title);
        }

        return held;
    }

    internal async Task<IReadOnlyList<ProjectEntry>> ListProjectsAsync()
    {
        if (Session == null || !CanLaunchSiblingApps)
            return [];
        var rows = await Task.Run(() => Session.Call("project_list", new { client_id = (long?)null, include_archived = false }).Clone());
        var list = new List<ProjectEntry>();
        if (rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in rows.EnumerateArray())
            {
                if (row.TryGetProperty("id", out var id) && id.TryGetInt64(out var n))
                    list.Add(new ProjectEntry(n, Text(row, "code"), Text(row, "name"), Text(row, "client_name"), Text(row, "status")));
            }
        }

        return list;
    }

    /// <summary>Lock a project for the suite. Refused while O-CoreOp or O-Draft is open on a different project. Makes the project's folder when it has none.</summary>
    internal async Task<SelectedProject> LockProjectAsync(ProjectEntry entry)
    {
        if (Session == null || !CanLaunchSiblingApps)
            throw new InvalidOperationException("Sign in to your company first.");
        var current = Project;
        if (current != null && current.ProjectId != entry.Id)
            RefuseWhileAppsOpen(current);

        var path = CompanyPath;
        var user = Username;
        var locked = await Task.Run(() =>
        {
            var info = Session.Call("project_launch_info", new { project_id = entry.Id }).Clone();
            if (info.TryGetProperty("archived", out var archived) && archived.ValueKind == JsonValueKind.True)
                throw new InvalidOperationException($"{entry.Name} is archived. Restore it in O-Studio first.");
            if (!HasWorkspace(info))
                info = Session.Call("project_workspace_ensure", new { project_id = entry.Id }).Clone() is var made && HasWorkspace(made) ? Wrap(made) : info;
            var osp = info.TryGetProperty("workspace", out var ws) && ws.ValueKind == JsonValueKind.Object ? Text(ws, "osp") : "";
            if (osp.Length == 0)
                throw new InvalidOperationException($"{entry.Name} has no project folder and one could not be made.");
            var project = new SelectedProject(path, entry.Id, entry.Code, entry.Name, osp, user, DateTimeOffset.UtcNow);
            SelectedProjectStore.Lock(project);
            return project;
        });
        Changed?.Invoke();
        return locked;
    }

    /// <summary>Release the lock. Refused while O-CoreOp or O-Draft is open on it.</summary>
    internal void UnlockProject()
    {
        if (Project is { } current)
            RefuseWhileAppsOpen(current);
        SelectedProjectStore.Clear();
        Changed?.Invoke();
    }

    static void RefuseWhileAppsOpen(SelectedProject current)
    {
        var held = AppsHoldingProject();
        if (held.Count > 0)
            throw new InvalidOperationException($"{string.Join(" and ", held)} still {(held.Count == 1 ? "has" : "have")} {current.Name} open. Close {(held.Count == 1 ? "it" : "them")} before closing or changing the project.");
    }

    static bool HasWorkspace(JsonElement info) =>
        info.ValueKind == JsonValueKind.Object && info.TryGetProperty("workspace", out var ws) && ws.ValueKind == JsonValueKind.Object;

    /// <summary><c>project_workspace_ensure</c> answers with the workspace alone; wrap it like <c>project_launch_info</c> does.</summary>
    static JsonElement Wrap(JsonElement workspace) =>
        JsonDocument.Parse("{\"workspace\":" + workspace.GetRawText() + "}").RootElement.Clone();

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
        if (Project == null)
            throw new InvalidOperationException($"Load a project first (Company sign-in…, Project). {app.Title} opens on the loaded project.");

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

    /// <summary>Start the app that asked for the launcher (<see cref="LaunchRequest"/>) once a company is signed in and a project is locked.</summary>
    internal async Task RunPendingLaunchAsync(DispatcherQueue queue)
    {
        var request = LaunchRequest.Read();
        if (request == null || !CanLaunchSiblingApps)
            return;
        if (request.Osp == null && Project == null)
            return;
        var descriptor = SuiteCatalog.Apps.FirstOrDefault(a =>
            string.Equals(a.Title, request.App, StringComparison.OrdinalIgnoreCase) || string.Equals(a.TicketApp, request.App, StringComparison.OrdinalIgnoreCase));
        LaunchRequest.Clear();
        if (descriptor == null)
            return;
        try
        {
            // A double-clicked project file: lock the project whose folder holds it, then start the app on it.
            if (request.Osp != null)
                await LockProjectOfFileAsync(request.Osp);
            await LaunchAppAsync(descriptor, queue);
        }
        catch (Exception ex)
        {
            CompanyLoginWidget.ShowError(ex.Message);
        }
    }

    /// <summary>Lock the project whose project file is <paramref name="osp"/>. Says so when no project of this company has that file.</summary>
    internal async Task LockProjectOfFileAsync(string osp)
    {
        if (Session == null || !CanLaunchSiblingApps)
            throw new InvalidOperationException("Sign in to your company first.");
        var full = Path.GetFullPath(osp);
        var entries = await ListProjectsAsync();
        var match = await Task.Run(() => entries.FirstOrDefault(entry =>
        {
            var ws = Session.Call("project_workspace_get", new { project_id = entry.Id }).Clone();
            return ws.ValueKind == JsonValueKind.Object
                && string.Equals(Path.GetFullPath(Text(ws, "osp")), full, StringComparison.OrdinalIgnoreCase);
        }));
        if (match == null)
            throw new InvalidOperationException($"{Path.GetFileName(osp)} is not the project file of a project in {CompanyName}. Add it to a project in O-Studio first.");
        await LockProjectAsync(match);
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
