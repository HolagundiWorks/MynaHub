using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Osas.Core;
using Osas.Core.Suite;

namespace Adms.LoginWidget;

/// <summary>
/// The launcher window is laid out as a phone shell: an app bar on top (who, and two status dots), one page at a time in the middle (Home, Services,
/// Account) and a bar of three tabs at the bottom. Before sign-in only the app bar and the sign-in pages show.
/// </summary>
internal sealed partial class CompanyLoginWidget
{
    const double Touch = 48;

    readonly TextBlock _barTitle = new() { Text = "O-Studio suite", Style = (Style)Application.Current.Resources["SectionTitleStyle"], TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _barSub = new() { Style = (Style)Application.Current.Resources["CaptionStyle"], TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0.8 };
    readonly Microsoft.UI.Xaml.Shapes.Ellipse _barAi = new() { Width = 10, Height = 10 };
    readonly Microsoft.UI.Xaml.Shapes.Ellipse _barDb = new() { Width = 10, Height = 10 };

    readonly StackPanel _pageHome = new() { Spacing = 14 };
    readonly StackPanel _pageServices = new() { Spacing = 10, Visibility = Visibility.Collapsed };
    readonly StackPanel _pageAccount = new() { Spacing = 10, Visibility = Visibility.Collapsed };
    readonly StackPanel _projectLoadedBox = new() { Spacing = 8, Visibility = Visibility.Collapsed };
    readonly StackPanel _projectPickBox = new() { Spacing = 8 };
    readonly TextBlock _projectTitle = new() { Style = (Style)Application.Current.Resources["SectionTitleStyle"], TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _projectMeta = new() { Style = (Style)Application.Current.Resources["CaptionStyle"], TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    readonly TextBlock _avatar = new() { FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly Grid _nav = new() { Visibility = Visibility.Collapsed };
    readonly List<(Button Button, TextBlock Label, FontIcon Icon, UIElement Page)> _tabs = [];
    int _tab;

    static Brush AccentBrush => new SolidColorBrush((Windows.UI.Color)Application.Current.Resources["SuiteAccent"]);

    static Brush CardBrush => (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];

    static Border Card(UIElement child) => new()
    {
        Background = CardBrush,
        BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(14),
        Child = child,
    };

    static void Big(Button b)
    {
        b.MinHeight = Touch;
        b.HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    FrameworkElement BuildAppBar()
    {
        var mark = Path.Combine(AppContext.BaseDirectory, "Assets", "mark.png");
        FrameworkElement logo = File.Exists(mark)
            ? new Image { Source = new BitmapImage(new Uri(mark)), Width = 32, Height = 32 }
            : new Border { Width = 32, Height = 32 };
        var titles = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(_barTitle);
        titles.Children.Add(_barSub);
        var dots = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        dots.Children.Add(_barAi);
        dots.Children.Add(_barDb);
        ToolTipService.SetToolTip(dots, "AI agent and database server. Tap for details.");
        dots.Tapped += (_, _) =>
        {
            if (_signedInPanel.Visibility == Visibility.Visible)
                ShowTab(1);
        };
        var bar = new Grid { ColumnSpacing = 12, Padding = new Thickness(16, 12, 16, 12) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.Children.Add(logo);
        Grid.SetColumn(titles, 1);
        bar.Children.Add(titles);
        Grid.SetColumn(dots, 2);
        bar.Children.Add(dots);
        return new Border
        {
            Child = bar,
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    void SetBar(string title, string sub)
    {
        _barTitle.Text = title;
        _barSub.Text = sub;
        _barSub.Visibility = string.IsNullOrWhiteSpace(sub) ? Visibility.Collapsed : Visibility.Visible;
    }

    void ColourDots(ServiceLevel ai, ServiceLevel db, string aiText, string dbText)
    {
        _barAi.Fill = StatusRow.Colour(ai);
        _barDb.Fill = StatusRow.Colour(db);
        AutomationProperties.SetName(_barAi, $"AI agent: {ServiceStatus.Word(ai)}");
        AutomationProperties.SetName(_barDb, $"Database server: {ServiceStatus.Word(db)}");
        ToolTipService.SetToolTip(_barAi, $"AI agent: {aiText}");
        ToolTipService.SetToolTip(_barDb, $"Database server: {dbText}");
    }

    FrameworkElement BuildNavBar()
    {
        var specs = new (string Name, string Glyph, UIElement Page)[]
        {
            ("Home", "", _pageHome),
            ("Services", "", _pageServices),
            ("Account", "", _pageAccount),
        };
        for (var i = 0; i < specs.Length; i++)
        {
            var (name, glyph, page) = specs[i];
            var icon = new FontIcon { Glyph = glyph, FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center };
            var label = new TextBlock { Text = name, Style = (Style)Application.Current.Resources["CaptionStyle"], HorizontalAlignment = HorizontalAlignment.Center };
            var stack = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(icon);
            stack.Children.Add(label);
            var button = new Button
            {
                Content = stack,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                MinHeight = 60,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
            };
            AutomationProperties.SetName(button, name);
            var index = i;
            button.Click += (_, _) => ShowTab(index);
            _nav.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(button, i);
            _nav.Children.Add(button);
            _tabs.Add((button, label, icon, page));
        }

        return new Border
        {
            Child = _nav,
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(0, 1, 0, 0),
        };
    }

    void ShowTab(int index)
    {
        _tab = index;
        for (var i = 0; i < _tabs.Count; i++)
        {
            var on = i == index;
            _tabs[i].Page.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            Brush? ink = on ? AccentBrush : null;
            if (ink != null)
            {
                _tabs[i].Icon.Foreground = ink;
                _tabs[i].Label.Foreground = ink;
            }
            else
            {
                _tabs[i].Icon.ClearValue(IconElement.ForegroundProperty);
                _tabs[i].Label.ClearValue(TextBlock.ForegroundProperty);
            }

            _tabs[i].Label.FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }
    }

    // ---- Home: the project, then the apps
    void BuildHomePage()
    {
        _projectFilter.TextChanged += (_, _) => ShowProjects();
        _projectLock.Click += async (_, _) => await LockSelectedProjectAsync();
        _projectRelease.Click += (_, _) => ReleaseProject();
        _projectNew.Click += async (_, _) => await NewProjectAsync();
        AutomationProperties.SetName(_projectFilter, "Project");
        AutomationProperties.SetName(_projectBox, "Project list");
        _projectBox.MinHeight = 40;
        _projectBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        Big(_projectLock);
        Big(_projectRelease);
        Big(_projectNew);

        _projectPickBox.Children.Add(new TextBlock { Text = "Project", Style = (Style)Application.Current.Resources["SectionTitleStyle"] });
        _projectPickBox.Children.Add(_projectState);
        _projectPickBox.Children.Add(_projectFilter);
        _projectPickBox.Children.Add(_projectBox);
        var actions = new Grid { ColumnSpacing = 8 };
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        actions.Children.Add(_projectLock);
        Grid.SetColumn(_projectNew, 1);
        actions.Children.Add(_projectNew);
        _projectPickBox.Children.Add(actions);

        _projectLoadedBox.Children.Add(new TextBlock { Text = "Current project", Style = (Style)Application.Current.Resources["CaptionStyle"], Opacity = 0.8 });
        _projectLoadedBox.Children.Add(_projectTitle);
        _projectLoadedBox.Children.Add(_projectMeta);
        _projectLoadedBox.Children.Add(_projectRelease);

        var inside = new StackPanel { Spacing = 0 };
        inside.Children.Add(_projectPickBox);
        inside.Children.Add(_projectLoadedBox);
        _pageHome.Children.Add(Card(inside));
        _pageHome.Children.Add(new TextBlock { Text = "Apps", Style = (Style)Application.Current.Resources["SectionTitleStyle"] });
        _pageHome.Children.Add(BuildAppTiles());
    }

    // ---- Services: the AI agent, the database server and the company file
    void BuildServicesPage()
    {
        _pageServices.Children.Add(new TextBlock { Text = "Services", Style = (Style)Application.Current.Resources["SectionTitleStyle"] });
        _pageServices.Children.Add(new TextBlock
        {
            Text = "These run in the launcher for as long as it is open.",
            Style = (Style)Application.Current.Resources["CaptionStyle"],
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap,
        });
        _pageServices.Children.Add(Card(_aiRow.Root));
        _pageServices.Children.Add(Card(_dbRow.Root));
        _pageServices.Children.Add(Card(_fileRow.Root));
        _pageServices.Children.Add(_suiteStatus);
    }

    // ---- Account: who is signed in, and the session
    void BuildAccountPage()
    {
        var avatar = new Border
        {
            Width = 56,
            Height = 56,
            CornerRadius = new CornerRadius(28),
            Background = AccentBrush,
            Child = _avatar,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _avatar.Foreground = new SolidColorBrush((Windows.UI.Color)Application.Current.Resources["SuiteOnAccent"]);
        _signedInCompany.TextWrapping = TextWrapping.Wrap;
        var who = new StackPanel { Spacing = 4 };
        who.Children.Add(avatar);
        who.Children.Add(_signedInSummary);
        who.Children.Add(_signedInCompany);
        who.Children.Add(_signedInPath);
        _pageAccount.Children.Add(Card(who));

        var lockBtn = new Button { Content = "Lock session" };
        lockBtn.Click += async (_, _) => await LockAsync();
        var logout = new Button { Content = "Sign out" };
        logout.Click += async (_, _) => await LogOutAsync();
        var close = new Button { Content = "Close company" };
        close.Click += (_, _) => _ = CloseCompanyAsync();
        foreach (var b in new[] { lockBtn, logout, close })
        {
            Big(b);
            _pageAccount.Children.Add(b);
        }

        _pageAccount.Children.Add(new TextBlock
        {
            Text = "Users, roles and company settings are managed in O-Studio.",
            Style = (Style)Application.Current.Resources["CaptionStyle"],
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
        });
    }
}
