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

namespace Osas.Launcher;

/// <summary>
/// OSAS, the Open Source Architecture Suit: one window that shows which of O-Studio, O-CoreOp and O-Draft are installed on this
/// computer and opens them. It starts the apps and changes nothing in them.
/// </summary>
public sealed class MainWindow : Window
{
    private sealed record Look(string Mark, string Fill, string Hover, string Pressed, string On);

    private static readonly Dictionary<SuiteApp, Look> Looks = new()
    {
        [SuiteApp.Studio] = new("mark-studio.png", "#FF6B00", "#F26400", "#E65F00", "#17181B"),
        [SuiteApp.Metra] = new("mark-metra.png", "#1F5FD1", "#1A52B8", "#15449A", "#FFFFFF"),
        [SuiteApp.Draft] = new("mark-draft.png", "#1B1C20", "#34363C", "#4B4E56", "#FFFFFF"),
    };

    private readonly SavedPaths _saved = new(SavedPaths.DefaultFile);
    private readonly FlexBar _cards = new();
    private readonly CompanyPanel _company;
    private readonly InfoBar _notice = new() { IsOpen = false, IsClosable = true, Margin = new Thickness(32, 0, 32, 8) };

    public MainWindow()
    {
        Title = "OSAS — Open Source Architecture Suit";
        var root = new Grid { Background = Brush("AppBackgroundBrush") };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { Margin = new Thickness(32, 28, 32, 12), ColumnSpacing = 16 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var mark = new Image { Width = 64, Height = 64, Source = Asset("mark-osas.png") };
        AutomationProperties.SetAccessibilityView(mark, AccessibilityView.Raw);
        header.Children.Add(mark);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        var title = new TextBlock { Text = "OSAS", Style = Style("PageTitleStyle"), FontSize = 28 };
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);
        titles.Children.Add(title);
        titles.Children.Add(new TextBlock { Text = "Open Source Architecture Suit", Style = Style("SecondaryStyle") });
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);
        var about = new Button { Content = "About", VerticalAlignment = VerticalAlignment.Center };
        about.Click += async (_, _) => await About();
        Grid.SetColumn(about, 2);
        header.Children.Add(about);
        root.Children.Add(header);

        var intro = new TextBlock
        {
            Text = "Open an app. Each one keeps its own window. O-Studio and O-CoreOp can have the same company file open at the same time, and they share its clients and projects.",
            Style = Style("SecondaryStyle"),
            Margin = new Thickness(32, 0, 32, 12),
            MaxWidth = 760,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var introRow = new StackPanel();
        introRow.Children.Add(intro);
        introRow.Children.Add(_notice);
        Grid.SetRow(introRow, 1);
        root.Children.Add(introRow);

        var page = new StackPanel { Padding = new Thickness(32, 8, 32, 24), Spacing = 24 };
        var settings = new SuiteSettings();
        _company = new CompanyPanel(this, settings);
        var monitor = new MonitorPanel(this);
        _company.Changed += () => monitor.Follow(_company.CompanyPath);
        monitor.Follow(_company.CompanyPath);
        page.Children.Add(_company);
        page.Children.Add(_cards);
        page.Children.Add(monitor);
        var shilpi = new ShilpiPanel(settings, () => _company.CompanyPath);
        var agent = new AgentPanel(settings, () => _company.CompanyPath, () => "auditor");
        _company.Changed += agent.Refresh;
        page.Children.Add(shilpi);
        page.Children.Add(agent);
        Closed += (_, _) => shilpi.Shutdown();
        var scroll = new ScrollViewer { Content = page };
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);

        var footer = new TextBlock { Text = "Human Centric Works", Style = Style("CaptionStyle"), Margin = new Thickness(32, 0, 32, 12) };
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        Content = root;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 640));
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(icon))
            AppWindow.SetIcon(icon);
        Refresh();
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

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
        var name = new TextBlock { Text = app.Name, Style = Style("SectionTitleStyle"), Margin = new Thickness(0) };
        names.Children.Add(name);
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
            // The app is started on the suite's company file and, when someone is signed in here, with a one-time ticket that signs it in
            // as the same person. Environment variables, not the command line, because other programs can read a command line.
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
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
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

    private async Task About()
    {
        var text = new StackPanel { Spacing = 8, MaxWidth = 440 };
        text.Children.Add(new TextBlock { Text = "OSAS is the Open Source Architecture Suit: three apps for an architecture practice.", TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = "O-Studio — office and project management", TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = "O-CoreOp — quantities, estimates and billing", TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = "O-Draft — drafting", TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = "Developed by Human Centric Works. This launcher is version 0.3.0.", Style = Style("CaptionStyle"), TextWrapping = TextWrapping.Wrap });
        await new ContentDialog { Title = "About OSAS", Content = text, CloseButtonText = "Close", XamlRoot = Content.XamlRoot }.ShowAsync();
    }
}
