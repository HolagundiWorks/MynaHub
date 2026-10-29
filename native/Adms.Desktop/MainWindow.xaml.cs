using System.Text.Json;
using Adms.Interop;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Printing;
using Windows.Graphics;
using Windows.Graphics.Printing;
using Windows.Storage;
using Windows.System;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace Adms.Desktop;

public sealed class RecentCompany
{
    public string Uuid { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Detail { get; set; } = "";
    public bool Exists { get; set; }
}

public sealed partial class MainWindow : Window
{
    private AdmsSession? _session;
    private WorkspaceView? _workspace;
    private bool _companyOpen;
    private bool _signedIn;
    private bool _busy;
    private bool _shuttingDown;
    private bool _closeAfterBusy;
    private string _companyName = "";
    private string _companyPath = "";
    private string _companyUuid = "";
    private string _role = "";
    private bool _allowClose;
    private RectInt32 _restored;
    private bool _maximized;
    private readonly AccessibilitySettings _accessibility = new();
    private PrintDocument? _printDocument;
    private IPrintDocumentSource? _printSource;
    private IReadOnlyList<UIElement> _printPages = [];
    private string _printJob = "O-Studio";

    public MainWindow()
    {
        InitializeComponent();
        Hcw.Ui.SuiteMotion.RespectSystemPreference();
        WelcomeLogo.Source = BrandArt.Wordmark();
        LoginLogo.Source = BrandArt.Mark();
        SetupLogo.Source = BrandArt.Mark();
        RecoverLogo.Source = BrandArt.Mark();
        UnlockLogo.Source = BrandArt.Mark();
        Title = BrandArt.Name;
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(icon))
            AppWindow.SetIcon(icon);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
            presenter.IsResizable = true;
            presenter.PreferredMinimumWidth = 480;
            presenter.PreferredMinimumHeight = 600;
        }

        var placement = WindowPlacement.LoadOrCenter();
        _restored = new RectInt32(placement.X, placement.Y, placement.Width, placement.Height);
        _maximized = placement.Maximized;
        placement.Apply(AppWindow);
        AppWindow.Changed += RememberPlacement;
        AppWindow.Closing += OnClosing;

