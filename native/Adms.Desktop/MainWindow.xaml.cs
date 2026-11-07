using System.Text.Json;
using Adms.Cloud;
using Adms.Interop;
using Osas.Core;
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
    private string _printJob = "Kivo";
    private readonly ProgressRing _busyRing = new() { Width = 20, Height = 20, IsActive = false };
    private readonly O.Ui.Controls.SuiteThemeSwitch _suiteTheme = new();

    public MainWindow()
    {
        InitializeComponent();
        O.Ui.SuiteMotion.RespectSystemPreference();
        StatusStrip.Left.Children.Add(_busyRing);
        _suiteTheme.Attach(this);
        StatusStrip.Right.Children.Add(_suiteTheme);
        StatusStrip.Right.Children.Add(new TextBlock
        {
            Text = "Human Centric Works",
            FontSize = O.Ui.SuiteScale.FontCaption,
            VerticalAlignment = VerticalAlignment.Center,
        });
        O.Ui.SuiteAppearance.Changed += _ => DispatcherQueue.TryEnqueue(() =>
        {
            RestoreTheme();
            StatusStrip.Retheme();
        });
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
        // The title bar, window frame and menu take their colours from the theme, so they follow it when it is switched.
        try
        {
            var dir = AppFolder.Prepare();
            _session = new AdmsSession(dir);
            StatusStrip.StatusText = "Engine ready";
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

    private void OpenCompanyLoginWidget(object sender, RoutedEventArgs e)
    {
        if (!CompanyLoginLauncher.TryLaunch(out var error) && error != null)
            ShowError(error);
    }

    private void RestoreTheme() => AppTheme.Apply(Root, AppWindow, Hwnd);

    // ----- Studio is the suite hub: suite.json, ticket launch. Companies are chosen and created in the launcher. -----

    /// <summary>Opens the company last remembered for the suite (written by Studio, or earlier by OSAS).</summary>
    private async void FollowSuite(object sender, RoutedEventArgs e)
    {
        if (SuiteHub.CompanyPath() is { } path)
            await OpenPathAsync(path);
    }

    /// <summary>
    /// A sibling app may start Studio with <c>OSTUDIO_COMPANY</c> and a one-time
    /// <c>OSTUDIO_TICKET</c>. The file opens through the ordinary path; an expired ticket leaves the launcher prompt showing.
    /// </summary>
    private async Task StartFromSuiteAsync()
    {
        var path = Environment.GetEnvironmentVariable(SuiteWire.EnvCompany);
        var ticket = Environment.GetEnvironmentVariable(SuiteWire.EnvTicket);
        Environment.SetEnvironmentVariable(SuiteWire.EnvTicket, null);
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

    private async void CreateCompany(object sender, RoutedEventArgs e) => await CreateCompanyAsync();

    private async Task CreateCompanyAsync()
    {
        if (_session == null)
        {
            ShowError("The accounting engine is not loaded. Rebuild O-Studio with the Rust engine (src-tauri, cargo build --release) and ensure architect_office_desktop_lib.dll is beside O-Studio.exe.");
            return;
        }
        if (_busy)
        {
            ShowError("Wait for the current operation to finish, then try again.");
            return;
        }
        if (_companyOpen)
        {
            ShowError("Close the current company before creating a new one.");
            return;
        }
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
        string? pendingPath = null;
        string? pendingName = null;
        string? pendingPassword = null;
        dialog.SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            if (step > 0)
                step--;
            Show();
        };
        dialog.PrimaryButtonClick += (_, args) =>
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
                pendingPath = Path.Combine(folder!, FullName());
                pendingName = nameBox.Text.Trim();
                pendingPassword = password.Password;
                password.Password = "";
                confirm.Password = "";
            }
            finally
            {
                deferral.Complete();
            }
        };
        await dialog.ShowAsync();
        if (pendingPath == null || pendingName == null || pendingPassword == null || _shuttingDown)
            return;

        string? recovery = null;
        var createdOk = await RunEngineAsync("Creating company…", () =>
        {
            var created = _session!.CreateCompany(pendingPath, pendingName, "admin", pendingPassword, pendingPassword);
            recovery = created.Recovery;
        });
        if (!createdOk || recovery == null || _shuttingDown)
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
        StatusStrip.StatusText = "Preview closed";
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
        Office.Ai.SuiteAiCoordinator.StopHost();
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
        _ = CloudSyncHub.StopAsync();
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
            StatusStrip.StatusText = "Set a new password. It opens this company file, and a new recovery code is shown once.";
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
            Title = "Kivo";
            ClearSecrets();
            ClearWorkspace();
            ShowWelcome();
            StatusStrip.StatusText = "Company closed";
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
            RecoverUser.Text = SuiteHub.RememberedUsername() ?? "";
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
        StatusStrip.StatusText = "Preview ready";
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
            OpenCompanyLoginWidget(this, new RoutedEventArgs());
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
                StatusStrip.StatusText = "";
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
            SetBusy(false, StatusStrip.StatusText);
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
        var loaded = await RunEngineAsync("Looking for earlier data…", () =>
        {
            legacy = _session.Call("company_legacy_info");
        });
        if (!loaded || _companyOpen)
            return;
        var chosen = SuiteHub.CompanyPath();
        FollowButton.Visibility = chosen != null ? Visibility.Visible : Visibility.Collapsed;
        if (chosen != null)
            FollowButton.Content = "Open " + Path.GetFileNameWithoutExtension(chosen) + " (last chosen for the suite)";
        LegacyNotice.IsOpen = legacy.ValueKind == JsonValueKind.Object;
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
        Title = string.IsNullOrWhiteSpace(_companyName) ? "Kivo" : $"{_companyName} — Kivo";
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
        _busyRing.IsActive = busy;
        StatusStrip.StatusText = status;
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

    private bool _launcherOffered;

    private void ShowLogin(string name)
    {
        LoginHeading.Text = string.IsNullOrWhiteSpace(name) ? "Sign in with the launcher" : name;
        ShowOnly(LoginPanel);
        Notice.IsOpen = false;
        // Sign-in lives in the launcher: start it once per run so the user is not left looking at a dead end.
        if (!_launcherOffered)
        {
            _launcherOffered = true;
            CompanyLoginLauncher.TryLaunch(out _);
        }
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
        _workspace = new WorkspaceView(new AppShell { Session = _session!, Window = this, Role = _role, User = username, CompanyName = _companyName, CompanyPath = _companyPath, CompanyUuid = _companyUuid });
        WorkspaceHost.Children.Add(_workspace);
        _workspace.WireBottomBar(StatusStrip);
        ShowOnly(WorkspaceHost);
        Notice.IsOpen = false;
        StatusStrip.StatusText = "Signed in";
    }

    private void ClearWorkspace()
    {
        _signedIn = false;
        _workspace = null;
        WorkspaceHost.Children.Clear();
        if (_workspace is not null)
            _workspace.ClearBottomBar(StatusStrip);
    }

    private void ClearSecrets()
    {
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
        ChooseCompanyItem.IsEnabled = !_busy;
        OpenCompanyButton.IsEnabled = !_busy;
        FollowButton.IsEnabled = !_busy && !_companyOpen && engine;
        CloseItem.IsEnabled = !_busy && _companyOpen;
        LockItem.IsEnabled = !_busy && _signedIn;
        LogoutItem.IsEnabled = !_busy && _signedIn;
        PreviewItem.IsEnabled = !_busy && _signedIn;
        SetupButton.IsEnabled = !_busy && _companyOpen;
        RecoverButton.IsEnabled = !_busy && _companyOpen;
        UnlockButton.IsEnabled = !_busy && _companyOpen;
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
        StatusStrip.StatusText = "";
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
            cleaned = "Kivo";
        return cleaned + " preview";
    }

    private sealed record GoItem(string Id, string Label)
    {
        public override string ToString() => Label;
    }
}
