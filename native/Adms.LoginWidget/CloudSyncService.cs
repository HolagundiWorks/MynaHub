using System.Text.Json;
using Adms.Cloud;
using Adms.Interop;
using Microsoft.UI.Dispatching;

namespace Adms.LoginWidget;

/// <summary>
/// The cloud sync (Neon) of the suite runs here, in the launcher, not in O-Studio. It starts by itself once a company is signed in and Neon is
/// set up (<c>neon.env</c>), keeps going while the launcher runs (also after the sign-in window is closed or O-Studio is started), and stops when the
/// user signs out or closes the company in the launcher, or the launcher exits. Which projects sync is the per-project switch on O-Studio's Cloud sync
/// page (<c>cloud-sync.json</c>); the list of projects is read from the company file while the launcher has it open and remembered after that.
/// </summary>
internal sealed class CloudSyncService
{
    internal static CloudSyncService Instance { get; } = new();

    /// <summary>Raised when what <see cref="Describe"/> says has changed, so the status markers do not wait for the next look.</summary>
    internal event Action? Changed;

    string _last = "";

    readonly object _gate = new();
    IReadOnlyList<OspSyncCoordinator.SyncTarget> _targets = [];
    DateTime _targetsAt = DateTime.MinValue;
    DateTime _flagsAt = DateTime.MinValue;
    string _companyUuid = "";
    string _user = "";
    bool _suppressed;
    bool _wasSignedIn;
    int _busy;
    string _problem = "";
    DispatcherQueueTimer? _timer;

    internal void Start(DispatcherQueue queue)
    {
        if (_timer != null)
            return;
        SuiteLauncherHost.Instance.Changed += () => queue.TryEnqueue(async () => await TickAsync());
        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(30);
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();
    }

    /// <summary>The user signed out or closed the company in the launcher: stop, and do not start again until the next sign-in.</summary>
    internal void StopForSignOut()
    {
        _suppressed = true;
        _problem = "";
        _ = StopAsync().ContinueWith(_ => Changed?.Invoke());
    }

    internal static void Stop() => _ = CloudSyncHub.StopAsync();

    internal async Task StopAsync()
    {
        await CloudSyncHub.StopAsync();
        lock (_gate)
            _targets = [];
    }

    /// <summary>One line for the launcher window, the tray menu and the status file.</summary>
    internal string Describe()
    {
        if (!NeonSettings.Load().IsComplete)
            return "not set up (add neon.env, see docs/CLOUD-NEON.md)";
        if (CloudSyncHub.IsRunning)
        {
            var n = TargetsForCycle().Count;
            return n == 0 ? "running, no project is switched on (O-Studio > Cloud sync)" : $"running ({n} project{(n == 1 ? "" : "s")} syncing)";
        }

        if (_suppressed)
            return "stopped (signed out); starts again at the next sign-in";
        return _problem.Length > 0 ? _problem : "credentials found; starts at sign-in";
    }

    async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
            return;
        try
        {
            var host = SuiteLauncherHost.Instance;
            if (host.SignedIn && !_wasSignedIn)
                _suppressed = false;
            _wasSignedIn = host.SignedIn;

            if (host.SignedIn)
            {
                _companyUuid = string.IsNullOrWhiteSpace(host.CompanyUuid) ? host.CompanyPath : host.CompanyUuid;
                _user = host.Username;
                await Task.Run(RefreshTargets);
            }

            if (_suppressed || CloudSyncHub.IsRunning || string.IsNullOrWhiteSpace(_companyUuid) || !NeonSettings.Load().IsComplete)
                return;
            // Start once a company has been signed in during this run of the launcher.
            if (!host.SignedIn && _targetsAt == DateTime.MinValue)
                return;
            _problem = "";
            var started = await CloudSyncHub.StartAsync(new CloudSyncHub.AppShellBridge
            {
                CompanyUuid = _companyUuid,
                UserName = _user,
                ListSyncTargets = TargetsForCycle,
            });
            if (!started)
                _problem = "could not connect (" + (CloudSyncHub.RecentLog.LastOrDefault() ?? "see the log").Trim() + "); retrying every 30 s";
        }
        catch (Exception ex)
        {
            _problem = "error: " + ex.Message;
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
            var now = Describe();
            if (now != _last)
            {
                _last = now;
                Changed?.Invoke();
            }
        }
    }

    /// <summary>Read the project list from the open company file. The last list stays in use while the launcher has no session.</summary>
    void RefreshTargets()
    {
        var session = SuiteLauncherHost.Instance.Session;
        if (session == null || !SuiteLauncherHost.Instance.SignedIn)
            return;
        var list = new List<OspSyncCoordinator.SyncTarget>();
        try
        {
            var rows = session.Call("project_list", new { client_id = (long?)null, include_archived = false }).Clone();
            if (rows.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in rows.EnumerateArray())
                {
                    if (!row.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id))
                        continue;
                    var name = row.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    JsonElement ws;
                    try
                    {
                        ws = session.Call("project_workspace_get", new { project_id = id }).Clone();
                    }
                    catch (AdmsException)
                    {
                        continue;
                    }

                    var osp = ws.ValueKind == JsonValueKind.Object && ws.TryGetProperty("osp", out var o) ? o.GetString() ?? "" : "";
                    if (osp.Length > 0)
                        list.Add(new OspSyncCoordinator.SyncTarget(id, name, osp));
                }
            }
        }
        catch (Exception ex) when (ex is AdmsException or InvalidOperationException)
        {
            return;
        }

        lock (_gate)
        {
            _targets = list;
            _targetsAt = DateTime.UtcNow;
        }
    }

    /// <summary>The projects the sync works on now: those with the switch on in <c>cloud-sync.json</c>, which O-Studio's page changes while this runs.</summary>
    IReadOnlyList<OspSyncCoordinator.SyncTarget> TargetsForCycle()
    {
        // The coordinator saves its own copy of the store; keep the switches in it equal to the file so a save here never undoes a change made in O-Studio.
        if (DateTime.UtcNow - _flagsAt > TimeSpan.FromSeconds(4))
        {
            _flagsAt = DateTime.UtcNow;
            var fresh = CloudSyncStore.Load(_companyUuid);
            var mine = CloudSyncHub.Store;
            foreach (var id in mine.Projects.Keys.Union(fresh.Projects.Keys).ToList())
            {
                var state = mine.Get(id);
                state.Enabled = fresh.Get(id).Enabled;
                mine.Projects[id] = state;
            }
        }

        CloudSyncHub.UseCompany(_companyUuid);
        IReadOnlyList<OspSyncCoordinator.SyncTarget> all;
        lock (_gate)
            all = _targets;
        return all.Where(t => CloudSyncHub.Store.Get(t.StudioProjectId).Enabled).ToList();
    }
}
