using System.Text.Json;
using Adms.Cloud;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Office.Shared;

namespace Adms.Desktop;

/// <summary>Suite → Cloud sync: Neon Postgres + Object Storage for `.osp` project files.</summary>
internal sealed class CloudSyncPage : UserControl
{
    readonly AppShell _shell;
    readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Style = Resource("BodyStyle") };
    readonly TextBlock _log = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Style = Resource("SecondaryStyle") };
    readonly StackPanel _projects = new() { Spacing = 8 };
    readonly Action<string> _onLog;

    public CloudSyncPage(AppShell shell)
    {
        _shell = shell;
        // The switches and revisions on this page belong to the company that is open, not to every company file used on this PC.
        CloudSyncHub.UseCompany(string.IsNullOrWhiteSpace(shell.CompanyUuid) ? shell.CompanyPath : shell.CompanyUuid);
        _onLog = _ => DispatcherQueue.TryEnqueue(RefreshLog);
        var title = new TextBlock { Text = "Cloud sync (Neon)", Style = Resource("PageTitleStyle") };
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);

        CloudSyncHub.LogLine += _onLog;

        var page = new StackPanel { Padding = O.Ui.SuiteScale.PageMargin, Spacing = O.Ui.SuiteScale.SectionGap };
        page.Children.Add(title);
        page.Children.Add(new TextBlock
        {
            Text = "Cloud sync uses service credentials in %LocalAppData%\\O-Studio\\neon.env (Postgres + Object Storage from neon deploy). "
                   + "That is separate from Neon Auth sign-in — there is no login screen here yet; use an env file or paste keys from the Neon Console. "
                   + "Your Downloads storage-only export still needs DATABASE_URL from the full branch env.",
            Style = Resource("SecondaryStyle"),
            TextWrapping = TextWrapping.Wrap,
        });
        page.Children.Add(_status);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var loadEnv = new Button { Content = "Load env file…", MinHeight = O.Ui.SuiteScale.ControlMinHeight };
        loadEnv.Click += async (_, _) => await LoadEnvFileAsync();
        var test = new Button { Content = "Test connection", MinHeight = O.Ui.SuiteScale.ControlMinHeight };
        test.Click += async (_, _) => await TestAsync();
        var refreshRev = new Button { Content = "Refresh revisions", MinHeight = O.Ui.SuiteScale.ControlMinHeight };
        refreshRev.Click += (_, _) => _ = ReloadProjectsAsync();
        actions.Children.Add(loadEnv);
        actions.Children.Add(test);
        actions.Children.Add(refreshRev);
        page.Children.Add(actions);

        page.Children.Add(new TextBlock { Text = "Projects", Style = Resource("SectionTitleStyle") });
        page.Children.Add(_projects);
        page.Children.Add(new TextBlock { Text = "Activity", Style = Resource("SectionTitleStyle") });
        page.Children.Add(new ScrollViewer { MaxHeight = 220, Content = _log });

        Content = new ScrollViewer { Content = page };
        Loaded += (_, _) =>
        {
            _ = ReloadProjectsAsync();
            RefreshLog();
            _status.Text = LauncherLine();
        };
        Unloaded += (_, _) => CloudSyncHub.LogLine -= _onLog;
    }

    static Style Resource(string key) => (Style)Application.Current.Resources[key];

    CloudSyncHub.AppShellBridge Bridge() => new()
    {
        CompanyUuid = string.IsNullOrWhiteSpace(_shell.CompanyUuid) ? _shell.CompanyPath : _shell.CompanyUuid,
        UserName = _shell.User,
        ListSyncTargets = ListTargets,
    };

    IReadOnlyList<OspSyncCoordinator.SyncTarget> ListTargets()
    {
        var list = new List<OspSyncCoordinator.SyncTarget>();
        var rows = _shell.Call("project_list", new { client_id = (long?)null, include_archived = false });
        if (rows.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var row in rows.EnumerateArray())
        {
            var id = row.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var pid) ? pid : 0;
            if (id == 0)
                continue;
            var name = Kit.Str(row, "name");
            JsonElement ws;
            try
            {
                ws = _shell.Call("project_workspace_get", new { project_id = id });
            }
            catch (Adms.Interop.AdmsException)
            {
                continue;
            }
            var osp = Kit.Str(ws, "osp");
            if (string.IsNullOrWhiteSpace(osp))
                continue;
            var store = CloudSyncHub.Store.Get(id);
            if (!store.Enabled)
                continue;
            list.Add(new OspSyncCoordinator.SyncTarget(id, name, osp));
        }
        return list;
    }

    async Task ReloadProjectsAsync()
    {
        try
        {
            _projects.Children.Clear();
            var store = CloudSyncHub.Store;
            var rows = _shell.Call("project_list", new { client_id = (long?)null, include_archived = false });
            if (rows.ValueKind != JsonValueKind.Array)
            {
                _projects.Children.Add(new TextBlock { Text = "No projects.", Style = Resource("SecondaryStyle") });
                return;
            }

            var settingsOk = NeonSettings.Load().IsComplete;
            foreach (var row in rows.EnumerateArray())
            {
                var id = row.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var pid) ? pid : 0;
                if (id == 0)
                    continue;
                var name = Kit.Str(row, "name");
                JsonElement ws;
                try
                {
                    ws = _shell.Call("project_workspace_get", new { project_id = id });
                }
                catch (Adms.Interop.AdmsException)
                {
                    ws = default;
                }
                var osp = ws.ValueKind == JsonValueKind.Object ? Kit.Str(ws, "osp") : "";
                var state = store.Get(id);
                var rowUi = new StackPanel { Spacing = 4, Padding = new Thickness(0, 4, 0, 4) };
                rowUi.Children.Add(new TextBlock { Text = name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                rowUi.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrEmpty(osp) ? "No suite folder yet — open the project in Studio to create project.osp." : osp,
                    Style = Resource("CaptionStyle"),
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                });

                var revLine = new TextBlock { Style = Resource("CaptionStyle"), TextWrapping = TextWrapping.Wrap };
                rowUi.Children.Add(revLine);
                if (!string.IsNullOrEmpty(osp) && File.Exists(osp))
                {
                    await SetRevisionLineAsync(revLine, osp, state, settingsOk).ConfigureAwait(true);
                }
                else
                {
                    revLine.Text = "Revision: no local .osp yet.";
                }

                var syncToggle = new ToggleSwitch { IsOn = state.Enabled, OffContent = "Sync off", OnContent = "Sync on" };
                var projectId = id;
                syncToggle.Toggled += (_, _) =>
                {
                    var s = store.Get(projectId);
                    s.Enabled = syncToggle.IsOn;
                    s.OspPath = osp;
                    store.Projects[projectId] = s;
                    store.Save();
                };
                rowUi.Children.Add(syncToggle);

                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var push = new Button { Content = "Push now", IsEnabled = !string.IsNullOrEmpty(osp) };
                push.Click += async (_, _) => await PushProjectAsync(projectId, name, osp, revLine);
                var pull = new Button { Content = "Pull now", IsEnabled = !string.IsNullOrEmpty(osp) };
                pull.Click += async (_, _) => await PullProjectAsync(projectId, osp, revLine);
                buttons.Children.Add(push);
                buttons.Children.Add(pull);
                rowUi.Children.Add(buttons);
                _projects.Children.Add(rowUi);
            }
        }
        catch (Exception ex)
        {
            _status.Text = "Could not load projects: " + ex.Message;
            _shell.Problem(_status.Text);
        }
    }

    async Task SetRevisionLineAsync(TextBlock revLine, string osp, CloudSyncStore.ProjectSyncState state, bool settingsOk)
    {
        try
        {
            long localRev;
            using (var odp = OdpFile.Open(osp))
                localRev = odp.Meta().Revision;
            state.LastLocalRevision = localRev;
            long? cloudRev = null;
            if (settingsOk)
            {
                var head = await CloudSyncHub.GetCloudHeadForOspAsync(osp).ConfigureAwait(true);
                cloudRev = head?.LatestRevision;
                if (cloudRev is not null)
                    state.LastKnownCloudRevision = cloudRev.Value;
            }
            var cloudText = cloudRev is null ? "not in cloud yet" : cloudRev.Value.ToString();
            revLine.Text = $"Local revision {localRev} · Cloud {cloudText}";
            if (cloudRev is long cloud && cloud > localRev)
            {
                revLine.Foreground = Application.Current.Resources.TryGetValue("WarningBrush", out var w) && w is Brush wb
                    ? wb
                    : new SolidColorBrush(Microsoft.UI.Colors.DarkOrange);
            }
        }
        catch (Exception ex)
        {
            revLine.Text = "Revision: " + ex.Message;
        }
    }

    async Task PushProjectAsync(long projectId, string name, string osp, TextBlock revLine)
    {
        var r = await CloudSyncHub.PushNowAsync(projectId, osp, Bridge()).ConfigureAwait(true);
        if (!r.Ok)
        {
            var resolved = await CloudSyncConflictDialog.TryResolveCloudAheadAsync(XamlRoot, projectId, name, osp, r.Message);
            _status.Text = resolved ?? r.Message;
            if (resolved is not null)
                await ReloadProjectsAsync();
            return;
        }
        _status.Text = r.Message;
        if (r.Ok && r.NewRevision is long nr)
        {
            var store = CloudSyncHub.Store;
            var s = store.Get(projectId);
            s.LastKnownCloudRevision = nr;
            s.LastLocalRevision = nr;
            store.Projects[projectId] = s;
            store.Save();
            await SetRevisionLineAsync(revLine, osp, s, NeonSettings.Load().IsComplete);
        }
    }

    async Task PullProjectAsync(long projectId, string osp, TextBlock revLine)
    {
        var r = await CloudSyncHub.PullNowAsync(projectId, osp).ConfigureAwait(true);
        _status.Text = r.Message;
        if (r.Ok && r.NewRevision is long nr)
        {
            var store = CloudSyncHub.Store;
            var s = store.Get(projectId);
            s.LastKnownCloudRevision = nr;
            s.LastLocalRevision = nr;
            store.Projects[projectId] = s;
            store.Save();
            await SetRevisionLineAsync(revLine, osp, s, NeonSettings.Load().IsComplete);
        }
    }

    void ReloadProjects()
    {
        _ = ReloadProjectsAsync();
    }

    static string DescribeConfigState()
    {
        var missing = NeonEnvFile.MissingForSync();
        if (missing.Count == 0)
            return "Sync is off. Credentials loaded — turn on background sync or use Test connection.";
        return "Sync is off. Missing in neon.env: " + string.Join(", ", missing) + ". Use Load env file… (merge repo .env.local + storage export).";
    }

    async Task LoadEnvFileAsync()
    {
        try
        {
            var path = await FileDialogs.PickFile(_shell.Window.Handle, "*", ".env", ".local");
            if (string.IsNullOrWhiteSpace(path))
                return;
            NeonEnvFile.MergeIntoDefault(path);
            _status.Text = "Merged " + Path.GetFileName(path) + " into neon.env. " + DescribeConfigState();
            await ReloadProjectsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _shell.Problem("Could not load env file. " + ex.Message);
        }
    }

    async Task TestAsync()
    {
        _status.Text = "Testing…";
        try
        {
            var (ok, msg) = await CloudSyncHub.TestConnectionAsync().ConfigureAwait(true);
            _status.Text = msg;
            if (!ok)
                _shell.Problem(msg);
        }
        catch (Exception ex)
        {
            var msg = "Test connection failed. " + ex.Message;
            _status.Text = msg;
            _shell.Problem(msg);
        }
    }

    /// <summary>The sync runs in the launcher; this page sets it up. The launcher writes its state to <c>launcher-status.txt</c>.</summary>
    static string LauncherLine()
    {
        const string head = "Background sync runs in the suite launcher, from sign-in on. ";
        try
        {
            var file = Osas.Core.SuiteWire.LocalPath("launcher-status.txt");
            if (File.Exists(file) && File.ReadLines(file).FirstOrDefault(l => l.StartsWith("Cloud sync:", StringComparison.Ordinal)) is { } line)
                return head + line;
        }
        catch (IOException)
        {
        }

        return head + "Start the launcher to see its state. " + DescribeConfigState();
    }
    void RefreshLog()
    {
        _log.Text = CloudSyncHub.RecentLog.Count == 0
            ? "(no activity yet)"
            : string.Join(Environment.NewLine, CloudSyncHub.RecentLog);
    }
}
