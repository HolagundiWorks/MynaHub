using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Adms.Desktop;

/// <summary>
/// The editing ribbon used by the template page: Home, Insert and Layout, arranged as in Word.
/// </summary>
internal sealed class WordRibbon : UserControl
{
    private readonly Border _home;
    private readonly Border _insert;
    private readonly Border _layout;
    private readonly Button _homeTab;
    private readonly Button _insertTab;
    private readonly Button _layoutTab;
    private readonly List<Control> _character = new();
    private readonly List<Control> _insertCommands = new();
    private readonly List<Control> _styles = new();

    public ComboBox Font { get; }
    public ComboBox Size { get; }
    public ComboBox Paper { get; }

    public WordRibbon(
        Action undo, Action redo, Action bold, Action italic, Action underline,
        Action alignLeft, Action alignCenter, Action alignRight, Action justify,
        Action bullets, Action numbering,
        Action normal, Action title, Action heading, Action subtitle,
        Action table, Action line, Action header, Action footer, Action remove)
    {
        Font = Choice(180, "Font");
        Size = Choice(68, "Size");
        Paper = Choice(150, "Paper");
        Add(Font, "Plex Sans", "sans", "Segoe UI");
        Add(Font, "Plex Sans Medium", "sans-medium", "Segoe UI Semilight");
        Add(Font, "Plex Sans SemiBold", "sans-semibold", "Segoe UI Semibold");
        Add(Font, "Plex Serif", "serif", "Georgia");
        Add(Font, "Plex Serif SemiBold", "serif-semibold", "Palatino Linotype");
        Add(Font, "Plex Mono", "mono", "Consolas");
        foreach (var size in new[] { 8, 9, 10, 11, 12, 14, 16, 18, 20, 22, 24, 28, 36 })
            Add(Size, size.ToString(), size, null);
        Add(Paper, "A4", "A4", null);
        Add(Paper, "A4 landscape", "A4 landscape", null);
        Add(Paper, "A3", "A3", null);
        Add(Paper, "A5", "A5", null);
        Font.SelectedIndex = 0;
        Size.SelectedIndex = 4;

        var history = Row(
            Icon("\uE7A7", "Undo", undo, _styles),
            Icon("\uE7A6", "Redo", redo, _styles));
        var font = Row(Font, Size,
            Icon("\uE8DD", "Bold", bold, _character),
            Icon("\uE8DB", "Italic", italic, _character),
            Icon("\uE8DC", "Underline", underline, _character));
        _character.Add(Font);
        _character.Add(Size);
        var paragraph = Row(
            Icon("\uE8E4", "Align left", alignLeft, _character),
            Icon("\uE8E3", "Centre", alignCenter, _character),
            Icon("\uE8E2", "Align right", alignRight, _character),
            TextCommand("≡", "Justify", justify, _character),
            Icon("\uE8FD", "Bullets", bullets, _character),
            TextCommand("1.", "Numbering", numbering, _character));
        var styles = Row(
            TextCommand("Normal", "Normal", normal, _styles),
            TextCommand("Title", "Title", title, _styles),
            TextCommand("Heading", "Heading", heading, _styles),
            TextCommand("Subtitle", "Subtitle", subtitle, _styles));

        _home = Band(
            Group("Clipboard", history),
            Group("Font", font),
            Group("Paragraph", paragraph),
            Group("Styles", styles));
        _insert = Band(
            Group("Tables", Row(TextCommand("Table", "Table", table, _insertCommands))),
            Group("Illustrations", Row(TextCommand("Line", "Horizontal line", line, _insertCommands))),
            Group("Header & Footer", Row(
                TextCommand("Header", "Header", header, _insertCommands),
                TextCommand("Footer", "Footer", footer, _insertCommands),
                TextCommand("Remove", "Remove", remove, _insertCommands))));
        _layout = Band(Group("Page setup", Row(Paper)));
        _insert.Visibility = Visibility.Collapsed;
        _layout.Visibility = Visibility.Collapsed;

        _homeTab = Tab("Home", () => Show(_home, _homeTab!));
        _insertTab = Tab("Insert", () => Show(_insert, _insertTab!));
        _layoutTab = Tab("Layout", () => Show(_layout, _layoutTab!));
        Mark(_homeTab, true);

        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
        tabs.Children.Add(_homeTab);
        tabs.Children.Add(_insertTab);
        tabs.Children.Add(_layoutTab);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(tabs);
        var commands = new Grid();
        commands.Children.Add(_home);
        commands.Children.Add(_insert);
        commands.Children.Add(_layout);
        Grid.SetRow(commands, 1);
        root.Children.Add(commands);

        Content = new Border
        {
            Background = Flow.Ink("SurfaceBackgroundBrush"),
            BorderBrush = Flow.Ink("BorderSubtleBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = root,
        };
    }

    public void SetEnabled(bool edit, bool design)
    {
        foreach (var control in _styles)
            control.IsEnabled = edit;
        foreach (var control in _character)
            control.IsEnabled = edit && design;
        foreach (var control in _insertCommands)
            control.IsEnabled = edit && design;
        Paper.IsEnabled = edit && design;
    }

    private void Show(Border band, Button tab)
    {
        _home.Visibility = Visibility.Collapsed;
        _insert.Visibility = Visibility.Collapsed;
        _layout.Visibility = Visibility.Collapsed;
        band.Visibility = Visibility.Visible;
        Mark(_homeTab, false);
        Mark(_insertTab, false);
        Mark(_layoutTab, false);
        Mark(tab, true);
    }

    private static void Mark(Button tab, bool selected)
    {
        tab.BorderThickness = new Thickness(0, 0, 0, selected ? 2 : 0);
        tab.BorderBrush = Flow.Ink("TextPrimaryBrush");
        tab.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private static Button Tab(string label, Action show)
    {
        var button = new Button
        {
            Content = label,
            Padding = new Thickness(12, 8, 12, 6),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            FontSize = 13,
        };
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => show();
        return button;
    }

    private static Border Band(params UIElement[] groups)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 2, 4, 4) };
        for (var i = 0; i < groups.Length; i++)
        {
            if (i > 0)
                row.Children.Add(new Border { Width = 1, Height = 58, Margin = new Thickness(2, 6, 2, 6), Background = Flow.Ink("BorderSubtleBrush") });
            row.Children.Add(groups[i]);
        }
        return new Border
        {
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollMode = ScrollMode.Auto,
                VerticalScrollMode = ScrollMode.Disabled,
                Content = row,
            },
        };
    }

    private static StackPanel Group(string caption, UIElement commands)
    {
        var stack = new StackPanel { Margin = new Thickness(6, 2, 6, 0) };
        stack.Children.Add(commands);
        stack.Children.Add(new TextBlock
        {
            Text = caption,
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 2),
            Foreground = Flow.Ink("TextSecondaryBrush"),
        });
        return stack;
    }

    private static StackPanel Row(params UIElement[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        foreach (var item in items)
            row.Children.Add(item);
        return row;
    }

    private static Button Icon(string glyph, string name, Action action, List<Control> bucket)
    {
        var button = Command(new FontIcon { Glyph = glyph, FontSize = 16, FontFamily = new FontFamily("Segoe MDL2 Assets") }, name, action, bucket);
        button.Width = 32;
        button.Height = 32;
        return button;
    }

    private static Button TextCommand(string label, string name, Action action, List<Control> bucket)
    {
        var button = Command(label, name, action, bucket);
        button.Height = 32;
        button.Padding = new Thickness(8, 0, 8, 0);
        return button;
    }

    private static Button Command(object content, string name, Action action, List<Control> bucket)
    {
        var button = new Button
        {
            Content = content,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        button.Click += (_, _) => action();
        bucket.Add(button);
        return button;
    }

    private static ComboBox Choice(double width, string name)
    {
        var box = new ComboBox { Width = width, MinHeight = 32, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(box, name);
        return box;
    }

    private static void Add(ComboBox box, string label, object tag, string? font)
    {
        var item = new ComboBoxItem { Content = label, Tag = tag };
        if (font != null)
            item.FontFamily = new FontFamily(font);
        box.Items.Add(item);
    }
}
