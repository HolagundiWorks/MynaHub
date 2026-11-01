using System.Diagnostics;
using System.Text.Json;
using Adms.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using O.Ui.Widgets;
using Osas.Core;
using Osas.Core.Suite;
using WinRT.Interop;

namespace Adms.LoginWidget;

/// <summary>
/// Floating widget: pick a company file (.osas), sign in, write suite.json, optionally hand off to O-Studio.
/// </summary>
internal sealed partial class CompanyLoginWidget : WidgetWindow
{
    static CompanyLoginWidget? _open;

    AdmsSession? _session;
    bool _companyOpen;
    bool _signedIn;
    bool _busy;
    string _companyName = "";
    string _companyPath = "";
    string _role = "";
    string _username = "";

    readonly InfoBar _notice = new() { IsOpen = false, IsClosable = true };
    readonly ProgressRing _ring = new() { Width = 18, Height = 18, IsActive = false };
    readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly StatusRow _aiRow = new("AI agent", "");
    readonly StatusRow _dbRow = new("Database server (cloud sync)", "\uEE94");
    readonly StatusRow _fileRow = new("Company file", "\uE8B7");
    readonly TextBlock _suiteStatus = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Style = (Style)Application.Current.Resources["CaptionStyle"] };

    readonly StackPanel _welcome = new() { Spacing = 12 };
    readonly StackPanel _login = new() { Spacing = 12 };
    readonly StackPanel _setup = new() { Spacing = 12 };
    readonly StackPanel _unlock = new() { Spacing = 12 };
    readonly StackPanel _create = new() { Spacing = 12 };
    readonly StackPanel _recover = new() { Spacing = 12 };
    readonly StackPanel _recent = new() { Spacing = 4 };
    readonly StackPanel _signedInPanel = new() { Spacing = 12 };

    readonly TextBox _usernameBox = new() { Header = "User name", PlaceholderText = "User name" };
    readonly PasswordBox _passwordBox = new() { Header = "Password", PlaceholderText = "Password" };
    readonly TextBlock _loginHeading = new() { Style = (Style)Application.Current.Resources["SectionTitleStyle"], FontFamily = O.Ui.SuiteFonts.Display };
    readonly PasswordBox _setupPassword = new() { Header = "Admin password (12 or more characters)" };
    readonly PasswordBox _setupConfirm = new() { Header = "Confirm password" };
    readonly PasswordBox _unlockPassword = new() { Header = "Password" };
    readonly TextBox _createName = new() { Header = "Company name" };
    readonly TextBox _createUser = new() { Header = "Owner user name", Text = "admin" };
    readonly PasswordBox _createPassword = new() { Header = "Owner password (12 or more characters)" };
    readonly PasswordBox _createConfirm = new() { Header = "Confirm password" };
    readonly TextBox _recoverUser = new() { Header = "User name" };
    readonly TextBox _recoverCode = new() { Header = "Recovery code" };
    readonly PasswordBox _recoverPassword = new() { Header = "New password (12 or more characters)" };
    readonly PasswordBox _recoverConfirm = new() { Header = "Confirm new password" };
    readonly TextBox _projectFilter = new() { PlaceholderText = "Search by code, name or client" };
    readonly ComboBox _projectBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Choose a project" };
    readonly TextBlock _projectState = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.85, FontSize = 12 };
    readonly Button _projectLock = new() { Content = "Load project", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
    readonly Button _projectRelease = new() { Content = "Close project" };
    readonly Button _projectNew = new() { Content = "New project…" };
    IReadOnlyList<ProjectEntry> _projects = [];
    readonly TextBlock _signedInCompany = new() { Style = (Style)Application.Current.Resources["SectionTitleStyle"], FontFamily = O.Ui.SuiteFonts.Display, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _signedInSummary = new() { TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _signedInPath = new() { Style = (Style)Application.Current.Resources["CaptionStyle"], TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0.75 };

    readonly Button _signInButton = new() { Content = "Sign in", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
    readonly Button _setupButton = new() { Content = "Create admin account", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
    readonly Button _createButton = new() { Content = "Create company", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
    readonly Button _recoverButton = new() { Content = "Reset password", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
    readonly Button _unlockButton = new() { Content = "Unlock", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };

    public CompanyLoginWidget() : base("Company login")
    {
        void ShowSuiteStatus() => DispatcherQueue.TryEnqueue(() =>
        {
            var status = SuiteStatus.Instance;
            _aiRow.Set(ServiceStatus.ForAi(status.Ai), status.Ai);
            _dbRow.Set(ServiceStatus.ForCloud(status.Cloud), status.Cloud);
            _fileRow.Set(ServiceStatus.ForCompanyFile(status.Database), status.Database);
            ColourDots(ServiceStatus.ForAi(status.Ai), ServiceStatus.ForCloud(status.Cloud), status.Ai, status.Cloud);
            _suiteStatus.Text = $"Project: {status.Project}"
                + (LaunchRequest.Read() is { App: var waiting } ? $"\nWaiting to start {waiting}: sign in and load a project." : "");
            UpdateApps();
        });
        SuiteStatus.Instance.Changed += ShowSuiteStatus;
        ShowSuiteStatus();
        Closed += (_, _) =>
        {
            SuiteStatus.Instance.Changed -= ShowSuiteStatus;
            ReleaseSession();
            if (ReferenceEquals(_open, this))
                _open = null;
        };

        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(icon))
            AppWindow.SetIcon(icon);

        AttachChrome(BuildChrome());
        AppWindow.Resize(new Windows.Graphics.SizeInt32(400, 760));
        EnsureSession();
        ShowWelcome();
        UpdateCommands();
    }

    public static void ShowOrFocus()
    {
        if (_open != null)
        {
            _open.Activate();
            return;
        }

        _open = new CompanyLoginWidget();
    }

    internal static void ShowError(string message)
    {
        if (_open != null)
            _open.DisplayError(message);
    }

    FrameworkElement BuildChrome()
    {
        BuildWelcomePanel();
        BuildLoginPanel();
        BuildSetupPanel();
        BuildUnlockPanel();
        BuildCreatePanel();
        BuildRecoverPanel();
        BuildSignedInPanel();
        foreach (var b in new[] { _signInButton, _setupButton, _createButton, _recoverButton, _unlockButton })
            Big(b);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var bar = BuildAppBar();
        root.Children.Add(bar);
        Grid.SetRow(bar, 0);
        root.Children.Add(_notice);
        Grid.SetRow(_notice, 1);

        var host = new Grid { Margin = new Thickness(16, 16, 16, 12) };
        host.Children.Add(_welcome);
        host.Children.Add(_login);
        host.Children.Add(_setup);
        host.Children.Add(_unlock);
        host.Children.Add(_create);
        host.Children.Add(_recover);
        host.Children.Add(_signedInPanel);
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = host,
        };
        root.Children.Add(scroll);
        Grid.SetRow(scroll, 2);

        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(16, 4, 16, 6), Children = { _ring, _status } };
        root.Children.Add(strip);
        Grid.SetRow(strip, 3);

        var nav = BuildNavBar();
        root.Children.Add(nav);
        Grid.SetRow(nav, 4);

        return new Border
        {
            Background = (Brush)Application.Current.Resources["SurfaceBackgroundBrush"],
            CornerRadius = new CornerRadius(8),
            Child = root,
        };
    }

    void BuildWelcomePanel()
    {
        var mark = Path.Combine(AppContext.BaseDirectory, "Assets", "mark.png");
        if (File.Exists(mark))
        {
            _welcome.Children.Add(new Image
            {
                Source = new BitmapImage(new Uri(mark)),
                MaxHeight = 48,
                HorizontalAlignment = HorizontalAlignment.Left,
                Stretch = Stretch.Uniform,
            });
        }

        _welcome.Children.Add(new TextBlock
        {
            Text = "Choose your company file and sign in. Other suite apps follow the choice saved on this PC.",
            TextWrapping = TextWrapping.Wrap,
        });

        var open = new Button { Content = "Open company file…" };
        _welcomeCreate = new Button { Content = "Create a new company…" };
        _welcomeFollow = new Button { Content = "Open remembered company" };
        open.Click += async (_, _) => await PickAndOpenAsync();
        var follow = _welcomeFollow;
        follow.Click += async (_, _) =>
        {
            if (SuiteJsonHub.CompanyPath() is { } path)
                await OpenPathAsync(path);
            else
            ShowError("No company is remembered yet. Open a company file first.");
        };
        var create = _welcomeCreate;
        create.Click += (_, _) => ShowCreate();
        foreach (var b in new[] { create, open, follow })
            Big(b);
        _welcome.Children.Add(create);
        _welcome.Children.Add(open);
        _welcome.Children.Add(follow);
        _welcome.Children.Add(new TextBlock
        {
            Text = "Recent companies",
            Style = (Style)Application.Current.Resources["SectionTitleStyle"], FontFamily = O.Ui.SuiteFonts.Display,
            Margin = new Thickness(0, 8, 0, 0),
        });
        _welcome.Children.Add(_recent);
        _welcome.Children.Add(new TextBlock
        {
            Text = "Manage users and company settings in full O-Studio.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
            FontSize = 12,
        });
    }

    void BuildLoginPanel()
    {
        _login.Children.Add(_loginHeading);
        _login.Children.Add(_usernameBox);
        _login.Children.Add(_passwordBox);
        _signInButton.Click += SignIn;
        _login.Children.Add(_signInButton);
        var forgot = new HyperlinkButton { Content = "Forgot password?" };
        forgot.Click += (_, _) => ShowRecover();
        _login.Children.Add(forgot);
        var close = new HyperlinkButton { Content = "Close company" };
        close.Click += (_, _) => _ = CloseCompanyAsync();
        _login.Children.Add(close);
    }

    void BuildSetupPanel()
    {
        _setup.Children.Add(new TextBlock
        {
            Text = "Set up the administrator password for this company.",
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["SectionTitleStyle"], FontFamily = O.Ui.SuiteFonts.Display,
        });
        _setup.Children.Add(_setupPassword);
        _setup.Children.Add(_setupConfirm);
        _setupButton.Click += CreateOwner;
        _setup.Children.Add(_setupButton);
    }

    void BuildCreatePanel()
    {
        _create.Children.Add(new TextBlock
        {
            Text = "Create a new company",
            Style = (Style)Application.Current.Resources["SectionTitleStyle"], FontFamily = O.Ui.SuiteFonts.Display,
        });
        _create.Children.Add(_createName);
        _create.Children.Add(_createUser);
        _create.Children.Add(_createPassword);
        _create.Children.Add(_createConfirm);
        _createButton.Click += CreateCompany;
        _create.Children.Add(_createButton);
        var back = new HyperlinkButton { Content = "Back" };
        back.Click += (_, _) => ShowWelcome();
        _create.Children.Add(back);
    }

    void BuildRecoverPanel()
    {
        _recover.Children.Add(new TextBlock
        {
            Text = "Reset a forgotten password with the recovery code saved when the company was created.",
            TextWrapping = TextWrapping.Wrap,
        });
        _recover.Children.Add(_recoverUser);
        _recover.Children.Add(_recoverCode);
        _recover.Children.Add(_recoverPassword);
        _recover.Children.Add(_recoverConfirm);
        _recoverButton.Click += ResetPassword;
        _recover.Children.Add(_recoverButton);
        var back = new HyperlinkButton { Content = "Back to sign in" };
        back.Click += (_, _) => ShowOnly(_login);
        _recover.Children.Add(back);
    }

    void BuildUnlockPanel()
    {
        _unlock.Children.Add(new TextBlock
        {
            Text = "Session locked",
            Style = (Style)Application.Current.Resources["SectionTitleStyle"], FontFamily = O.Ui.SuiteFonts.Display,
        });
        _unlock.Children.Add(_unlockPassword);
        _unlockButton.Click += Unlock;
        _unlock.Children.Add(_unlockButton);
    }

    void BuildSignedInPanel()
    {
        BuildHomePage();
        BuildServicesPage();
        BuildAccountPage();
        _signedInPanel.Spacing = 0;
        _signedInPanel.Children.Add(_pageHome);
        _signedInPanel.Children.Add(_pageServices);
        _signedInPanel.Children.Add(_pageAccount);
        ShowTab(0);
    }

    async Task LoadProjectsAsync()
    {
        try
        {
            _projects = await SuiteLauncherHost.Instance.ListProjectsAsync();
        }
        catch (Exception ex)
        {
            _projects = [];
            ShowError(string.IsNullOrWhiteSpace(ex.Message) ? "The project list could not be read." : ex.Message);
        }

        ShowProjects();
    }

    void ShowProjects()
    {
        var term = _projectFilter.Text.Trim();
        var locked = SuiteLauncherHost.Instance.Project;
        _projectBox.Items.Clear();
        ProjectEntry? pick = null;
        foreach (var p in _projects)
        {
            if (term.Length > 0 && p.ToString().IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            _projectBox.Items.Add(p);
            if (locked != null && p.Id == locked.ProjectId)
                pick = p;
        }

        if (pick != null)
            _projectBox.SelectedItem = pick;
        // One project is loaded at a time: the card shows it with Close project, or the list to choose from.
        var loaded = locked != null;
        _projectPickBox.Visibility = loaded ? Visibility.Collapsed : Visibility.Visible;
        _projectLoadedBox.Visibility = loaded ? Visibility.Visible : Visibility.Collapsed;
        _projectLock.IsEnabled = !loaded && _projects.Count > 0;
        if (loaded)
        {
            _projectTitle.Text = locked!.Name;
            var client = _projects.FirstOrDefault(p => p.Id == locked.ProjectId)?.Client;
            _projectMeta.Text = string.IsNullOrWhiteSpace(client) ? locked.Code : $"{locked.Code} · {client}";
        }

        UpdateApps();
        _projectState.Text = _projects.Count == 0
            ? "No projects yet. Create one with New project…"
            : "Choose a project and load it. O‑CoreOp and O‑Draft open on it.";
    }

    async Task LockSelectedProjectAsync()
    {
        if (_projectBox.SelectedItem is not ProjectEntry entry)
        {
            ShowError("Choose a project first.");
            return;
        }

        await RunEngineAsync("Loading the project…", () => SuiteLauncherHost.Instance.LockProjectAsync(entry).GetAwaiter().GetResult());
        ShowProjects();
    }

    void ReleaseProject()
    {
        try
        {
            SuiteLauncherHost.Instance.UnlockProject();
            _notice.IsOpen = false;
        }
        catch (InvalidOperationException ex)
        {
            ShowError(ex.Message);
        }

        ShowProjects();
    }

    /// <summary>The basic project wizard: client, name, code, site address. Everything else is edited in O-Studio.</summary>
    async Task NewProjectAsync()
    {
        if (_busy || _session == null || !_signedIn)
            return;
        JsonElement clients = default;
        string nextCode = "";
        if (!await RunEngineAsync("", () =>
        {
            clients = _session!.Call("client_list", new { include_archived = false }).Clone();
            nextCode = _session.Call("project_next_code").GetString() ?? "";
        }))
            return;

        var clientBox = new ComboBox { Header = "Client", HorizontalAlignment = HorizontalAlignment.Stretch };
        clientBox.Items.Add("New client…");
        var ids = new List<long?> { null };
        if (clients.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in clients.EnumerateArray())
            {
                if (c.TryGetProperty("id", out var cid) && cid.TryGetInt64(out var n))
                {
                    clientBox.Items.Add(Text(c, "name"));
                    ids.Add(n);
                }
            }
        }

        clientBox.SelectedIndex = ids.Count > 1 ? 1 : 0;
        var newClient = new TextBox { Header = "New client name", Visibility = ids.Count > 1 ? Visibility.Collapsed : Visibility.Visible };
        clientBox.SelectionChanged += (_, _) => newClient.Visibility = clientBox.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        var name = new TextBox { Header = "Project name" };
        var code = new TextBox { Header = "Project code", Text = nextCode };
        var address = new TextBox { Header = "Site address", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 72 };
        var dialog = new ContentDialog
        {
            Title = "New project",
            Content = new StackPanel { Spacing = 10, Children = { clientBox, newClient, name, code, address } },
            PrimaryButtonText = "Create and load",
            PrimaryButtonStyle = Accent,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var projectName = name.Text.Trim();
        if (projectName.Length == 0)
        {
            ShowError("Enter the project name.");
            return;
        }

        var clientId = ids[Math.Max(0, clientBox.SelectedIndex)];
        var clientName = newClient.Text.Trim();
        if (clientId == null && clientName.Length == 0)
        {
            ShowError("Choose a client or enter a new client name.");
            return;
        }

        var projectCode = code.Text.Trim();
        var site = address.Text.Trim();
        long createdId = 0;
        if (!await RunEngineAsync("Creating the project…", () =>
        {
            var owner = clientId;
            if (owner == null)
            {
                owner = _session!.Call("client_save", new
                {
                    client = new
                    {
                        id = (long?)null,
                        name = clientName,
                        contact_person = "",
                        email = "",
                        phone = "",
                        address = "",
                        pan = "",
                        gstin = "",
                    },
                }).GetInt64();
            }

            createdId = _session!.Call("project_save", new
            {
                project = new { id = (long?)null, client_id = owner, code = projectCode, name = projectName, site_address = site, status = "active" },
            }).GetInt64();
        }))
            return;

        await LoadProjectsAsync();
        if (_projects.FirstOrDefault(p => p.Id == createdId) is { } created)
        {
            _projectBox.SelectedItem = created;
            await LockSelectedProjectAsync();
        }
    }

    static Style Accent => (Style)Application.Current.Resources["AccentButtonStyle"];

    /// <summary>One line of the status area: a coloured marker, the name, what it is doing in words, and the detail.</summary>
    sealed class StatusRow
    {
        readonly StatusIcon _icon;
        readonly TextBlock _state = new() { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock _detail = new() { Style = (Style)Application.Current.Resources["CaptionStyle"], TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
        readonly string _name;

        internal StatusRow(string name, string glyph)
        {
            _name = name;
            _icon = glyph.Length == 0 ? StatusIcon.Orb(42) : new StatusIcon(glyph, 36);
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            head.Children.Add(_icon.Root);
            head.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(_state);
            var column = new StackPanel { Spacing = 0 };
            column.Children.Add(head);
            _detail.Margin = new Thickness(46, 2, 0, 0);
            column.Children.Add(_detail);
            Root = column;
            Set(ServiceLevel.Off, "checking…");
        }

        internal FrameworkElement Root { get; }


        internal void Set(ServiceLevel level, string detail)
        {
            _icon.Set(level, $"{_name}: {ServiceStatus.Word(level)}");
            _state.Text = ServiceStatus.Word(level);
            _detail.Text = detail;
            AutomationProperties.SetName(Root, $"{_name}: {ServiceStatus.Word(level)}. {detail}");
        }
    }

    sealed record AppTile(string TicketApp, Button Button, bool NeedsProject);

    readonly List<AppTile> _tiles = [];

    /// <summary>The apps of the suite, each with its mark. O-Studio, O-CoreOp and O-Draft start from here; O-Form is a placeholder for the app that is planned.</summary>
    FrameworkElement BuildAppTiles()
    {
        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var specs = new (string Ticket, string Title, string Blurb, string Mark, bool NeedsProject)[]
        {
            ("O-Studio", "O-Studio", "Practice, people, billing", "mark-studio.png", false),
            ("O-CoreOp", "O-CoreOp", "Estimates and bills", "mark-coreop.png", true),
            ("O-Draft", "O-Draft", "2D drafting", "mark-draft.png", true),
            ("O-Form", "O-Form", "3D / BIM form (planned)", "mark-form.png", false),
        };
        for (var i = 0; i < specs.Length; i++)
        {
            var (ticket, title, blurb, mark, needsProject) = specs[i];
            var tile = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 10, 12, 10),
                MinHeight = 72,
                Content = TileContent(title, blurb, mark),
            };
            AutomationProperties.SetName(tile, title);
            if (ticket == "O-Form")
            {
                tile.IsEnabled = false;
                ToolTipService.SetToolTip(tile, "O-Form is planned and not built yet.");
            }
            else
            {
                var app = SuiteCatalog.FindByTicketApp(ticket);
                tile.Click += async (_, _) => await StartAppAsync(ticket, app);
                _tiles.Add(new AppTile(ticket, tile, needsProject));
            }

            Grid.SetColumn(tile, i % 2);
            Grid.SetRow(tile, i / 2);
            grid.Children.Add(tile);
        }

        UpdateApps();
        return grid;
    }

    static FrameworkElement TileContent(string title, string blurb, string mark)
    {
        FrameworkElement icon;
        var file = Path.Combine(AppContext.BaseDirectory, "Assets", mark);
        if (mark.Length > 0 && File.Exists(file))
        {
            icon = new Image { Source = new BitmapImage(new Uri(file)), Width = 40, Height = 40, Stretch = Stretch.Uniform };
        }
        else
        {
            // A placeholder for an app that is not built: a grey square with its initial.
            icon = new Border
            {
                Width = 40,
                Height = 40,
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x8B, 0x94, 0x9E)),
                Child = new TextBlock { Text = title[..1], Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
        }

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 110 };
        text.Children.Add(new TextBlock { Text = title, FontFamily = O.Ui.SuiteFonts.Names, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = blurb, Style = (Style)Application.Current.Resources["CaptionStyle"], TextWrapping = TextWrapping.Wrap, MaxLines = 2, Opacity = 0.8 });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(icon);
        row.Children.Add(text);
        return row;
    }

    /// <summary>O-Studio needs a signed-in company; O-CoreOp and O-Draft also need the project loaded.</summary>
    void UpdateApps()
    {
        var host = SuiteLauncherHost.Instance;
        var signedIn = host.SignedIn || _signedIn;
        var project = host.Project != null;
        foreach (var tile in _tiles)
        {
            var ready = signedIn && (!tile.NeedsProject || project);
            tile.Button.IsEnabled = ready && !_busy;
            ToolTipService.SetToolTip(tile.Button, ready ? null : !signedIn ? "Sign in first." : "Load a project first.");
        }
    }

    async Task StartAppAsync(string ticketApp, SuiteAppDescriptor? app)
    {
        if (ticketApp == "O-Studio")
        {
            await HandOffToStudioAsync();
            return;
        }

        if (app == null)
            return;
        try
        {
            await SuiteLauncherHost.Instance.LaunchAppAsync(app, DispatcherQueue);
            _notice.IsOpen = false;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    nint Hwnd => WindowNative.GetWindowHandle(this);

    void EnsureSession()
    {
        if (_session != null)
            return;
        try
        {
            _session = new AdmsSession(AppContext.BaseDirectory);
            SuiteLauncherHost.Instance.BindSession(_session);
        }
        catch (Exception ex)
        {
            ShowError("The accounting engine is not loaded. Rebuild with the Rust engine (architect_office_desktop_lib.dll beside this app). " + ex.Message);
        }
    }

    void ReleaseSession()
    {
        _session?.Dispose();
        _session = null;
        SuiteLauncherHost.Instance.UnbindSession();
    }

    string _idle = "";
    Button _welcomeCreate = new();
    Button _welcomeFollow = new();

    void ShowWelcome()
    {
        ShowOnly(_welcome);
        // A remembered company is what most people want next; with none, creating one is.
        var remembered = SuiteJsonHub.CompanyPath() != null;
        _welcomeFollow.Style = remembered ? Accent : null;
        _welcomeFollow.IsEnabled = remembered;
        _welcomeCreate.Style = remembered ? null : Accent;
        _ = LoadRecentAsync();
        _idle = SuiteJsonHub.CompanyPath() is { } p
            ? $"Remembered: {Path.GetFileName(p)}"
            : "No company remembered";
        _status.Text = _idle;
    }

    void ShowOnly(UIElement panel)
    {
        _welcome.Visibility = panel == _welcome ? Visibility.Visible : Visibility.Collapsed;
        _login.Visibility = panel == _login ? Visibility.Visible : Visibility.Collapsed;
        _setup.Visibility = panel == _setup ? Visibility.Visible : Visibility.Collapsed;
        _unlock.Visibility = panel == _unlock ? Visibility.Visible : Visibility.Collapsed;
        _create.Visibility = panel == _create ? Visibility.Visible : Visibility.Collapsed;
        _recover.Visibility = panel == _recover ? Visibility.Visible : Visibility.Collapsed;
        _signedInPanel.Visibility = panel == _signedInPanel ? Visibility.Visible : Visibility.Collapsed;
        _nav.Visibility = panel == _signedInPanel ? Visibility.Visible : Visibility.Collapsed;
        if (panel != _signedInPanel)
            SetBar(_companyName.Length > 0 ? _companyName : "O-Studio suite", panel == _welcome ? "Choose a company" : "Sign in to continue");
        UpdateCommands();
    }

    void UpdateCommands()
    {
        var engine = _session != null && !_busy;
        _signInButton.IsEnabled = engine && _companyOpen && !_signedIn;
        _setupButton.IsEnabled = engine && _companyOpen;
        _unlockButton.IsEnabled = engine && _companyOpen;
        _createButton.IsEnabled = engine && !_companyOpen;
        _recoverButton.IsEnabled = engine && _companyOpen;
        _usernameBox.IsEnabled = !_busy;
        _passwordBox.IsEnabled = !_busy;
    }

    async Task PickAndOpenAsync()
    {
        if (_busy || _companyOpen || _session == null)
            return;
        string? path;
        try
        {
            path = await LocalPickers.PickCompanyFile(Hwnd);
        }
        catch (Exception ex)
        {
            ShowError("The file picker could not be opened. " + ex.Message);
            return;
        }

        if (path != null)
            await OpenPathAsync(path);
    }

    async Task OpenPathAsync(string path)
    {
        if (_busy || _companyOpen || _session == null)
            return;
        JsonElement check = default;
        var valid = await RunEngineAsync("Checking file…", () =>
        {
            check = _session!.Call("company_validate", new { path });
        });
        if (!valid)
            return;

        var needsUpgrade = check.ValueKind == JsonValueKind.Object
            && check.TryGetProperty("needs_upgrade", out var flag)
            && flag.ValueKind == JsonValueKind.True;
        if (needsUpgrade)
        {
            var name = Text(check, "name");
            var dialog = new ContentDialog
            {
                Title = "Open this company?",
                Content = new TextBlock
                {
                    Text = $"{name}\n{path}\n\nThis file was saved by an older version. A safety copy is made before it is upgraded.",
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "Open",
                PrimaryButtonStyle = Accent,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;
        }

        var secret = "";
        var unlock = "password";
        var protectedFile = check.ValueKind == JsonValueKind.Object
            && check.TryGetProperty("protected", out var protectedFlag)
            && protectedFlag.ValueKind == JsonValueKind.True;
        if (protectedFile)
        {
            var box = new PasswordBox { Header = "Password or recovery code" };
            var recovery = new CheckBox { Content = "This is the recovery code" };
            var stack = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = "This company file is protected. Enter the file password or recovery code.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    box,
                    recovery,
                },
            };
            var dialog = new ContentDialog
            {
                Title = "Open protected company",
                Content = stack,
                PrimaryButtonText = "Open",
                PrimaryButtonStyle = Accent,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;
            secret = box.Password;
            unlock = recovery.IsChecked == true ? "recovery" : "password";
            if (secret.Length == 0)
            {
                ShowError(unlock == "recovery" ? "Enter the recovery code." : "Enter the file password.");
                return;
            }
        }

        await RunEngineAsync("Opening company…", () =>
        {
            _session!.Call("company_open", new { path, secret, unlock });
            var status = _session.Call("auth_status");
            DispatcherQueue.TryEnqueue(() => ApplyStatus(status));
        });
    }

    async void SignIn(object sender, RoutedEventArgs e)
    {
        if (_busy || _session == null || !_companyOpen)
            return;
        var password = _passwordBox.Password;
        var username = _usernameBox.Text.Trim();
        if (username.Length == 0 || password.Length == 0)
        {
            ShowError("Enter your user name and password.");
            return;
        }

        await RunEngineAsync("Signing in…", () =>
        {
            _session.Login(username, password);
            var status = _session.Call("auth_status");
            DispatcherQueue.TryEnqueue(() =>
            {
                _passwordBox.Password = "";
                ApplyStatus(status);
            });
        });
    }

    async Task LoadRecentAsync()
    {
        if (_session == null)
            return;
        JsonElement rows = default;
        if (!await RunEngineAsync("", () => rows = _session!.Call("company_recent_list").Clone()))
            return;
        _recent.Children.Clear();
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() == 0)
        {
            _recent.Children.Add(new TextBlock { Text = "None yet.", Opacity = 0.7 });
            return;
        }

        foreach (var row in rows.EnumerateArray())
        {
            var path = Text(row, "path");
            var present = row.TryGetProperty("exists", out var exists) && exists.ValueKind == JsonValueKind.True;
            var name = Text(row, "name");
            var button = new HyperlinkButton
            {
                Content = (string.IsNullOrWhiteSpace(name) ? Path.GetFileName(path) : name) + (present ? "" : " (file not found)"),
                IsEnabled = present,
            };
            ToolTipService.SetToolTip(button, path);
            button.Click += async (_, _) => await OpenPathAsync(path);
            _recent.Children.Add(button);
        }
    }

    void ShowCreate()
    {
        if (_busy || _companyOpen)
            return;
        ShowOnly(_create);
        _createName.Focus(FocusState.Programmatic);
    }

    async void CreateCompany(object sender, RoutedEventArgs e)
    {
        if (_busy || _session == null || _companyOpen)
            return;
        var name = _createName.Text.Trim();
        var username = _createUser.Text.Trim();
        var password = _createPassword.Password;
        var confirm = _createConfirm.Password;
        if (name.Length == 0 || username.Length == 0 || password.Length == 0)
        {
            ShowError("Enter the company name, the owner user name and a password.");
            return;
        }

        string? path;
        try
        {
            path = await LocalPickers.PickNewCompanyFile(Hwnd, name);
        }
        catch (Exception ex)
        {
            ShowError("The file picker could not be opened. " + ex.Message);
            return;
        }

        if (path == null)
            return;
        // The save picker leaves an empty placeholder; the engine must create the file itself.
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length == 0)
                File.Delete(path);
        }
        catch (IOException)
        {
        }

        JsonElement result = default;
        var created = await RunEngineAsync("Creating the company…", () =>
        {
            result = _session!.Call("company_create", new { path, name, username, password, confirm }).Clone();
        });
        _createPassword.Password = "";
        _createConfirm.Password = "";
        if (!created)
            return;
        var code = result.ValueKind == JsonValueKind.Array && result.GetArrayLength() > 1 ? result[1].GetString() : null;
        await RefreshStatusAsync();
        if (!string.IsNullOrWhiteSpace(code))
            await ShowRecoveryAsync(code, "Save your recovery code");
    }

    void ShowRecover()
    {
        if (_busy || !_companyOpen)
            return;
        ShowOnly(_recover);
        if (string.IsNullOrWhiteSpace(_recoverUser.Text))
            _recoverUser.Text = SuiteJsonHub.RememberedUsername() ?? "";
        _recoverUser.Focus(FocusState.Programmatic);
    }

    async void ResetPassword(object sender, RoutedEventArgs e)
    {
        if (_busy || _session == null || !_companyOpen)
            return;
        var username = _recoverUser.Text.Trim();
        var code = _recoverCode.Text.Trim();
        var password = _recoverPassword.Password;
        var confirm = _recoverConfirm.Password;
        if (username.Length == 0 || code.Length == 0 || password.Length == 0)
        {
            ShowError("Enter the user name, the recovery code and a new password.");
            return;
        }

        string? fresh = null;
        var reset = await RunEngineAsync("Resetting the password…", () =>
        {
            fresh = _session!.Call("auth_recover", new { username, code, @new = password, confirm }).GetString();
        });
        _recoverPassword.Password = "";
        _recoverConfirm.Password = "";
        _recoverCode.Text = "";
        if (!reset || string.IsNullOrWhiteSpace(fresh))
            return;
        await ShowRecoveryAsync(fresh, "Password reset. Save the new recovery code");
        ShowOnly(_login);
    }

    async void CreateOwner(object sender, RoutedEventArgs e)
    {
        if (_busy || _session == null || !_companyOpen)
            return;
        var password = _setupPassword.Password;
        var confirm = _setupConfirm.Password;
        if (password.Length == 0)
        {
            ShowError("Enter the admin password.");
            return;
        }

        string? code = null;
        var created = await RunEngineAsync("Creating the admin account…", () =>
        {
            code = _session!.Call("auth_setup", new { username = "admin", password, confirm }).GetString();
        });
        _setupPassword.Password = "";
        _setupConfirm.Password = "";
        if (!created || string.IsNullOrWhiteSpace(code))
            return;
        await ShowRecoveryAsync(code, "Save your recovery code");
        await RefreshStatusAsync();
    }

    async void Unlock(object sender, RoutedEventArgs e)
    {
        if (_busy || _session == null || !_companyOpen)
            return;
        var password = _unlockPassword.Password;
        if (password.Length == 0)
        {
            ShowError("Enter your password.");
            return;
        }

        await RunEngineAsync("Unlocking…", () =>
        {
            _session!.Call("auth_unlock", new { password });
            var status = _session.Call("auth_status");
            DispatcherQueue.TryEnqueue(() =>
            {
                _unlockPassword.Password = "";
                ApplyStatus(status);
            });
        });
    }

    async Task LockAsync()
    {
        if (_busy || _session == null || !_signedIn)
            return;
        await RunEngineAsync("Locking…", () =>
        {
            _session!.Call("auth_lock");
            var status = _session.Call("auth_status");
            DispatcherQueue.TryEnqueue(() => ApplyStatus(status));
        });
    }

    async Task LogOutAsync()
    {
        if (_busy || _session == null || !_signedIn)
            return;
        CloudSyncService.Instance.StopForSignOut();
        await RunEngineAsync("Signing out…", () =>
        {
            _session!.Call("auth_logout");
            var status = _session.Call("auth_status");
            DispatcherQueue.TryEnqueue(() => ApplyStatus(status));
        });
    }

    async Task CloseCompanyAsync()
    {
        if (_busy || !_companyOpen || _session == null)
            return;
        await Task.Yield();
        CloudSyncService.Instance.StopForSignOut();
        try
        {
            _session.CloseCompany();
            _companyOpen = false;
            _signedIn = false;
            _companyName = "";
            _companyPath = "";
            _role = "";
            _username = "";
            _usernameBox.Text = "";
            _passwordBox.Password = "";
            ShowWelcome();
            _status.Text = "Company closed";
            _notice.IsOpen = false;
        }
        catch (AdmsException ex)
        {
            ShowError(ex.Message);
        }
    }

    async Task HandOffToStudioAsync()
    {
        if (_busy || _session == null || !_signedIn || string.IsNullOrWhiteSpace(_companyPath))
            return;
        try
        {
            await SuiteLauncherHost.Instance.HandOffToStudioAsync(DispatcherQueue);
            _session = null;
            Close();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    async Task RefreshStatusAsync()
    {
        if (_session == null)
            return;
        await RunEngineAsync("Checking session…", () =>
        {
            var status = _session!.Call("auth_status");
            DispatcherQueue.TryEnqueue(() => ApplyStatus(status));
        });
    }

    void ApplyStatus(JsonElement status)
    {
        SuiteLauncherHost.Instance.ApplyStatus(status);
        if (status.ValueKind != JsonValueKind.Object || !status.TryGetProperty("company", out var company) || company.ValueKind != JsonValueKind.Object)
        {
            _companyOpen = false;
            _signedIn = false;
            ShowWelcome();
            return;
        }

        _companyOpen = true;
        // Signed in only when the status below says so (after Sign out or Lock the sign-in button must work again).
        _signedIn = false;
        _companyName = Text(company, "name");
        _companyPath = Text(company, "path");
        if (!string.IsNullOrWhiteSpace(_companyPath))
            SuiteJsonHub.RememberCompany(_companyPath, chosenBy: "O-Studio.Launcher");

        var setup = status.TryGetProperty("setup_required", out var setupFlag) && setupFlag.ValueKind == JsonValueKind.True;
        if (setup)
        {
            ShowOnly(_setup);
            _idle = "Set up the administrator password";
            return;
        }

        if (!status.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object)
        {
            ShowOnly(_login);
            _idle = "Sign in to continue";
            _loginHeading.Text = string.IsNullOrWhiteSpace(_companyName) ? "Sign in" : _companyName;
            if (string.IsNullOrWhiteSpace(_usernameBox.Text) && SuiteJsonHub.RememberedUsername() is { } remembered)
                _usernameBox.Text = remembered;
            return;
        }

        _username = Text(user, "username");
        _role = Text(user, "role");
        if (!string.IsNullOrWhiteSpace(_companyPath))
            SuiteJsonHub.RememberCompany(_companyPath, _username, "O-Studio.Launcher");

        var locked = status.TryGetProperty("locked", out var lockedFlag) && lockedFlag.ValueKind == JsonValueKind.True;
        if (locked)
        {
            ShowOnly(_unlock);
            _idle = "Session locked";
            return;
        }

        var firstShow = !_signedIn;
        _signedIn = true;
        if (firstShow)
            _ = LoadProjectsAsync();
        _signedInCompany.Text = string.IsNullOrWhiteSpace(_companyName) ? Path.GetFileNameWithoutExtension(_companyPath) : _companyName;
        _signedInSummary.Text = $"{_username} · {_role}";
        _avatar.Text = string.IsNullOrWhiteSpace(_username) ? "?" : _username[..1].ToUpperInvariant();
        SetBar(_signedInCompany.Text, _signedInSummary.Text);
        _signedInPath.Text = _companyPath;
        ToolTipService.SetToolTip(_signedInPath, _companyPath);
        ShowOnly(_signedInPanel);
        _idle = "Signed in";
        _status.Text = _idle;
        _notice.IsOpen = false;
    }

    async Task<bool> RunEngineAsync(string status, Action work)
    {
        if (_session == null)
            return false;
        SetBusy(true, status);
        try
        {
            await Task.Run(work);
            return true;
        }
        catch (AdmsException ex)
        {
            ShowError(string.IsNullOrWhiteSpace(ex.Message) ? "The engine reported an error." : ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            ShowError(string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message);
            return false;
        }
        finally
        {
            // Back to the state the window is in, not the name of the action that just ended.
            SetBusy(false, _idle.Length > 0 ? _idle : _status.Text);
        }
    }

    void SetBusy(bool busy, string status)
    {
        _busy = busy;
        _ring.IsActive = busy;
        _ring.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateApps();
        if (!string.IsNullOrWhiteSpace(status))
            _status.Text = status;
        UpdateCommands();
    }

    void DisplayError(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            _notice.IsOpen = false;
            return;
        }

        _notice.Severity = InfoBarSeverity.Error;
        _notice.Title = message.Trim();
        _notice.Message = "";
        _notice.IsOpen = true;
    }

    async Task ShowRecoveryAsync(string code, string title)
    {
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock
        {
            Text = "Store this code safely. It is shown once.",
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new TextBlock
        {
            Text = code,
            IsTextSelectionEnabled = true,
            FontFamily = new FontFamily("Consolas"),
        });
        var dialog = new ContentDialog
        {
            Title = title,
            Content = body,
            CloseButtonText = "I have stored it",
            XamlRoot = Content.XamlRoot,
        };
        await dialog.ShowAsync();
    }

    static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
