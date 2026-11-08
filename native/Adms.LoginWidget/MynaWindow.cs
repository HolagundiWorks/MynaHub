using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using O.Ui.Widgets;
using Osas.Core;
using Osas.Core.Suite;

namespace Adms.LoginWidget;

/// <summary>
/// The launcher window. It shows the two services the launcher hosts (the AI Core and the database service), offers Kivo's sign-in when the database is
/// signed out, and starts the suite apps. It asks for no user name or password: the company sign-in is Kivo's.
/// </summary>
internal sealed class MynaWindow : WidgetWindow
{
    static MynaWindow? _open;

    readonly InfoBar _notice = new() { IsOpen = false, IsClosable = true };
    readonly StatusIcon _aiIcon = StatusIcon.Orb(42);
    readonly StatusIcon _dbIcon = new("", 36);
    readonly TextBlock _aiText = new() { TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _dbText = new() { TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _dbDetail = new() { Style = (Style)Application.Current.Resources["CaptionStyle"], TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    readonly Button _signIn = new() { Content = "Open Kivo to sign in", HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 48, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };

    public MynaWindow() : base("Myna")
    {
        void Show() => DispatcherQueue.TryEnqueue(Apply);
        SuiteStatus.Instance.Changed += Show;
        Closed += (_, _) =>
        {
            SuiteStatus.Instance.Changed -= Show;
            if (ReferenceEquals(_open, this))
                _open = null;
        };

        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "myna.ico");
        if (File.Exists(icon))
            AppWindow.SetIcon(icon);

        _signIn.Click += (_, _) => Run(SuiteLauncherHost.Instance.OpenKivoToSignIn);
        AttachChrome(BuildChrome());
        AppWindow.Resize(new Windows.Graphics.SizeInt32(400, 640));
        Apply();
    }

    public static void ShowOrFocus()
    {
        if (_open != null)
        {
            _open.Activate();
            return;
        }

        _open = new MynaWindow();
        // A WinUI window made in code stays hidden until it is activated.
        _open.Activate();
    }

    internal static void ShowError(string message)
    {
        if (_open == null)
            return;
        _open._notice.Severity = InfoBarSeverity.Error;
        _open._notice.Message = message;
        _open._notice.IsOpen = true;
    }

    void Run(Action action)
    {
        try
        {
            _notice.IsOpen = false;
            action();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    void Apply()
    {
        var s = SuiteStatus.Instance;
        var ai = ServiceStatus.ForAi(s.Ai);
        var db = ServiceStatus.ForDatabase(s.Database);
        _aiIcon.Set(ai, $"AI: {SuiteLauncherHost.Word(s.Ai)} ({s.Ai})");
        _dbIcon.Set(db, $"Database: {s.Database}");
        _aiText.Text = "AI: " + SuiteLauncherHost.Word(s.Ai);
        _dbText.Text = "Database: " + s.Database;
        _dbDetail.Text = s.DatabaseDetail;
        _dbDetail.Visibility = s.DatabaseDetail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _signIn.Visibility = string.Equals(s.Database, "signed out", StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
    }

    FrameworkElement BuildChrome()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var bar = BuildAppBar();
        root.Children.Add(bar);
        Grid.SetRow(bar, 0);
        root.Children.Add(_notice);
        Grid.SetRow(_notice, 1);

        var body = new StackPanel { Spacing = 14, Margin = new Thickness(16, 16, 16, 12) };
        body.Children.Add(ServiceCard(_aiIcon, _aiText, null));
        body.Children.Add(ServiceCard(_dbIcon, _dbText, _dbDetail));
        body.Children.Add(_signIn);
        body.Children.Add(new TextBlock { Text = "Apps", Style = (Style)Application.Current.Resources["SectionTitleStyle"], FontFamily = O.Ui.SuiteFonts.Display, Margin = new Thickness(0, 6, 0, 0) });
        body.Children.Add(BuildAppTiles());
        body.Children.Add(new TextBlock
        {
            Text = "Sign in once in Kivo. Tren and Falcon ask Kivo for the database when you connect a project.",
            Style = (Style)Application.Current.Resources["CaptionStyle"],
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
        });
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = body,
        };
        root.Children.Add(scroll);
        Grid.SetRow(scroll, 2);

        return new Border
        {
            Background = (Brush)Application.Current.Resources["SurfaceBackgroundBrush"],
            CornerRadius = new CornerRadius(8),
            Child = root,
        };
    }

    FrameworkElement BuildAppBar()
    {
        var mark = Path.Combine(AppContext.BaseDirectory, "Assets", "mark.png");
        FrameworkElement logo = File.Exists(mark)
            ? new Image { Source = new BitmapImage(new Uri(mark)), Width = 32, Height = 32 }
            : new Border { Width = 32, Height = 32 };
        var title = new TextBlock { Text = "Myna", Style = (Style)Application.Current.Resources["SectionTitleStyle"], FontFamily = O.Ui.SuiteFonts.Names };
        var sub = new TextBlock { Text = "AI and database for the suite", Style = (Style)Application.Current.Resources["CaptionStyle"], Opacity = 0.8 };
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { title, sub } };
        var bar = new Grid { ColumnSpacing = 12, Padding = new Thickness(16, 12, 16, 12) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.Children.Add(logo);
        Grid.SetColumn(titles, 1);
        bar.Children.Add(titles);
        return new Border
        {
            Child = bar,
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    static Border ServiceCard(StatusIcon icon, TextBlock title, TextBlock? detail)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        text.Children.Add(title);
        if (detail != null)
            text.Children.Add(detail);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(icon.Root);
        row.Children.Add(text);
        return new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Child = row,
        };
    }

    /// <summary>The apps of the suite, each with its mark. Finch is a placeholder for the app that is planned.</summary>
    FrameworkElement BuildAppTiles()
    {
        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var specs = new (string Title, string Blurb, string Mark)[]
        {
            ("Kivo", "Company, estimates, bills", "mark-studio.png"),
            ("Falcon", "Estimates and bills", "mark-coreop.png"),
            ("Tren", "2D drafting", "mark-draft.png"),
            ("Finch", "3D / BIM form (planned)", "mark-form.png"),
        };
        for (var i = 0; i < specs.Length; i++)
        {
            var (title, blurb, mark) = specs[i];
            var tile = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 10, 12, 10),
                MinHeight = 72,
                Content = TileContent(title, blurb, mark),
            };
            AutomationProperties.SetName(tile, title);
            var app = SuiteCatalog.Apps.FirstOrDefault(a => string.Equals(a.Title, title, StringComparison.Ordinal));
            if (title == "Finch" || app == null)
            {
                tile.IsEnabled = false;
                ToolTipService.SetToolTip(tile, "Finch is planned and not built yet.");
            }
            else
            {
                tile.Click += (_, _) => Run(() => SuiteLauncherHost.Instance.LaunchApp(app));
            }

            Grid.SetColumn(tile, i % 2);
            Grid.SetRow(tile, i / 2);
            grid.Children.Add(tile);
        }

        return grid;
    }

    static FrameworkElement TileContent(string title, string blurb, string mark)
    {
        FrameworkElement icon;
        var file = Path.Combine(AppContext.BaseDirectory, "Assets", mark);
        if (File.Exists(file))
        {
            icon = new Image { Source = new BitmapImage(new Uri(file)), Width = 40, Height = 40, Stretch = Stretch.Uniform };
        }
        else
        {
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
}
