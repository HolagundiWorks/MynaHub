using System.Text.Json;
using Adms.Cloud;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Adms.Desktop;

/// <summary>Suite → Cloud sync: Neon Postgres + Object Storage for `.osp` project files.</summary>
internal sealed class CloudSyncPage : UserControl
{
    readonly AppShell _shell;
    readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Style = Resource("BodyStyle") };
    readonly TextBlock _log = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Style = Resource("SecondaryStyle") };
    readonly StackPanel _projects = new() { Spacing = 8 };
    readonly ToggleSwitch _run = new() { OffContent = "Off", OnContent = "On" };
    readonly Action<string> _onLog;

    public CloudSyncPage(AppShell shell)
    {
        _shell = shell;
        _onLog = _ => DispatcherQueue.TryEnqueue(RefreshLog);
        var title = new TextBlock { Text = "Cloud sync (Neon)", Style = Resource("PageTitleStyle") };
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);

        _run.Toggled += async (_, _) => await OnRunToggledAsync();
        CloudSyncHub.LogLine += _onLog;

        var page = new StackPanel { Padding = Hcw.Ui.SuiteScale.PageMargin, Spacing = Hcw.Ui.SuiteScale.SectionGap };
        page.Children.Add(title);
        page.Children.Add(new TextBlock
        {
            Text = "O-Studio holds Neon credentials and syncs project.osp files. O-Draft and O-CoreOp keep using the local file; they pick up new revisions automatically.",
            Style = Resource("SecondaryStyle"),
            TextWrapping = TextWrapping.Wrap,
        });
        page.Children.Add(_status);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var test = new Button { Content = "Test connection", MinHeight = Hcw.Ui.SuiteScale.ControlMinHeight };
        test.Click += async (_, _) => await TestAsync();
        actions.Children.Add(test);
        actions.Children.Add(new TextBlock { Text = "Background sync", VerticalAlignment = VerticalAlignment.Center, Style = Resource("CaptionStyle") });
        actions.Children.Add(_run);
        page.Children.Add(actions);

        page.Children.Add(new TextBlock { Text = "Projects", Style = Resource("SectionTitleStyle") });
        page.Children.Add(_projects);
        page.Children.Add(new TextBlock { Text = "Activity", Style = Resource("SectionTitleStyle") });
        page.Children.Add(new ScrollViewer { MaxHeight = 220, Content = _log });

        Content = new ScrollViewer { Content = page };
        Loaded += (_, _) =>
        {
            ReloadProjects();
            RefreshLog();
            _run.IsOn = CloudSyncHub.IsRunning;
            _status.Text = CloudSyncHub.IsRunning ? "Sync is running." : "Sync is off. Turn it on after neon.env is configured.";
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
            var ws = _shell.Call("project_workspace_get", new { project_id = id });
            if (ws.ValueKind == JsonValueKind.Null)
                continue;
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

    void ReloadProjects()
    {
        _projects.Children.Clear();
        var store = CloudSyncHub.Store;
        var rows = _shell.Call("project_list", new { client_id = (long?)null, include_archived = false });
        if (rows.ValueKind != JsonValueKind.Array)
        {
            _projects.Children.Add(new TextBlock { Text = "No projects.", Style = Resource("SecondaryStyle") });
            return;
        }

        foreach (var row in rows.EnumerateArray())
        {
            var id = row.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var pid) ? pid : 0;
            if (id == 0)
                continue;
            var name = Kit.Str(row, "name");
            var ws = _shell.Call("project_workspace_get", new { project_id = id });
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
            push.Click += async (_, _) =>
            {
                var r = await CloudSyncHub.PushNowAsync(projectId, osp);
                _status.Text = r.Message;
                if (r.Ok && r.NewRevision is long nr)
                {
                    var s = store.Get(projectId);
                    s.LastKnownCloudRevision = nr;
                    s.LastLocalRevision = nr;
                    store.Projects[projectId] = s;
                    store.Save();
                }
            };
            var pull = new Button { Content = "Pull now", IsEnabled = !string.IsNullOrEmpty(osp) };
            pull.Click += async (_, _) =>
            {
                var r = await CloudSyncHub.PullNowAsync(projectId, osp);
                _status.Text = r.Message;
                if (r.Ok && r.NewRevision is long nr)
                {
                    var s = store.Get(projectId);
                    s.LastKnownCloudRevision = nr;
                    s.LastLocalRevision = nr;
                    store.Projects[projectId] = s;
                    store.Save();
                }
            };
            buttons.Children.Add(push);
            buttons.Children.Add(pull);
            rowUi.Children.Add(buttons);
            _projects.Children.Add(rowUi);
        }
    }

    async Task TestAsync()
    {
        _status.Text = "Testing…";
        var (ok, msg) = await CloudSyncHub.TestConnectionAsync();
        _status.Text = msg;
        if (!ok)
            _shell.Problem(msg);
    }

    async Task OnRunToggledAsync()
    {
        if (_run.IsOn)
        {
            _status.Text = "Connecting…";
            if (!await CloudSyncHub.StartAsync(Bridge()))
            {
                _run.IsOn = false;
                return;
            }
            _status.Text = "Sync is running.";
        }
        else
        {
            await CloudSyncHub.StopAsync();
            _status.Text = "Sync stopped.";
        }
    }

    void RefreshLog()
    {
        _log.Text = CloudSyncHub.RecentLog.Count == 0
            ? "(no activity yet)"
            : string.Join(Environment.NewLine, CloudSyncHub.RecentLog);
    }
}
