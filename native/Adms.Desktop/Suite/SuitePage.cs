using System.Diagnostics;
using Hcw.Ui.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Osas.Core;
using Osas.Core.Suite;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Adms.Desktop;

/// <summary>
/// What OSAS used to do as a separate program, now a page of O-Studio: the company and its users, the company file monitor, ShilpiDB, the
/// AI assistant, and opening the other apps of the suite (O-CoreOp, O-Draft).
/// </summary>
internal sealed class SuitePage : UserControl
{
    private sealed record Look(string Mark, string Fill, string Hover, string Pressed, string On);

    private static readonly Dictionary<Osas.Core.SuiteApp, Look> Looks = new()
    {
        [Osas.Core.SuiteApp.Metra] = new("mark-metra.png", "#1F5FD1", "#1A52B8", "#15449A", "#FFFFFF"),
        [Osas.Core.SuiteApp.Draft] = new("mark-draft.png", "#1B1C20", "#34363C", "#4B4E56", "#FFFFFF"),
    };

    private readonly Window _window;
    private readonly SavedPaths _saved = new(SavedPaths.DefaultFile);
    private readonly FlexBar _cards = new();
    private readonly CompanyPanel _company;
    private readonly ShilpiPanel _shilpi;
    private readonly InfoBar _notice = new() { IsOpen = false, IsClosable = true };

    public SuitePage(Window window)
    {
        _window = window;
        var page = new StackPanel { Padding = new Thickness(32, 24, 32, 24), Spacing = 24 };
        var title = new TextBlock { Text = "Suite", Style = Style("PageTitleStyle") };
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);
        page.Children.Add(title);
        page.Children.Add(_notice);

        var settings = new SuiteSettings();
        _company = new CompanyPanel(window, settings);
        var monitor = new MonitorPanel(window);
        _company.Changed += () => monitor.Follow(_company.CompanyPath);
        monitor.Follow(_company.CompanyPath);
        _shilpi = new ShilpiPanel(settings, () => _company.CompanyPath);
        var agent = new AgentPanel(settings, () => _company.Company, () => _company.CompanyPath);
        _company.Changed += agent.Refresh;

        page.Children.Add(_company);
        var apps = new TextBlock { Text = "Other apps", Style = Style("SectionTitleStyle") };
        AutomationProperties.SetHeadingLevel(apps, AutomationHeadingLevel.Level2);
        page.Children.Add(apps);
        page.Children.Add(_cards);
        page.Children.Add(monitor);
        page.Children.Add(_shilpi);
        page.Children.Add(agent);
        Content = new ScrollViewer { Content = page };
        Unloaded += (_, _) => _shilpi.Shutdown();
        Refresh();
    }

    private static Style Style(string key) => (Style)Application.Current.Resources[key];

    private static BitmapImage Asset(string name) => new(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", name)));

    private static SolidColorBrush Colour(string hex) => new(Windows.UI.Color.FromArgb(255, Convert.ToByte(hex[1..3], 16), Convert.ToByte(hex[3..5], 16), Convert.ToByte(hex[5..7], 16)));

    private void Say(InfoBarSeverity severity, string message)
    {
        _notice.Severity = severity;
        _notice.Message = message;
        _notice.IsOpen = true;
    }

    private void Refresh()
    {
        _cards.Children.Clear();
        foreach (var app in new AppCatalog(new WindowsProbe(_saved)).Detect())
            if (Looks.ContainsKey(app.App))
                _cards.Children.Add(Card(app));
    }

    private UIElement Card(AppInfo app)
    {
        var look = Looks[app.App];
        var status = app.Installed
            ? $"Installed{(app.Version is { Length: > 0 } v ? $" · version {v}" : "")}"
            : "Not installed on this computer";
        var stack = new StackPanel { Spacing = 10 };

        var top = new Grid { ColumnSpacing = 14 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var mark = new Image { Width = 56, Height = 56, Source = Asset(look.Mark) };
        AutomationProperties.SetAccessibilityView(mark, AccessibilityView.Raw);
        top.Children.Add(mark);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock { Text = app.Name, Style = Style("SectionTitleStyle"), Margin = new Thickness(0) });
        names.Children.Add(new TextBlock { Text = app.Purpose, Style = Style("SecondaryStyle") });
        Grid.SetColumn(names, 1);
        top.Children.Add(names);
        stack.Children.Add(top);

        stack.Children.Add(new TextBlock { Text = status, Style = Style("BodyStyle") });
        if (app.Installed)
        {
            stack.Children.Add(new TextBlock { Text = $"Found {app.Source}", Style = Style("CaptionStyle") });
            stack.Children.Add(new TextBlock { Text = app.ExePath, Style = Style("CaptionStyle"), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        }

        var open = new Button { Content = "Open", Style = Style("AccentButtonStyle"), IsEnabled = app.Installed, MinWidth = 96 };
        open.Background = Colour(look.Fill);
        open.Foreground = Colour(look.On);
        open.BorderBrush = Colour(look.Pressed);
        open.Resources["AccentFillColorSecondaryBrush"] = Colour(look.Hover);
        open.Resources["AccentFillColorTertiaryBrush"] = Colour(look.Pressed);
        open.Resources["TextOnAccentFillColorPrimaryBrush"] = Colour(look.On);
        AutomationProperties.SetName(open, $"Open {app.Name}");
        open.Click += (_, _) => Start(app);
        var locate = new Button { Content = app.Installed ? "Choose another copy…" : "Find it…" };
        AutomationProperties.SetName(locate, $"{(app.Installed ? "Choose another copy of" : "Find")} {app.Name}");
        locate.Click += async (_, _) => await Locate(app);
        stack.Children.Add(new FlexBar { Children = { open, locate } });
        if (!app.Installed)
            stack.Children.Add(new TextBlock { Text = $"Install {app.Name} with its own setup, or choose its {app.ExeName} with Find it.", Style = Style("CaptionStyle"), TextWrapping = TextWrapping.Wrap });

        var card = new Border { Style = Style("SuiteCardStyle"), Width = 340, Margin = new Thickness(0, 0, 16, 16), Child = stack };
        AutomationProperties.SetName(card, $"{app.Name}. {app.Purpose}. {status}");
        return card;
    }

    private void Start(AppInfo app)
    {
        try
        {
            // Started on the suite's company file and, when someone is signed in, with a one-time ticket that signs the app in as the same
            // person. Environment variables, not the command line, because other programs can read a command line.
            var info = new ProcessStartInfo(app.ExePath!) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(app.ExePath)! };
            foreach (var (key, value) in _company.EnvironmentFor(app.Name))
                info.Environment[key] = value;
            Process.Start(info);
            Say(InfoBarSeverity.Success, _company.SignedIn ? $"{app.Name} is starting, signed in as you." : $"{app.Name} is starting.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            Say(InfoBarSeverity.Error, $"{app.Name} could not be started: {ex.Message}");
        }
    }

    private async Task Locate(AppInfo app)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add(".exe");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(_window));
        var file = await picker.PickSingleFileAsync();
        if (file == null)
            return;
        if (!_saved.Save(app.App, file.Path))
        {
            Say(InfoBarSeverity.Error, "Choose the program file of the app (a file ending in .exe).");
            return;
        }
        Refresh();
        Say(InfoBarSeverity.Success, $"{app.Name} will be opened from {file.Path}.");
    }
}
