using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Adms.Desktop;

/// <summary>
/// The optional link to AORMS, the studio's web portal. Signed out, AOS makes no network call. Signed in, a portal project can be
/// pulled into the office file as a client and a project. The password is used once to sign in and is never stored; the sign-in is
/// the suite's shared one, so the other desktop apps on this computer use it too.
/// </summary>
internal sealed class AormsPage : UserControl
{
    private readonly AppShell _shell;
    private readonly TextBlock _status = new() { Style = Flow.Look("BodyStyle"), TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _email = Flow.Input("AORMS e-mail");
    private readonly PasswordBox _password = new() { Header = "Password" };
    private readonly TextBox _server = Flow.Input("Server address (leave empty for aorms.in)");
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly StackPanel _projects = new() { Spacing = 0 };
    private readonly InfoBar _notice = Flow.Notice();
    private string _state = "signed_out";

    public AormsPage(AppShell shell)
    {
        _shell = shell;
        var page = new StackPanel { Spacing = 12, Margin = new Thickness(8) };
        page.Children.Add(Flow.SheetMark("06.05", "Office", "AORMS"));
        page.Children.Add(Flow.Heading("AORMS portal"));
        page.Children.Add(Flow.Secondary("Optional. AOS works fully without it and makes no network call until you sign in here. Signed in, you can pull a portal project into this company file as a client and a project. Nothing is sent to the portal, and nothing local is deleted."));
        page.Children.Add(_notice);
        page.Children.Add(_status);
        page.Children.Add(Flow.Pair(_email, _password));
        page.Children.Add(_server);
        page.Children.Add(_buttons);
        page.Children.Add(Flow.Section("Portal projects"));
        page.Children.Add(_projects);
        Content = new ScrollViewer { Content = page };
        Paint();
        Check();
    }

    private static string Server(string text) => text.Trim();

    private void Say(InfoBarSeverity severity, string message)
    {
        _notice.Severity = severity;
        _notice.Message = message;
        _notice.IsOpen = true;
    }

    private void Paint(JsonElement? state = null)
    {
        if (state is { } s)
        {
            _state = Kit.Str(s, "state");
            _status.Text = _state switch
            {
                "connected" => $"Connected as {Kit.Str(s, "email")} ({Kit.Str(s, "role")}), {Kit.Str(s, "studio")}, {Kit.Str(s, "plan")} plan.",
                "needs_connect" => $"{Kit.Str(s, "email")} is signed in to AORMS on this computer through another app. Connect AOS to use it here.",
                _ => "Not signed in. AOS is working on its own.",
            };
        }
        else
            _status.Text = "Checking…";
        var signedIn = _state is "connected" or "needs_connect";
        _email.IsEnabled = _password.IsEnabled = _server.IsEnabled = !signedIn;
        _buttons.Children.Clear();
        if (!_shell.CanEdit)
            return;
        if (!signedIn)
            _buttons.Children.Add(Flow.Command("Sign in", SignIn, primary: true));
        if (_state == "needs_connect")
            _buttons.Children.Add(Flow.Command("Connect AOS", Connect, primary: true));
        if (_state == "connected")
            _buttons.Children.Add(Flow.Command("Load projects", LoadProjects, primary: true));
        if (signedIn)
            _buttons.Children.Add(Flow.Command("Sign out of AORMS", SignOut));
        _buttons.Children.Add(Flow.Command("Check again", Check));
    }

    private object ServerArg() => new { base_url = Server(_server.Text).Length == 0 ? null : Server(_server.Text) };

    private void Check()
    {
        _ = _shell.Run("Checking AORMS…", () =>
        {
            JsonElement? result = null;
            try { result = _shell.Call("aorms_restore", ServerArg()); }
            catch (Exception) { /* the shell reports a failed call; an unreachable portal leaves the page signed out */ }
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                if (result is { } r)
                {
                    Paint(r);
                    if (Kit.Str(r, "state") == "connected")
                        LoadProjects();
                }
                else
                    Paint(JsonDocument.Parse("{\"state\":\"signed_out\"}").RootElement.Clone());
            });
        });
    }

    private void SignIn()
    {
        var email = _email.Text.Trim();
        var password = _password.Password;
        _password.Password = "";
        if (email.Length == 0 || password.Length == 0)
        {
            _shell.Problem("Enter your AORMS e-mail and password.");
            return;
        }
        _ = _shell.Run("Signing in to AORMS…", () =>
        {
            var result = _shell.Call("aorms_sign_in", new { email, password, base_url = Server(_server.Text).Length == 0 ? null : Server(_server.Text) });
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                Paint(result);
                Say(InfoBarSeverity.Success, "Signed in to AORMS.");
                LoadProjects();
            });
        });
    }

    private void Connect()
    {
        _ = _shell.Run("Connecting to AORMS…", () =>
        {
            var result = _shell.Call("aorms_connect", ServerArg());
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                Paint(result);
                Say(InfoBarSeverity.Success, "AOS is connected to AORMS.");
                LoadProjects();
            });
        });
    }

    private void SignOut()
    {
        _ = _shell.Run("Signing out…", () =>
        {
            _shell.Call("aorms_sign_out", ServerArg());
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                _projects.Children.Clear();
                Paint(JsonDocument.Parse("{\"state\":\"signed_out\"}").RootElement.Clone());
                Say(InfoBarSeverity.Informational, "Signed out of AORMS on this computer. Other AORMS apps will ask you to sign in again.");
            });
        });
    }

    private void LoadProjects()
    {
        _ = _shell.Run("Loading portal projects…", () =>
        {
            var rows = _shell.Call("aorms_projects", ServerArg());
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                _projects.Children.Clear();
                if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() == 0)
                {
                    _projects.Children.Add(Flow.Secondary("The portal has no projects for your studio yet."));
                    return;
                }
                var widths = new GridLength[] { new(2, GridUnitType.Star), new(90), new(1, GridUnitType.Star), new(150) };
                _projects.Children.Add(Flow.Header(["Project", "Reference", "City", ""], widths));
                foreach (var row in rows.EnumerateArray())
                {
                    var id = Kit.Str(row, "project_office_id");
                    var pulled = row.TryGetProperty("local_project_id", out var local) && local.ValueKind == JsonValueKind.Number;
                    var line = Flow.Columns([Kit.Str(row, "title"), Kit.Str(row, "reference"), Kit.Str(row, "city"), ""], widths);
                    if (_shell.CanEdit && line.Children.Count > 0 && line.Children[line.Children.Count - 1] is Border cell)
                    {
                        var button = new Button { Content = pulled ? "Update from portal" : "Pull into AOS", MinHeight = 28 };
                        button.Click += (_, _) => Pull(id, pulled);
                        cell.Child = button;
                    }
                    _projects.Children.Add(line);
                }
            });
        });
    }

    private void Pull(string id, bool refresh)
    {
        _ = _shell.Run("Pulling the project…", () =>
        {
            var result = _shell.Call("aorms_pull", new { project_office_id = id, base_url = Server(_server.Text).Length == 0 ? null : Server(_server.Text) });
            var created = result.TryGetProperty("created", out var c) && c.ValueKind == JsonValueKind.True;
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                Say(InfoBarSeverity.Success, created ? "The project and its client are in Clients and Projects." : "The project's name and site address are updated.");
                _ = refresh;
                LoadProjects();
            });
        });
    }
}