        try
        {
            var dir = AppFolder.Prepare();
            _session = new AdmsSession(dir);
            StatusText.Text = "Engine ready";
        }
        catch (Exception ex)
        {
            ShowError("The accounting engine could not be loaded. " + ex.Message);
        }
        ShowWelcome();
        try
        {
            _accessibility.HighContrastChanged += (_, _) => DispatcherQueue.TryEnqueue(RestoreTheme);
        }
        catch (Exception)
        {
            // This machine does not expose the high-contrast change event to an unpackaged app.
            // The current setting is still read when the theme is applied.
        }
        Root.Loaded += (_, _) => RestoreTheme();
        RestoreTheme();
        _ = LoadWelcomeAsync();
        _ = StartFromSuiteAsync();
    }

    private void OpenTasksWidget(object sender, RoutedEventArgs e)
    {
        if (!_signedIn || _workspace?.Shell is not AppShell shell)
        {
            Notice.Title = "Sign in first, then open the tasks widget.";
            Notice.IsOpen = true;
            return;
        }
        TasksWidget.ShowOrFocus(shell);
    }

    private void RestoreTheme() => AppTheme.Apply(Root, AppWindow, Hwnd);

    // ----- Studio is the suite hub: company choose/create, suite.json, ticket launch -----

    /// <summary>Opens the company last remembered for the suite (written by Studio, or earlier by OSAS).</summary>
    private async void FollowSuite(object sender, RoutedEventArgs e)
    {
        if (SuiteHub.CompanyPath() is { } path)
            await OpenPathAsync(path);
    }

    /// <summary>
    /// Another suite process (or an older OSAS launcher) may start Studio with OSAS_COMPANY and a one-time
    /// OSAS_TICKET. The file opens through the ordinary path; an expired ticket leaves the sign-in page showing.
    /// </summary>
    private async Task StartFromSuiteAsync()
    {
        var path = Environment.GetEnvironmentVariable("OSAS_COMPANY");
        var ticket = Environment.GetEnvironmentVariable("OSAS_TICKET");
        Environment.SetEnvironmentVariable("OSAS_TICKET", null);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;
        for (var i = 0; i < 100 && (_session == null || _busy); i++)
            await Task.Delay(100);
        if (_session == null)
            return;
        await OpenPathAsync(path);
        if (!_companyOpen || string.IsNullOrWhiteSpace(ticket))
            return;
        await RunEngineAsync("Signing in…", () =>
        {
            _session.Call("auth.login_ticket", new { ticket });
            var status = _session.Call("auth_status");
            DispatcherQueue.TryEnqueue(() => ApplyStatus(status));
        });
    }

    private async void OpenCompany(object sender, RoutedEventArgs e) => await PickAndOpenAsync();

    private async Task PickAndOpenAsync()
    {
        if (_busy || _companyOpen || _session == null)
            return;
        string? path;
        try
        {
            path = await FileDialogs.PickCompanyFile(Hwnd);
        }
        catch (Exception ex)
        {
            ShowError("The file picker could not be opened. " + ex.Message);
            return;
        }
        if (path != null)
            await OpenPathAsync(path);
    }

    private async void OpenRecent(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RecentCompany row } && row.Exists)
            await OpenPathAsync(row.Path);
    }

    private async void RemoveRecent(object sender, RoutedEventArgs e)
    {
        if (_busy || _session == null || sender is not Button { Tag: RecentCompany row })
            return;
        var removed = await RunEngineAsync("Updating recent companies…", () =>
            _session.Call("company_recent_remove", new { uuid = row.Uuid }));
        if (removed)
            await LoadWelcomeAsync();
    }

    private async void CreateCompany(object sender, RoutedEventArgs e) => await CreateCompanyAsync();

    private async Task CreateCompanyAsync()
    {
        if (_busy || _companyOpen || _session == null)
            return;
        var nameBox = new TextBox { Header = "Company name" };
        var fileBox = new TextBox { Header = "File name", Text = "Company.osas" };
        var folderText = new TextBlock { Text = "No folder chosen", TextWrapping = TextWrapping.Wrap };
        string? folder = null;
        nameBox.TextChanged += (_, _) =>
        {
            if (!fileBox.FocusState.Equals(FocusState.Unfocused) || fileBox.Text.Length == 0)
                return;
            fileBox.Text = FileNameFor(nameBox.Text);
        };
        var choose = new Button { Content = "Choose folder…" };
        choose.Click += async (_, _) =>
        {
            try
            {
                folder = await FileDialogs.PickFolder(Hwnd);
            }
            catch (Exception ex)
            {
                ShowError("The folder picker could not be opened. " + ex.Message);
                return;
            }
            if (folder != null)
            {
                if (string.IsNullOrWhiteSpace(fileBox.Text))
                    fileBox.Text = FileNameFor(nameBox.Text);
                folderText.Text = folder;
            }
        };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Flow.Ink("TextSecondaryBrush") };
        var password = new PasswordBox { Header = "Admin password (12 or more characters)" };
        var confirm = new PasswordBox { Header = "Confirm password" };
        var review = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };

        // Three steps in one dialog: the company and where it is kept, the administrator's password, then a review before anything is made.
        var step = 0;
        var titles = new[] { "Step 1 of 3: the company", "Step 2 of 3: the administrator", "Step 3 of 3: review and create" };
        var pages = new StackPanel[]
        {
            new() { Spacing = 8, Children = { new TextBlock { Text = "Name the company and choose the folder that will hold its file.", TextWrapping = TextWrapping.Wrap }, nameBox, fileBox, choose, folderText } },
            new() { Spacing = 8, Children = { new TextBlock { Text = "The administrator signs in first and manages the other users. Choose a strong password; you will get a recovery code next.", TextWrapping = TextWrapping.Wrap }, password, confirm } },
            new() { Spacing = 8, Children = { review, new TextBlock { Text = "After the company is created a recovery code is shown once. Save it: it is the only way back in if the password is forgotten.", TextWrapping = TextWrapping.Wrap } } },
        };
        var form = new StackPanel { Spacing = 12, MinWidth = 420 };
        var heading = new TextBlock { Style = (Style)Application.Current.Resources["SectionTitleStyle"] };
        form.Children.Add(heading);
        foreach (var page in pages)
            form.Children.Add(page);
        form.Children.Add(error);
        var dialog = new ContentDialog
        {
            Title = "Create a new company",
            Content = form,
            PrimaryButtonText = "Next",
            SecondaryButtonText = "Back",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
            RequestedTheme = Root.ActualTheme,
        };
        string FullName()
        {
            var fileName = fileBox.Text.Trim();
            if (!fileName.EndsWith(".osas", StringComparison.OrdinalIgnoreCase) && !fileName.EndsWith(".adms", StringComparison.OrdinalIgnoreCase))
                fileName += ".osas";
            return fileName;
        }
        void Show()
        {
            for (var n = 0; n < pages.Length; n++)
                pages[n].Visibility = n == step ? Visibility.Visible : Visibility.Collapsed;
            heading.Text = titles[step];
            dialog.PrimaryButtonText = step == pages.Length - 1 ? "Create company" : "Next";
            dialog.IsSecondaryButtonEnabled = step > 0;
            error.Text = "";
            if (step == pages.Length - 1)
                review.Text = string.Join(Environment.NewLine, $"Company: {nameBox.Text.Trim()}", $"File: {(folder == null ? "" : Path.Combine(folder, FullName()))}", "Administrator: admin");
        }
        Show();
        string? recovery = null;
        dialog.SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            if (step > 0)
                step--;
            Show();
        };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            error.Text = "";
            try
            {
                if (step == 0)
                {
                    args.Cancel = true;
                    if (nameBox.Text.Trim().Length == 0 || fileBox.Text.Trim().Length == 0 || folder == null)
                        error.Text = "Enter the company's name, a file name, and choose a folder.";
                    else
                    {
                        step = 1;
                        Show();
                    }
                    return;
                }
                if (step == 1)
                {
                    args.Cancel = true;
                    if (password.Password.Length < 12)
                        error.Text = "The password needs 12 or more characters.";
                    else if (password.Password != confirm.Password)
                        error.Text = "The two passwords do not match.";
                    else
                    {
                        step = 2;
                        Show();
                    }
                    return;
                }
                var path = Path.Combine(folder!, FullName());
                dialog.IsPrimaryButtonEnabled = false;
                dialog.IsSecondaryButtonEnabled = false;
                var created = await Task.Run(() => _session.CreateCompany(path, nameBox.Text.Trim(), "admin", password.Password, confirm.Password));
                recovery = created.Recovery;
                password.Password = "";
                confirm.Password = "";
            }
            catch (Exception ex)
            {
                args.Cancel = true;
                error.Text = ex.Message;
                dialog.IsPrimaryButtonEnabled = true;
                dialog.IsSecondaryButtonEnabled = step > 0;
            }
            finally
            {
                deferral.Complete();
            }
        };
        await dialog.ShowAsync();
        if (recovery == null || _shuttingDown)
            return;
        await ShowRecoveryAsync(recovery, "Save your recovery code", "This code is shown once. It is the only way to recover the owner account if the password is forgotten.");
        await RefreshStatusAsync();
    }

    private nint Hwnd => WindowNative.GetWindowHandle(this);

    internal nint Handle => Hwnd;

    internal Task<bool> RunWork(string status, Action work) => RunEngineAsync(status, work);

    internal void ShowProblem(string message) => ShowError(message);

    private PdfViewer? _pdf;
    private Grid? _show;
    private AppWindowPresenterKind _showWas;

    internal void BeginShow(UIElement stage)
    {
        EndShow();
        _showWas = AppWindow.Presenter.Kind;
        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        _show = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Black) };
        _show.Children.Add(stage);
        Grid.SetRowSpan(_show, 4);
        Root.Children.Add(_show);
        if (stage is Control control)
            control.Focus(FocusState.Programmatic);
    }

    internal void EndShow()
    {
        if (_show == null)
            return;
        Root.Children.Remove(_show);
        _show = null;
        var restore = _showWas == AppWindowPresenterKind.FullScreen ? AppWindowPresenterKind.Overlapped : _showWas;
        AppWindow.SetPresenter(restore);
    }

    internal async Task ShowPdf(byte[] pdf, string name)
    {
        _pdf ??= new PdfViewer(() => ClosePdf());
        if (PdfHost.Children.Count == 0)
            PdfHost.Children.Add(_pdf);
        PdfHost.Visibility = Visibility.Visible;
        await _pdf.Load(this, pdf, name);
    }

    internal async Task PrintPagesAsync(string jobName, IReadOnlyList<UIElement> pages)
    {
        if (pages.Count == 0)
        {
            ShowProblem("There is nothing to print.");
            return;
        }
        _printPages = pages;
        _printJob = jobName;
        EnsurePrinter();
        try
        {
            await PrintManagerInterop.ShowPrintUIForWindowAsync(Hwnd);
        }
        catch (Exception ex)
        {
            ShowProblem("Printing could not be opened. " + ex.Message);
        }
    }

    private void EnsurePrinter()
    {
        if (_printDocument != null)
            return;
        _printDocument = new PrintDocument();
        _printSource = _printDocument.DocumentSource;
        _printDocument.Paginate += (_, _) => _printDocument.SetPreviewPageCount(_printPages.Count, PreviewPageCountType.Final);
        _printDocument.GetPreviewPage += (_, args) =>
        {
            if (args.PageNumber >= 1 && args.PageNumber <= _printPages.Count)
                _printDocument.SetPreviewPage(args.PageNumber, _printPages[args.PageNumber - 1]);
        };
        _printDocument.AddPages += (_, _) =>
        {
            foreach (var page in _printPages)
                _printDocument.AddPage(page);
            _printDocument.AddPagesComplete();
        };
        var manager = PrintManagerInterop.GetForWindow(Hwnd);
        manager.PrintTaskRequested += (_, args) => args.Request.CreatePrintTask(_printJob, source => source.SetSource(_printSource));
    }

    private bool ClosePdf()
    {
        if (PdfHost.Visibility != Visibility.Visible)
            return false;
        PdfHost.Visibility = Visibility.Collapsed;
        _pdf?.Clear();
        StatusText.Text = "Preview closed";
        return true;
    }

    private void RememberPlacement(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (sender.Presenter is not OverlappedPresenter presenter)
            return;
        if (presenter.State == OverlappedPresenterState.Maximized)
        {
            _maximized = true;
            return;
        }
        if (presenter.State == OverlappedPresenterState.Restored && sender.Size.Width > 0 && sender.Size.Height > 0)
        {
            _maximized = false;
            _restored = new RectInt32(sender.Position.X, sender.Position.Y, sender.Size.Width, sender.Size.Height);
        }
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        SavePlacement();
        // The assistant's model process is O-Studio's own; it goes when the window does.
        AssistantBar.Runtime?.Stop();
        if (_shuttingDown)
            return;
        if (_busy)
        {
            args.Cancel = true;
            _closeAfterBusy = true;
            ShowError("O-Studio will close after the current operation finishes. Nothing is written until you confirm a save.");
            return;
        }
        if (!_allowClose && _signedIn && _workspace != null)
        {
            args.Cancel = true;
            _ = ConfirmClose();
            return;
        }
        _shuttingDown = true;
        ReleaseSession();
    }

    private async Task ConfirmClose()
    {
        if (_workspace != null && !await _workspace.ConfirmLeave())
            return;
        _allowClose = true;
        Close();
    }

    private void SavePlacement()
    {
        var placement = new WindowPlacement
        {
            X = _restored.X,
            Y = _restored.Y,
            Width = _restored.Width,
            Height = _restored.Height,
            Maximized = _maximized,
        };
        if (placement.Width < 480 || placement.Height < 480)
        {
            placement.Width = Math.Max(placement.Width, 960);
            placement.Height = Math.Max(placement.Height, 600);
        }
        try
        {
            placement.Save();
        }
        catch (IOException)
        {
        }
    }

    private void ReleaseSession()
    {
        var session = _session;
        _session = null;
        if (session == null)
            return;
        try
        {
            if (_companyOpen)
                session.CloseCompany();
        }
        catch (AdmsException)
        {
        }
        session.Dispose();
        _companyOpen = false;
        _signedIn = false;
    }




    private async Task OpenPathAsync(string path)
    {
        if (_busy || _companyOpen || _session == null)
            return;
        JsonElement check = default;
        var valid = await RunEngineAsync("Checking file…", () =>
        {
            check = _session.Call("company_validate", new { path });
        });
        if (!valid || _closeAfterBusy || _shuttingDown)
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
                    Text = $"{name}\n{path}\n\nThis file was saved by an older version. A safety copy is made next to it before it is upgraded.",
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "Open",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot,
            RequestedTheme = Root.ActualTheme,
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
            var recovery = new CheckBox { Content = "This is the recovery code", MinHeight = 32 };
            var stack = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = "This company file is protected. Your password opens it. The recovery code opens it when the password is forgotten, and you then set a new password.",
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
                RequestedTheme = Root.ActualTheme,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;
            secret = box.Password;
            unlock = recovery.IsChecked == true ? "recovery" : "password";
            if (secret.Length == 0)
            {
                ShowError(unlock == "recovery" ? "Enter the recovery code." : "Enter the password that opens this company file.");
                return;
            }
        }
        var opened = await RunEngineAsync("Opening company…", () =>
        {
            _session.Call("company_open", new { path, secret, unlock });
            var status = _session.Call("auth_status");
            DispatcherQueue.TryEnqueue(() => ApplyStatus(status));
        });
        if (opened && unlock == "recovery" && _companyOpen)
        {
            ShowRecover(this, new RoutedEventArgs());
            StatusText.Text = "Set a new password. It opens this company file, and a new recovery code is shown once.";
        }
    }


    private void CloseCompany(object sender, RoutedEventArgs e) => _ = CloseCompanyNow();

    private async Task CloseCompanyNow()
    {
        if (_busy || !_companyOpen || _session == null)
            return;
        if (_workspace != null && !await _workspace.ConfirmLeave())
            return;
        try
        {
            _session.CloseCompany();
            _companyOpen = false;
            _signedIn = false;
            _companyName = "";
            _companyPath = "";
            _companyUuid = "";
            _role = "";
            Title = "O-Studio";
            ClearSecrets();
            ClearWorkspace();
            ShowWelcome();
            StatusText.Text = "Company closed";
            Notice.IsOpen = false;
            _ = LoadWelcomeAsync();
        }
        catch (AdmsException ex)
        {
            ShowError(ex.Message);
        }
    }



    private async void ConvertLegacy(object sender, RoutedEventArgs e)
    {
        if (_busy || _companyOpen || _session == null)
            return;
        string? folder;
        try
        {
            folder = await FileDialogs.PickFolder(Hwnd);
        }
        catch (Exception ex)
        {
            ShowError("The folder picker could not be opened. " + ex.Message);
            return;
        }
        if (folder == null)
            return;
        var path = Path.Combine(folder, "My company.osas");
        await RunEngineAsync("Converting…", () =>
        {
            _session.Call("company_convert_legacy", new { path });
            var status = _session.Call("auth_status");
            DispatcherQueue.TryEnqueue(() => ApplyStatus(status));
        });
    }

    private void PasswordKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;
        e.Handled = true;
        SignIn(sender, new RoutedEventArgs());
    }

    private void SetupKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;
        e.Handled = true;
        CreateOwner(sender, new RoutedEventArgs());
    }

    private void RecoverKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;
        e.Handled = true;
        ResetPassword(sender, new RoutedEventArgs());
    }

    private void UnlockKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;
        e.Handled = true;
        Unlock(sender, new RoutedEventArgs());
    }

    private async void SignIn(object sender, RoutedEventArgs e)
    {
        if (_busy || _session == null || !_companyOpen)
            return;
        var password = PasswordBox.Password;
        var username = UsernameBox.Text.Trim();
        if (username.Length == 0 || password.Length == 0)
        {
            ShowError("Enter your username and password.");
            return;
        }
        await RunEngineAsync("Signing in…", () =>
        {
            _session.Login(username, password);
            var status = _session.Call("auth_status");
            DispatcherQueue.TryEnqueue(() =>
            {
                PasswordBox.Password = "";
                ApplyStatus(status);
            });
        });
    }

    private async void CreateOwner(object sender, RoutedEventArgs e)
    {
        if (_busy || _session == null || !_companyOpen)
            return;
        var password = SetupPassword.Password;
        var confirm = SetupConfirm.Password;
        if (password.Length == 0)
        {
            ShowError("Enter the admin password.");
            return;
        }
        string? code = null;
        var created = await RunEngineAsync("Creating the admin account…", () =>
        {
            code = _session.Call("auth_setup", new { username = "admin", password, confirm }).GetString();
        });
        SetupPassword.Password = "";
        SetupConfirm.Password = "";
        if (!created || string.IsNullOrWhiteSpace(code) || _closeAfterBusy || _shuttingDown)
            return;
        await ShowRecoveryAsync(code, "Save your recovery code", "This code is shown once. Store it somewhere safe; it is the only way to recover the owner account if the password is forgotten.");
        await RefreshStatusAsync();
    }

    private void ShowRecover(object sender, RoutedEventArgs e)
    {
        if (_busy || !_companyOpen)
            return;
        ShowOnly(RecoverPanel);
        if (string.IsNullOrWhiteSpace(RecoverUser.Text))
            RecoverUser.Text = UsernameBox.Text;
        RecoverUser.Focus(FocusState.Programmatic);
    }

    private void BackToSignIn(object sender, RoutedEventArgs e) => ShowLogin(_companyName);

    private async void ResetPassword(object sender, RoutedEventArgs e)
    {
        if (_busy || _session == null || !_companyOpen)
            return;
        var code = RecoverCode.Text.Trim();
        var username = RecoverUser.Text.Trim();
        var password = RecoverPassword.Password;
        var confirm = RecoverConfirm.Password;
        if (username.Length == 0 || code.Length == 0 || password.Length == 0)
        {
            ShowError("Enter the username, the recovery code, and a new password.");
            return;
        }
        string? fresh = null;
        var reset = await RunEngineAsync("Resetting password…", () =>
        {
            var args = new Dictionary<string, string>
            {
                ["username"] = username,
                ["code"] = code,
                ["new"] = password,
                ["confirm"] = confirm,
            };
            fresh = _session.Call("auth_recover", args).GetString();
        });
        RecoverPassword.Password = "";
        RecoverConfirm.Password = "";
        RecoverCode.Text = "";
        if (!reset || string.IsNullOrWhiteSpace(fresh) || _closeAfterBusy || _shuttingDown)
            return;
        await ShowRecoveryAsync(fresh, "Password reset", "Your old recovery code no longer works. Store this new one safely; it is shown once.");
        ShowLogin(_companyName);
    }

    private async void Unlock(object sender, RoutedEventArgs e)
    {
        if (_busy || _session == null || !_companyOpen)
            return;
        var password = UnlockPassword.Password;
        if (password.Length == 0)
        {
            ShowError("Enter your password.");
            return;
        }
        await RunEngineAsync("Unlocking…", () =>
        {
            _session.Call("auth_unlock", new { password });
            var status = _session.Call("auth_status");
            DispatcherQueue.TryEnqueue(() =>
            {
                UnlockPassword.Password = "";
                ApplyStatus(status);
            });
        });
    }

    private async void LockSession(object sender, RoutedEventArgs e)
    {
        if (_busy || _session == null || !_signedIn)
            return;
        if (_workspace != null && !await _workspace.ConfirmLeave())
            return;
        await RunEngineAsync("Locking…", () =>
        {
            _session.Call("auth_lock");
            var status = _session.Call("auth_status");
            DispatcherQueue.TryEnqueue(() => ApplyStatus(status));
        });
    }

    private async void LogOut(object sender, RoutedEventArgs e)
    {
        if (_busy || _session == null || !_signedIn)
            return;
        if (_workspace != null && !await _workspace.ConfirmLeave())
            return;
        await RunEngineAsync("Signing out…", () =>
        {
            _session.Call("auth_logout");
            var status = _session.Call("auth_status");
            DispatcherQueue.TryEnqueue(() => ApplyStatus(status));
        });
    }

    private async void GeneratePreview(object sender, RoutedEventArgs e)
    {
        if (_busy || _session == null || !_signedIn)
            return;
        byte[]? pdf = null;
        var produced = await RunEngineAsync("Generating preview…", () =>
        {
            pdf = _session.SamplePdf();
        });
        if (!produced || pdf == null || _closeAfterBusy || _shuttingDown)
            return;
        await ShowPdf(pdf, SafeFileName(_companyName));
        StatusText.Text = "Preview ready";
        Notice.IsOpen = false;
    }

    private async void CheckForUpdates(object sender, RoutedEventArgs e)
    {
        var result = await UpdateCheck.CheckAsync();
        var dialog = new ContentDialog
        {
            Title = result.UpdateAvailable ? "Update available" : "Check for updates",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = result.Message, TextWrapping = TextWrapping.WrapWholeWords },
                    new TextBlock
                    {
                        Text = "Updates are not installed automatically. See docs/packaging.md for signing and the suite channel.",
                        Opacity = 0.7,
                        TextWrapping = TextWrapping.WrapWholeWords,
                    },
                },
            },
            CloseButtonText = "Close",
            XamlRoot = Content.XamlRoot,
            RequestedTheme = Root.ActualTheme,
        };
        await dialog.ShowAsync();
    }

    private async void ShowAbout(object sender, RoutedEventArgs e)
    {
        uint version = 0;
        try
        {
            version = AdmsSession.ApiVersion;
        }
        catch (DllNotFoundException)
        {
        }
        var dialog = new ContentDialog
        {
            Title = "About O-Studio",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new Image { Source = BrandArt.Wordmark(), MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Left, Stretch = Stretch.Uniform },
                    new TextBlock { Text = "Developed by Human Centric Works" },
                    new TextBlock { Text = $"Version {UpdateCheck.ProductVersion} · Engine ABI {version}", Opacity = 0.7 },
                    new TextBlock { Text = "Ctrl+N new record, Ctrl+S save draft, Ctrl+P print, Ctrl+F search, Ctrl+K go to a page.", TextWrapping = TextWrapping.WrapWholeWords },
                },
            },
            CloseButtonText = "Close",
            XamlRoot = Content.XamlRoot,
            RequestedTheme = Root.ActualTheme,
        };
        await dialog.ShowAsync();
    }

    private void ExitApplication(object sender, RoutedEventArgs e) => Close();

    private void NewRecord(object sender, RoutedEventArgs e)
    {
        if (_signedIn && !_busy)
            _workspace?.NewItem();
    }

    private void SaveRecord(object sender, RoutedEventArgs e)
    {
        if (_signedIn && !_busy)
            _workspace?.Save();
    }

    private void GoDashboard(object sender, RoutedEventArgs e)
    {
        if (_signedIn)
            _workspace?.Show("dashboard");
    }

    private void GoCompany(object sender, RoutedEventArgs e)
    {
        if (_signedIn)
            _workspace?.Show("company");
    }

    private void DismissNotice(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!Notice.IsOpen && !ClosePdf())
            return;
        if (Notice.IsOpen)
            Notice.IsOpen = false;
        args.Handled = true;
    }

    private void NewCommand(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_busy)
            return;
        if (_signedIn)
        {
            args.Handled = true;
            _workspace?.NewItem();
            return;
        }
        if (!_companyOpen)
        {
            args.Handled = true;
            _ = CreateCompanyAsync();
        }
    }

    private void SaveCommand(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (PdfHost.Visibility == Visibility.Visible)
        {
            args.Handled = true;
            _pdf?.Save();
            return;
        }
        if (!_signedIn || _busy)
            return;
        args.Handled = true;
        _workspace?.Save();
    }

    private void PrintCommand(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!_signedIn || _busy)
            return;
        args.Handled = true;
        _workspace?.Print();
    }

    private void FindCommand(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!_signedIn || _busy)
            return;
        args.Handled = true;
        _workspace?.FocusSearch();
    }

    private async void GoToCommand(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!_signedIn || _workspace == null || _busy)
            return;
        args.Handled = true;
        var box = new TextBox { PlaceholderText = "Go to a page or command", Header = "Go to" };
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true, MaxHeight = 320 };
        void Fill()
        {
            list.Items.Clear();
            var query = box.Text.Trim();
            foreach (var (id, label) in _workspace.Destinations)
            {
                if (query.Length == 0 || label.Contains(query, StringComparison.OrdinalIgnoreCase) || id.Contains(query, StringComparison.OrdinalIgnoreCase))
                    list.Items.Add(new GoItem(id, label));
            }
            list.Items.Add(new GoItem("lock", "Lock the screen"));
            list.Items.Add(new GoItem("logout", "Log out"));
        }
        box.TextChanged += (_, _) => Fill();
        Fill();
        var panel = new StackPanel { Spacing = 8, Width = 360 };
        panel.Children.Add(box);
        panel.Children.Add(list);
        var dialog = new ContentDialog
        {
            Title = "Go to",
            Content = panel,
            CloseButtonText = "Cancel",
            XamlRoot = Content.XamlRoot,
            RequestedTheme = Root.ActualTheme,
        };
        void Pick(object item)
        {
            if (item is not GoItem go)
                return;
            dialog.Hide();
            if (go.Id == "lock")
                LockSession(this, new RoutedEventArgs());
            else if (go.Id == "logout")
                LogOut(this, new RoutedEventArgs());
            else
                _workspace.Show(go.Id);
        }
        list.ItemClick += (_, e) => Pick(e.ClickedItem);
        box.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter && list.Items.Count > 0)
            {
                e.Handled = true;
                Pick(list.Items[0]);
            }
        };
        dialog.Opened += (_, _) => box.Focus(FocusState.Programmatic);
        await dialog.ShowAsync();
    }

    private async Task<bool> RunEngineAsync(string status, Action work)
    {
        if (_session == null)
            return false;
        SetBusy(true, status);
        try
        {
            await Task.Run(work);
            if (!_closeAfterBusy)
            {
                StatusText.Text = "";
                Notice.IsOpen = false;
            }
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
            SetBusy(false, StatusText.Text);
            if (_closeAfterBusy)
                FinishClose();
        }
    }

    private async Task RefreshStatusAsync()
    {
        if (_session == null)
            return;
        await RunEngineAsync("Checking session…", () =>
        {
            var status = _session.Call("auth_status");
            DispatcherQueue.TryEnqueue(() => ApplyStatus(status));
        });
    }

    private async Task LoadWelcomeAsync()
    {
        if (_session == null || _companyOpen)
            return;
        JsonElement legacy = default;
        JsonElement recent = default;
        var loaded = await RunEngineAsync("Looking for earlier data…", () =>
        {
            legacy = _session.Call("company_legacy_info");
            recent = _session.Call("company_recent_list");
        });
        if (!loaded || _companyOpen)
            return;
        var chosen = SuiteHub.CompanyPath();
        FollowButton.Visibility = chosen != null ? Visibility.Visible : Visibility.Collapsed;
        if (chosen != null)
            FollowButton.Content = "Open " + Path.GetFileNameWithoutExtension(chosen) + " (last chosen for the suite)";
        LegacyNotice.IsOpen = legacy.ValueKind == JsonValueKind.Object;
        var rows = new List<RecentCompany>();
        if (recent.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in recent.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                    continue;
                var path = Text(row, "path");
                var exists = File.Exists(path);
                var opened = "";
                if (row.TryGetProperty("opened_at", out var at) && at.TryGetInt64(out var seconds) && seconds > 0)
                    opened = DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime.ToString("dd MMM yyyy");
                var detail = exists ? path : path + " (file not found)";
                if (opened.Length > 0)
                    detail += " · " + opened;
                rows.Add(new RecentCompany
                {
                    Uuid = Text(row, "uuid"),
                    Name = Text(row, "name"),
                    Path = path,
                    Detail = detail,
                    Exists = exists,
                });
            }
        }
        RecentList.ItemsSource = rows;
        RecentHeading.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyStatus(JsonElement status)
    {
        if (status.ValueKind != JsonValueKind.Object || !status.TryGetProperty("company", out var company) || company.ValueKind != JsonValueKind.Object)
        {
            _companyOpen = false;
            _signedIn = false;
            ClearWorkspace();
            ShowWelcome();
            _ = LoadWelcomeAsync();
            return;
        }
        _companyOpen = true;
        _companyName = Text(company, "name");
        _companyPath = Text(company, "path");
        _companyUuid = Text(company, "uuid");
        if (!string.IsNullOrWhiteSpace(_companyPath))
            SuiteHub.RememberCompany(_companyPath);
        Title = string.IsNullOrWhiteSpace(_companyName) ? "O-Studio" : $"{_companyName} — O-Studio";
        var setup = status.TryGetProperty("setup_required", out var setupFlag) && setupFlag.ValueKind == JsonValueKind.True;
        if (setup)
        {
            ClearWorkspace();
            ShowSetup();
            return;
        }
        if (!status.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object)
        {
            ClearWorkspace();
            ShowLogin(_companyName);
            if (string.IsNullOrWhiteSpace(UsernameBox.Text) && SuiteHub.RememberedUsername() is { } remembered)
                UsernameBox.Text = remembered;
            return;
        }
        var username = Text(user, "username");
        _role = Text(user, "role");
        if (!string.IsNullOrWhiteSpace(_companyPath))
            SuiteHub.RememberCompany(_companyPath, username);
        var locked = status.TryGetProperty("locked", out var lockedFlag) && lockedFlag.ValueKind == JsonValueKind.True;
        if (locked)
        {
            ClearWorkspace();
            ShowUnlock(username);
            return;
        }
        ShowWorkspace(username, _role);
    }

    private void FinishClose()
    {
        if (_shuttingDown)
            return;
        _shuttingDown = true;
        ReleaseSession();
        Close();
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        BusyRing.IsActive = busy;
        StatusText.Text = status;
        UpdateCommands();
    }

    private void ShowOnly(UIElement panel)
    {
        WelcomePanel.Visibility = panel == WelcomePanel ? Visibility.Visible : Visibility.Collapsed;
        LoginPanel.Visibility = panel == LoginPanel ? Visibility.Visible : Visibility.Collapsed;
        SetupPanel.Visibility = panel == SetupPanel ? Visibility.Visible : Visibility.Collapsed;
        RecoverPanel.Visibility = panel == RecoverPanel ? Visibility.Visible : Visibility.Collapsed;
        UnlockPanel.Visibility = panel == UnlockPanel ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceHost.Visibility = panel == WorkspaceHost ? Visibility.Visible : Visibility.Collapsed;
        UpdateCommands();
    }

    private void ShowWelcome() => ShowOnly(WelcomePanel);

    private void ShowLogin(string name)
    {
        LoginHeading.Text = string.IsNullOrWhiteSpace(name) ? "Sign in" : name;
        ShowOnly(LoginPanel);
        Notice.IsOpen = false;
        UsernameBox.Focus(FocusState.Programmatic);
    }

    private void ShowSetup()
    {
        SetupHeading.Text = string.IsNullOrWhiteSpace(_companyName) ? "Set up the admin password" : _companyName;
        ShowOnly(SetupPanel);
        Notice.IsOpen = false;
        SetupPassword.Focus(FocusState.Programmatic);
    }

    private void ShowUnlock(string username)
    {
        UnlockHeading.Text = "Session locked";
        ShowOnly(UnlockPanel);
        Notice.IsOpen = false;
        UnlockPassword.Focus(FocusState.Programmatic);
    }

    private static string CanonicalRole(string role) => role.Trim().ToLowerInvariant() switch
    {
        "owner" => "Owner",
        "principal" => "Principal",
        "accounts" => "Accounts",
        "document_editor" or "documenteditor" => "DocumentEditor",
        "auditor" => "Auditor",
        _ => role,
    };

    private void ShowWorkspace(string username, string role)
    {
        _signedIn = true;
        _role = CanonicalRole(role);
        ClearWorkspace();
        _signedIn = true;
        _workspace = new WorkspaceView(new AppShell { Session = _session!, Window = this, Role = _role, User = username, CompanyName = _companyName, CompanyPath = _companyPath });
        WorkspaceHost.Children.Add(_workspace);
        ShowOnly(WorkspaceHost);
        Notice.IsOpen = false;
        StatusText.Text = "Signed in";
    }

    private void ClearWorkspace()
    {
        _signedIn = false;
        _workspace = null;
        WorkspaceHost.Children.Clear();
    }

    private void ClearSecrets()
    {
        UsernameBox.Text = "";
        PasswordBox.Password = "";
        SetupUser.Text = "";
        SetupPassword.Password = "";
        SetupConfirm.Password = "";
        RecoverUser.Text = "";
        RecoverCode.Text = "";
        RecoverPassword.Password = "";
        RecoverConfirm.Password = "";
        UnlockPassword.Password = "";
    }

    private async Task ShowRecoveryAsync(string code, string title, string message)
    {
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = code, IsTextSelectionEnabled = true, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas") });
        var dialog = new ContentDialog
        {
            Title = title,
            Content = body,
            CloseButtonText = "I have stored it",
            XamlRoot = Content.XamlRoot,
            RequestedTheme = Root.ActualTheme,
        };
        await dialog.ShowAsync();
    }

    private void UpdateCommands()
    {
        var engine = _session != null;
        NewCompanyItem.IsEnabled = !_busy && !_companyOpen && engine;
        NewCompanyButton.IsEnabled = !_busy && !_companyOpen && engine;
        OpenItem.IsEnabled = !_busy && !_companyOpen && engine;
        OpenCompanyButton.IsEnabled = !_busy && !_companyOpen && engine;
        FollowButton.IsEnabled = !_busy && !_companyOpen && engine;
        CloseItem.IsEnabled = !_busy && _companyOpen;
        LockItem.IsEnabled = !_busy && _signedIn;
        LogoutItem.IsEnabled = !_busy && _signedIn;
        PreviewItem.IsEnabled = !_busy && _signedIn;
        SignInButton.IsEnabled = !_busy && _companyOpen;
        SetupButton.IsEnabled = !_busy && _companyOpen;
        RecoverButton.IsEnabled = !_busy && _companyOpen;
        UnlockButton.IsEnabled = !_busy && _companyOpen;
        UsernameBox.IsEnabled = !_busy;
        PasswordBox.IsEnabled = !_busy;
        ForgotButton.IsEnabled = !_busy;
        SwitchFromLogin.IsEnabled = !_busy && _companyOpen;
    }

    /// <summary>The last error shown at the top of the window, so a page can mark the field it is about.</summary>
    internal string LastError { get; private set; } = "";

    private void ShowError(string message)
    {
        LastError = message?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(message))
        {
            Notice.IsOpen = false;
            return;
        }
        Notice.RequestedTheme = AppTheme.InHighContrast ? ElementTheme.Default : ElementTheme.Light;
        Notice.Foreground = Flow.Ink("TextPrimaryBrush");
        Notice.Severity = InfoBarSeverity.Error;
        Notice.Title = message.Trim();
        Notice.Message = "";
        Notice.IsOpen = true;
        StatusText.Text = "";
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static string FileNameFor(string company)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(company.Select(ch => invalid.Contains(ch) ? ' ' : ch).ToArray()).Trim();
        if (cleaned.Length == 0)
            cleaned = "Company";
        return cleaned + ".osas";
    }

    private static string SafeFileName(string company)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(company.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray()).Trim();
        if (cleaned.Length == 0)
            cleaned = "O-Studio";
        return cleaned + " preview";
    }

    private sealed record GoItem(string Id, string Label)
    {
        public override string ToString() => Label;
    }
}
