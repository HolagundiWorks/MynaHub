using System.Diagnostics;
using System.Text.Json;
using Adms.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using O.Ui.Widgets;
using Osas.Core;
using WinRT.Interop;

namespace Adms.LoginWidget;

/// <summary>
/// Floating widget: pick a company file (.osas), sign in, write suite.json, optionally hand off to O-Studio.
/// </summary>
internal sealed class CompanyLoginWidget : WidgetWindow
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
    readonly TextBlock _suiteStatus = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Style = (Style)Application.Current.Resources["CaptionStyle"] };

    readonly StackPanel _welcome = new() { Spacing = 12 };
    readonly StackPanel _login = new() { Spacing = 12 };
    readonly StackPanel _setup = new() { Spacing = 12 };
    readonly StackPanel _unlock = new() { Spacing = 12 };
    readonly StackPanel _signedInPanel = new() { Spacing = 12 };

    readonly TextBox _usernameBox = new() { Header = "User name", PlaceholderText = "User name" };
    readonly PasswordBox _passwordBox = new() { Header = "Password", PlaceholderText = "Password" };
    readonly TextBlock _loginHeading = new() { Style = (Style)Application.Current.Resources["SectionTitleStyle"] };
    readonly PasswordBox _setupPassword = new() { Header = "Admin password (12 or more characters)" };
    readonly PasswordBox _setupConfirm = new() { Header = "Confirm password" };
    readonly PasswordBox _unlockPassword = new() { Header = "Password" };
    readonly TextBlock _signedInSummary = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };

    readonly Button _signInButton = new() { Content = "Sign in", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
    readonly Button _setupButton = new() { Content = "Create admin account", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
    readonly Button _unlockButton = new() { Content = "Unlock", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };

    public CompanyLoginWidget() : base("Company login")
    {
        void ShowSuiteStatus() => DispatcherQueue.TryEnqueue(() =>
            _suiteStatus.Text = $"AI agent: {SuiteStatus.Instance.Ai}\nDatabase: {SuiteStatus.Instance.Database}\nCloud sync: {SuiteStatus.Instance.Cloud}");
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
        AppWindow.Resize(new Windows.Graphics.SizeInt32(420, 520));
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
        BuildSignedInPanel();

        var body = new Grid { MinWidth = 360 };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.Children.Add(_notice);
        Grid.SetRow(_notice, 0);

        var host = new Grid();
        host.Children.Add(_welcome);
        host.Children.Add(_login);
        host.Children.Add(_setup);
        host.Children.Add(_unlock);
        host.Children.Add(_signedInPanel);
        body.Children.Add(host);
        Grid.SetRow(host, 1);

        var footer = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(0, 8, 0, 0),
            Children =
            {
                _suiteStatus,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _ring, _status } },
            },
        };
        body.Children.Add(footer);
        Grid.SetRow(footer, 2);

        return new Border
        {
            Background = (Brush)Application.Current.Resources["SurfaceBackgroundBrush"],
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Child = body,
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
        open.Click += async (_, _) => await PickAndOpenAsync();
        var follow = new Button { Content = "Open remembered company" };
        follow.Click += async (_, _) =>
        {
            if (SuiteJsonHub.CompanyPath() is { } path)
                await OpenPathAsync(path);
            else
            ShowError("No company is remembered yet. Open a company file first.");
        };
        _welcome.Children.Add(open);
        _welcome.Children.Add(follow);
        _welcome.Children.Add(new TextBlock
        {
            Text = "Create a new company or manage users in full O-Studio.",
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
            Style = (Style)Application.Current.Resources["SectionTitleStyle"],
        });
        _setup.Children.Add(_setupPassword);
        _setup.Children.Add(_setupConfirm);
        _setupButton.Click += CreateOwner;
        _setup.Children.Add(_setupButton);
    }

    void BuildUnlockPanel()
    {
        _unlock.Children.Add(new TextBlock
        {
            Text = "Session locked",
            Style = (Style)Application.Current.Resources["SectionTitleStyle"],
        });
        _unlock.Children.Add(_unlockPassword);
        _unlockButton.Click += Unlock;
        _unlock.Children.Add(_unlockButton);
    }

    void BuildSignedInPanel()
    {
        _signedInPanel.Children.Add(new TextBlock
        {
            Text = "Signed in",
            Style = (Style)Application.Current.Resources["SectionTitleStyle"],
        });
        _signedInPanel.Children.Add(_signedInSummary);
        var studio = new Button
        {
            Content = "Open O-Studio…",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        studio.Click += async (_, _) => await HandOffToStudioAsync();
        _signedInPanel.Children.Add(studio);
        var lockBtn = new Button { Content = "Lock" };
        lockBtn.Click += async (_, _) => await LockAsync();
        var logout = new Button { Content = "Sign out" };
        logout.Click += async (_, _) => await LogOutAsync();
        var close = new Button { Content = "Close company" };
        close.Click += (_, _) => _ = CloseCompanyAsync();
        _signedInPanel.Children.Add(lockBtn);
        _signedInPanel.Children.Add(logout);
        _signedInPanel.Children.Add(close);
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

    void ShowWelcome()
    {
        ShowOnly(_welcome);
        _status.Text = SuiteJsonHub.CompanyPath() is { } p
            ? $"Remembered: {Path.GetFileName(p)}"
            : "No company remembered";
    }

    void ShowOnly(UIElement panel)
    {
        _welcome.Visibility = panel == _welcome ? Visibility.Visible : Visibility.Collapsed;
        _login.Visibility = panel == _login ? Visibility.Visible : Visibility.Collapsed;
        _setup.Visibility = panel == _setup ? Visibility.Visible : Visibility.Collapsed;
        _unlock.Visibility = panel == _unlock ? Visibility.Visible : Visibility.Collapsed;
        _signedInPanel.Visibility = panel == _signedInPanel ? Visibility.Visible : Visibility.Collapsed;
        UpdateCommands();
    }

    void UpdateCommands()
    {
        var engine = _session != null && !_busy;
        _signInButton.IsEnabled = engine && _companyOpen && !_signedIn;
        _setupButton.IsEnabled = engine && _companyOpen;
        _unlockButton.IsEnabled = engine && _companyOpen;
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
        _companyName = Text(company, "name");
        _companyPath = Text(company, "path");
        if (!string.IsNullOrWhiteSpace(_companyPath))
            SuiteJsonHub.RememberCompany(_companyPath, chosenBy: "O-Studio.Launcher");

        var setup = status.TryGetProperty("setup_required", out var setupFlag) && setupFlag.ValueKind == JsonValueKind.True;
        if (setup)
        {
            ShowOnly(_setup);
            return;
        }

        if (!status.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object)
        {
            ShowOnly(_login);
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
            return;
        }

        _signedIn = true;
        _signedInSummary.Text = $"{_username} ({_role})\n{_companyPath}\n\nLaunch suite apps from the notification area icon, or open full O-Studio below.";
        ShowOnly(_signedInPanel);
        _status.Text = "Signed in";
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
            SetBusy(false, _status.Text);
        }
    }

    void SetBusy(bool busy, string status)
    {
        _busy = busy;
        _ring.IsActive = busy;
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
