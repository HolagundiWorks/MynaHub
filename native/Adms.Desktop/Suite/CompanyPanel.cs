using Hcw.Ui.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Osas.Core.Suite;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Adms.Desktop;

/// <summary>
/// The company the whole suite works in: where its file is, who is signed in, and the company's details. The apps follow what is chosen
/// here, and O-Studio signs them in as the same person with a one-time ticket, so the password is typed once and never passed on.
/// </summary>
public sealed partial class CompanyPanel : StackPanel
{
    private static readonly string EngineFolder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OSAS", "office");

    private readonly Window _window;
    private readonly SuiteSettings _settings;
    private readonly TextBlock _name = new() { Style = Resource<Style>("SectionTitleStyle") };
    private readonly TextBlock _path = new() { Style = Resource<Style>("CaptionStyle"), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly TextBlock _who = new() { Style = Resource<Style>("BodyStyle") };
    private readonly InfoBar _notice = new() { IsOpen = false, IsClosable = true };
    private readonly Button _signIn = new() { Content = "Sign in…" };
    private readonly Button _signOut = new() { Content = "Sign out" };
    private readonly Button _details = new() { Content = "Company details…" };
    private SuiteCompany? _company;

    public CompanyPanel(Window window, SuiteSettings settings)
    {
        _window = window;
        _settings = settings;
        Spacing = 8;
        var heading = new TextBlock { Text = "Company", Style = Resource<Style>("SectionTitleStyle") };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        Children.Add(heading);
        Children.Add(new TextBlock
        {
            Text = "The company file the whole suite works in. It is chosen or created only here: sign in once, and the apps open it already signed in as you.",
            Style = Resource<Style>("SecondaryStyle"),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 760,
            HorizontalAlignment = HorizontalAlignment.Left,
        });
        var choose = new Button { Content = "Choose company file…" };
        choose.Click += async (_, _) => await Choose();
        _create.Click += async (_, _) => await CreateCompany();
        _signIn.Click += async (_, _) => await SignIn();
        _signOut.Click += (_, _) => SignOut();
        _details.Click += async (_, _) => await EditDetails();
        Children.Add(_name);
        Children.Add(_path);
        Children.Add(_who);
        Children.Add(new FlexBar { Children = { choose, _create, _signIn, _signOut, _details } });
        Children.Add(_notice);
        BuildUsers();
        Paint();
    }

    /// <summary>Raised when the company chosen, or who is signed in, changes.</summary>
    public event Action? Changed;

    public string? CompanyPath => _settings.CompanyPath;

    public bool SignedIn => _company != null;

    /// <summary>The company signed in to here, or null. The assistant works as this person.</summary>
    public SuiteCompany? Company => _company;

    private static T Resource<T>(string key) => (T)Microsoft.UI.Xaml.Application.Current.Resources[key];

    private void Say(InfoBarSeverity severity, string message)
    {
        _notice.Severity = severity;
        _notice.Message = message;
        _notice.IsOpen = true;
    }

    private void Paint()
    {
        var path = _settings.CompanyPath;
        _name.Text = _company?.Name ?? (path != null ? System.IO.Path.GetFileNameWithoutExtension(path) : "No company chosen");
        _path.Text = path ?? "Choose the company file your office uses (.osas).";
        _who.Text = _company != null ? $"Signed in as {_company.User} ({_company.Role})" : path != null ? "Not signed in" : "";
        _signIn.Visibility = _company == null && path != null ? Visibility.Visible : Visibility.Collapsed;
        _signOut.Visibility = _company != null ? Visibility.Visible : Visibility.Collapsed;
        _details.Visibility = _company != null ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(_name, $"Company: {_name.Text}. {_who.Text}");
    }

    private async Task Choose()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".osas");
        picker.FileTypeFilter.Add(".adms");
        picker.FileTypeFilter.Add(".aorms");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(_window));
        var file = await picker.PickSingleFileAsync();
        if (file == null)
            return;
        SignOut(announce: false);
        _settings.CompanyPath = file.Path;
        _notice.IsOpen = false;
        Paint();
        Changed?.Invoke();
        await SignIn();
    }

    private async Task SignIn()
    {
        var path = _settings.CompanyPath;
        if (path == null || !Engine.IsAvailable())
        {
            Say(InfoBarSeverity.Warning, path == null ? "Choose a company file first." : "The company-file engine is missing beside O-Studio, so it cannot sign in here. The other apps can still be opened and sign in themselves.");
            return;
        }
        var user = new TextBox { Header = "User name", Text = _settings.LastUser ?? "owner" };
        var password = new PasswordBox { Header = "Password" };
        var filePassword = new PasswordBox { Header = "File password (only if the company file is protected)" };
        var panel = new StackPanel { Spacing = 10, MinWidth = 360 };
        panel.Children.Add(new TextBlock { Text = System.IO.Path.GetFileName(path), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(user);
        panel.Children.Add(password);
        panel.Children.Add(filePassword);
        var dialog = new ContentDialog { Title = "Sign in to the company", Content = panel, PrimaryButtonText = "Sign in", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = _window.Content.XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        var name = user.Text.Trim();
        var secret = password.Password;
        var file = filePassword.Password;
        try
        {
            var company = await Task.Run(() => SuiteCompany.Open(EngineFolder, path, name, secret, file));
            _company?.Dispose();
            _company = company;
            _settings.LastUser = company.User;
            _notice.IsOpen = false;
            _ = LoadUsers();
        }
        catch (EngineException ex)
        {
            Say(InfoBarSeverity.Error, ex.Message);
        }
        Paint();
        Changed?.Invoke();
    }

    public void SignOut(bool announce = true)
    {
        _company?.Dispose();
        _company = null;
        ClearUsers();
        Paint();
        Changed?.Invoke();
        if (announce)
            Say(InfoBarSeverity.Informational, "Signed out. Apps already open stay signed in until you close them.");
    }

    /// <summary>
    /// What an app is started with. Signed in: the file and a one-time ticket, so the app opens already signed in. Not signed in: the file only,
    /// and the app asks for the sign-in itself. No company chosen: nothing.
    /// </summary>
    public IReadOnlyDictionary<string, string> EnvironmentFor(string app)
    {
        var path = _settings.CompanyPath;
        if (path == null)
            return new Dictionary<string, string>();
        AppTicket? ticket = null;
        if (_company != null)
        {
            try
            {
                ticket = _company.TicketFor(app);
            }
            catch (EngineException ex)
            {
                // Typically the session locked after sitting idle. The app is still opened on the file and asks for the sign-in.
                Say(InfoBarSeverity.Warning, $"{app} will ask you to sign in: {ex.Message}");
            }
        }
        return SuiteSettings.AppEnvironment(path, ticket);
    }

    private async Task EditDetails()
    {
        if (_company == null)
            return;
        CompanyDetails current;
        try
        {
            current = await Task.Run(_company.Details);
        }
        catch (EngineException ex)
        {
            Say(InfoBarSeverity.Error, ex.Message);
            return;
        }
        TextBox Field(string header, string value, bool tall = false) => new() { Header = header, Text = value, AcceptsReturn = tall, MinHeight = tall ? 72 : 0, TextWrapping = tall ? TextWrapping.Wrap : TextWrapping.NoWrap };
        ComboBox Choice(string header, string value, params string[] options)
        {
            var box = new ComboBox { Header = header, HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var option in options)
                box.Items.Add(option);
            box.SelectedItem = options.Contains(value) ? value : options[0];
            return box;
        }
        var name = Field("Company name", current.Name);
        var architect = Field("Architect's name", current.ArchitectName);
        var address = Field("Address", current.Address, tall: true);
        var email = Field("Email", current.Email);
        var phone = Field("Phone", current.Phone);
        var pan = Field("PAN", current.Pan);
        var gstin = Field("GSTIN", current.Gstin);
        var coa = Field("Council of Architecture registration", current.CoaRegistration);
        var bank = Field("Bank details", current.BankDetails, tall: true);
        var upi = Field("UPI id", current.UpiId);
        var gst = Choice("GST scheme", current.GstScheme, "none", "composition", "regular");
        var letterhead = Choice("Letterhead style", current.LetterheadStyle, "classic", "minimal");
        var accent = Field("Document accent colour (such as #3E5240, or blank)", current.AccentHex);
        var panel = new StackPanel { Spacing = 10, MinWidth = 440 };
        foreach (var control in new UIElement[] { name, architect, address, email, phone, pan, gstin, coa, bank, upi, gst, letterhead, accent })
            panel.Children.Add(control);
        var dialog = new ContentDialog
        {
            Title = "Company details",
            Content = new ScrollViewer { Content = panel, MaxHeight = 520, Padding = new Thickness(0, 0, 12, 0) },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = _window.Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        var edited = new CompanyDetails(name.Text, address.Text, email.Text, phone.Text, pan.Text, gstin.Text, coa.Text, bank.Text,
            (string)gst.SelectedItem, upi.Text, architect.Text, (string)letterhead.SelectedItem, accent.Text);
        try
        {
            await Task.Run(() => _company.Save(edited));
            Say(InfoBarSeverity.Success, "Saved. Every app shows these details.");
        }
        catch (EngineException ex)
        {
            Say(InfoBarSeverity.Error, ex.Message);
        }
        Paint();
    }
}
