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
    readonly StatusIcon _barAi = new("\uF4A5", 36);
    readonly StatusIcon _barDb = new("\uEE94", 36);

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

    /// <summary>A button that is only an icon; its name and tooltip say what it does.</summary>
    static void IconOnly(Button b, string glyph, string name, bool accent = false)
    {
        b.Content = new FontIcon { Glyph = glyph, FontSize = 20 };
        b.MinHeight = Touch;
        b.MinWidth = Touch;
        if (accent)
            b.Style = Accent;
        AutomationProperties.SetName(b, name);
        ToolTipService.SetToolTip(b, name);
    }

    /// <summary>
    /// The symbol of a service: lit in the suite colours while the service runs, amber while it waits, grey and dim when it is not running or off.
    /// </summary>
    sealed class StatusIcon
    {
        readonly FontIcon _icon;
        readonly Border _badge;

        internal StatusIcon(string glyph, double size)
        {
            _icon = new FontIcon { Glyph = glyph, FontSize = size * 0.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            _badge = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Child = _icon, VerticalAlignment = VerticalAlignment.Center };
            Set(ServiceLevel.Off, "checking…");
        }

        internal FrameworkElement Root => _badge;

        internal void Set(ServiceLevel level, string tip)
        {
            static Windows.UI.Color Res(string key) => (Windows.UI.Color)Application.Current.Resources[key];
            switch (level)
            {
                case ServiceLevel.Running:
                    _badge.Background = new SolidColorBrush(Res("SuiteAccentWash"));
                    _icon.Foreground = new SolidColorBrush(Res("SuiteAccentText"));
                    _icon.Opacity = 1;
                    break;
                case ServiceLevel.Waiting:
                    _badge.Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0xFF, 0xF1, 0xD0));
                    _icon.Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x9A, 0x67, 0x00));
                    _icon.Opacity = 1;
                    break;
                default:
                    _badge.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                    _icon.Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x8B, 0x94, 0x9E));
                    _icon.Opacity = 0.55;
                    break;
            }

            AutomationProperties.SetName(_badge, tip);
            ToolTipService.SetToolTip(_badge, tip);
        }
    }

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
        var dots = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        dots.Children.Add(_barAi.Root);
        dots.Children.Add(_barDb.Root);
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
        _barAi.Set(ai, $"AI agent: {ServiceStatus.Word(ai)} ({aiText})");
        _barDb.Set(db, $"Database server: {ServiceStatus.Word(db)} ({dbText})");
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
        IconOnly(_projectLock, "\uE73E", "Load project", accent: true);
        IconOnly(_projectNew, "\uE710", "New project…");
        IconOnly(_projectRelease, "\uE711", "Close project");

        _projectPickBox.Children.Add(new TextBlock { Text = "Project", Style = (Style)Application.Current.Resources["SectionTitleStyle"] });
        _projectPickBox.Children.Add(_projectState);
        _projectPickBox.Children.Add(_projectFilter);
        _projectPickBox.Children.Add(_projectBox);
        var actions = new Grid { ColumnSpacing = 8 };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _projectLock.HorizontalAlignment = HorizontalAlignment.Stretch;
        actions.Children.Add(_projectLock);
        Grid.SetColumn(_projectNew, 1);
        actions.Children.Add(_projectNew);
        _projectPickBox.Children.Add(actions);

        var loadedText = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        loadedText.Children.Add(new TextBlock { Text = "Current project", Style = (Style)Application.Current.Resources["CaptionStyle"], Opacity = 0.8 });
        loadedText.Children.Add(_projectTitle);
        loadedText.Children.Add(_projectMeta);
        var loadedRow = new Grid { ColumnSpacing = 8 };
        loadedRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        loadedRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        loadedRow.Children.Add(loadedText);
        _projectRelease.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_projectRelease, 1);
        loadedRow.Children.Add(_projectRelease);
        _projectLoadedBox.Children.Add(loadedRow);

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

        var lockBtn = new Button();
        lockBtn.Click += async (_, _) => await LockAsync();
        var logout = new Button();
        logout.Click += async (_, _) => await LogOutAsync();
        var close = new Button();
        close.Click += (_, _) => _ = CloseCompanyAsync();
        IconOnly(lockBtn, "\uE72E", "Lock session");
        IconOnly(logout, "\uE7E8", "Sign out");
        IconOnly(close, "\uE72B", "Close company");
        var row = new Grid { ColumnSpacing = 8 };
        var buttons = new[] { lockBtn, logout, close };
        for (var i = 0; i < buttons.Length; i++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition());
            buttons[i].HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetColumn(buttons[i], i);
            row.Children.Add(buttons[i]);
        }

        _pageAccount.Children.Add(row);

        _pageAccount.Children.Add(new TextBlock
        {
            Text = "Users, roles and company settings are managed in O-Studio.",
            Style = (Style)Application.Current.Resources["CaptionStyle"],
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
        });
    }
}
