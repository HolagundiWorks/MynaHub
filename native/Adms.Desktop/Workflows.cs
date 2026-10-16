using System.Globalization;
using System.Text.Json;
using Adms.Interop;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Adms.Desktop;

internal static class Flow
{
    public static Style Look(string key) => (Style)Application.Current.Resources[key];

    public static Brush Ink(string key)
    {
        var theme = AppTheme.ActiveDictionary;
        foreach (var dictionary in Walk(Application.Current.Resources))
        {
            if (dictionary.ThemeDictionaries.TryGetValue(theme, out var match)
                && match is ResourceDictionary themed
                && themed.TryGetValue(key, out var value)
                && value is Brush brush)
                return brush;
        }
        return (Brush)Application.Current.Resources[key];
    }

    private static IEnumerable<ResourceDictionary> Walk(ResourceDictionary root)
    {
        yield return root;
        foreach (var merged in root.MergedDictionaries)
        {
            foreach (var child in Walk(merged))
                yield return child;
        }
    }

    public static StackPanel SheetMark(string sheet, string section, string page)
    {
        var mark = new TextBlock
        {
            Text = $"AOS-{sheet}  ·  {section}",
            Style = Look("CaptionStyle"),
        };
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 2) };
        panel.Children.Add(mark);
        AutomationProperties.SetName(panel, $"AOS {sheet}, {section}, {page}");
        return panel;
    }

    public static TextBlock Heading(string text)
    {
        var block = new TextBlock { Text = text, Style = Look("PageTitleStyle") };
        AutomationProperties.SetHeadingLevel(block, AutomationHeadingLevel.Level1);
        return block;
    }

    public static TextBlock Section(string text)
    {
        var block = new TextBlock { Text = text, Style = Look("SectionTitleStyle") };
        AutomationProperties.SetHeadingLevel(block, AutomationHeadingLevel.Level2);
        return block;
    }

    public static TextBlock Secondary(string text) => new()
    {
        Text = text,
        Style = Look("SecondaryStyle"),
        TextWrapping = TextWrapping.WrapWholeWords,
    };

    public static TextBox Input(string header, bool multiline = false) => new()
    {
        Header = header,
        AcceptsReturn = multiline,
        TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
        MinHeight = multiline ? 120 : 0,
    };

    public static Button Command(string label, Action action, bool primary = false)
    {
        var button = new Button { Content = label };
        if (primary)
            button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        button.Click += (_, _) => action();
        return button;
    }

    public static void Pin(Panel host, params (string Label, Action Run)[] actions)
    {
        if (actions.Length == 0)
            return;
        var button = new Button
        {
            Content = actions.Length == 1 ? actions[0].Label : "Create",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 12, 12),
            MinHeight = 36,
            Padding = new Thickness(16, 4, 16, 4),
            CornerRadius = new CornerRadius(8),
        };
        AutomationProperties.SetName(button, actions.Length == 1 ? actions[0].Label : "Create");
        if (actions.Length == 1)
            button.Click += (_, _) => actions[0].Run();
        else
        {
            var menu = new MenuFlyout();
            foreach (var (label, run) in actions)
            {
                var item = new MenuFlyoutItem { Text = label };
                item.Click += (_, _) => run();
                menu.Items.Add(item);
            }
            button.Flyout = menu;
        }
        host.Children.Add(button);
    }

    public static ScrollViewer Scroll(UIElement content) => new()
    {
        Content = content,
        HorizontalScrollMode = ScrollMode.Disabled,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };

    public static FlexBar Bar(params UIElement[] items)
    {
        var bar = new FlexBar();
        foreach (var item in items)
            bar.Children.Add(item);
        return bar;
    }

    public static Grid Pair(UIElement left, UIElement right)
    {
        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(left);
        grid.Children.Add(right);
        if (right is FrameworkElement start)
            Grid.SetColumn(start, 1);
        void PlacePair()
        {
            var narrow = grid.ActualWidth > 0 && grid.ActualWidth < 560;
            grid.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            if (right is FrameworkElement field)
            {
                Grid.SetColumn(field, narrow ? 0 : 1);
                Grid.SetRow(field, narrow ? 1 : 0);
            }
        }
        grid.SizeChanged += (_, _) => PlacePair();
        return grid;
    }

    public static Grid Line(UIElement body, Button action)
    {
        var line = new Grid { ColumnSpacing = 8 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.Children.Add(body);
        action.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(action, 1);
        line.Children.Add(action);
        return line;
    }

    public static Button Edit(Action open)
    {
        var button = new Button { Content = "Edit", MinWidth = 72 };
        AutomationProperties.SetName(button, "Edit");
        button.Click += (_, _) => open();
        return button;
    }

    public static async Task<bool> Discard(UIElement owner, bool dirty)
    {
        if (!dirty)
            return true;
        if (owner.XamlRoot == null)
            return false;
        var previous = FocusManager.GetFocusedElement(owner.XamlRoot);
        var dialog = new ContentDialog
        {
            Title = "Discard unsaved changes?",
            Content = "These changes have not been saved.",
            PrimaryButtonText = "Discard",
            CloseButtonText = "Keep editing",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = owner.XamlRoot,
        };
        var result = await dialog.ShowAsync() == ContentDialogResult.Primary;
        if (previous is UIElement element)
            element.Focus(FocusState.Programmatic);
        return result;
    }

    public static async Task<bool> Confirm(UIElement owner, string title, string message, string confirm)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.WrapWholeWords, MaxWidth = 420 },
            PrimaryButtonText = confirm,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = owner.XamlRoot,
        };
        var previous = FocusManager.GetFocusedElement(owner.XamlRoot);
        var result = await dialog.ShowAsync() == ContentDialogResult.Primary;
        if (previous is UIElement element)
            element.Focus(FocusState.Programmatic);
        return result;
    }

    public static Grid Header(params string[] cells) => Cells(true, cells, null, null, false);

    public static Grid Columns(params string[] cells) => Cells(false, cells, null, null, false);

    public static Grid Header(string[] cells, GridLength[] widths, bool[]? alignEnd = null) => Cells(true, cells, widths, alignEnd, false);

    public static Grid Columns(string[] cells, GridLength[] widths, bool[]? alignEnd = null, bool emphasize = false) => Cells(false, cells, widths, alignEnd, emphasize);

    public static DataSheet Table(string name, string[] headers, IReadOnlyList<string[]> rows, bool[] alignEnd, int emphasizeFrom = -1)
    {
        var panel = new StackPanel();
        var cells = new List<FrameworkElement[]>();
        var widths = Widths(headers.Length, alignEnd);
        var header = Cells(true, headers, widths, alignEnd, false);
        NameCells(header, headers, null, 0);
        cells.Add(CellElements(header));
        panel.Children.Add(header);
        for (var r = 0; r < rows.Count; r++)
        {
            var line = Cells(false, rows[r], widths, alignEnd, emphasizeFrom >= 0 && r >= emphasizeFrom);
            NameCells(line, headers, rows[r], r + 1);
            cells.Add(CellElements(line));
            panel.Children.Add(line);
        }
        return new DataSheet(name, headers, cells, panel);
    }

    private static void NameCells(Grid line, string[] headers, string[]? values, int row)
    {
        var cells = CellElements(line);
        for (var c = 0; c < cells.Length && c < headers.Length; c++)
        {
            var value = values != null && c < values.Length ? values[c] : "";
            var spoken = row == 0 ? $"{headers[c]} column" : value.Length == 0 ? headers[c] : $"{headers[c]} {value}";
            AutomationProperties.SetName(cells[c], spoken);
        }
    }

    private static FrameworkElement[] CellElements(Grid line) =>
        line.Children.OfType<FrameworkElement>().OrderBy(Grid.GetColumn).ToArray();

    public static void Place(Grid grid, IReadOnlyList<UIElement> items, double minItemWidth, bool fill)
    {
        grid.Children.Clear();
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();
        if (items.Count == 0)
            return;
        var host = grid.ActualWidth > 1 ? grid.ActualWidth : grid.Parent is FrameworkElement parent && parent.ActualWidth > 1 ? parent.ActualWidth : 720;
        var cols = Math.Max(1, (int)(host / minItemWidth));
        cols = Math.Min(cols, items.Count);
        for (var c = 0; c < cols; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = fill ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        var rowCount = (items.Count + cols - 1) / cols;
        for (var r = 0; r < rowCount; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is not FrameworkElement element)
                continue;
            Grid.SetRow(element, i / cols);
            Grid.SetColumn(element, i % cols);
            if (!fill)
                element.Margin = new Thickness(0, 0, 8, 8);
            grid.Children.Add(element);
        }
    }

    private static GridLength[] Widths(int count, bool[] alignEnd)
    {
        var widths = new GridLength[count];
        for (var i = 0; i < count; i++)
            widths[i] = alignEnd[i] ? new GridLength(160) : i == 0 ? new GridLength(2, GridUnitType.Star) : new GridLength(1, GridUnitType.Star);
        return widths;
    }

    private static Grid Cells(bool header, string[] cells, GridLength[]? widths, bool[]? alignEnd, bool emphasize)
    {
        var grid = new Grid();
        var brush = Ink(header || emphasize ? "BorderStrongBrush" : "BorderSubtleBrush");
        for (var i = 0; i < cells.Length; i++)
        {
            var width = widths != null && i < widths.Length
                ? widths[i]
                : i == 1 ? new GridLength(2, GridUnitType.Star) : new GridLength(1, GridUnitType.Star);
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            var align = alignEnd != null && i < alignEnd.Length && alignEnd[i];
            var text = new TextBlock
            {
                Text = cells[i],
                Style = Look(header || emphasize ? "TableHeaderStyle" : "TableCellStyle"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = align ? TextAlignment.Right : TextAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (align)
                text.FontFamily = new FontFamily("Cascadia Mono, Consolas");
            var cell = new Border
            {
                BorderBrush = brush,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(8, 8, 8, 8),
                Child = text,
            };
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }
        return grid;
    }

    public static void UseTable(ListView list)
    {
        var style = new Style(typeof(ListViewItem));
        if (Application.Current.Resources.TryGetValue(typeof(ListViewItem), out var existing) && existing is Style basis)
            style.BasedOn = basis;
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0d));
        list.ItemContainerStyle = style;
        list.Padding = new Thickness(0);
        Surface(list);
    }

    public static void Surface(FrameworkElement element)
    {
        void Paint()
        {
            var brush = Ink("SurfaceBackgroundBrush");
            switch (element)
            {
                case Control control:
                    control.Background = brush;
                    break;
                case Panel panel:
                    panel.Background = brush;
                    break;
            }
        }
        element.ActualThemeChanged += (_, _) => Paint();
        element.Loaded += (_, _) => Paint();
        Paint();
    }

    public static string Money(long paise) =>
        (paise / 100m).ToString("C", CultureInfo.GetCultureInfo("en-IN"));

    public static JsonElement? Try(AppShell shell, string operation, object? args = null)
    {
        try { return shell.Call(operation, args); }
        catch (AdmsException) { return null; }
    }

    public static object PageQuery(Dictionary<string, string>? filters = null, int pageSize = 10) => new
    {
        search = "",
        sort = "",
        dir = "",
        page = 0,
        page_size = pageSize,
        filters = filters ?? new Dictionary<string, string>(),
    };

    public static bool TakeCreate(AppShell shell, string page)
    {
        if (shell.PendingCreate != page)
            return false;
        shell.PendingCreate = null;
        return true;
    }
}

internal sealed class FlexBar : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var limit = double.IsInfinity(availableSize.Width) ? 1600 : Math.Max(0, availableSize.Width);
        double x = 0, y = 0, row = 0, width = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(limit, double.PositiveInfinity));
            var w = child.DesiredSize.Width;
            var h = child.DesiredSize.Height;
            if (x > 0 && x + w > limit)
            {
                y += row + 8;
                x = 0;
                row = 0;
            }
            if (x > 0)
                x += 8;
            x += w;
            row = Math.Max(row, h);
            width = Math.Max(width, x);
        }
        return new Size(Math.Min(limit, width), y + row);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, row = 0;
        foreach (var child in Children)
        {
            var w = child.DesiredSize.Width;
            var h = child.DesiredSize.Height;
            if (x > 0 && x + w > finalSize.Width + 0.5)
            {
                y += row + 8;
                x = 0;
                row = 0;
            }
            child.Arrange(new Rect(x, y, w, h));
            x += w + 8;
            row = Math.Max(row, h);
        }
        return finalSize;
    }
}

internal sealed class DashboardPage : UserControl, IPageCommands
{
    private readonly AppShell _shell;
    private readonly Grid _metrics = new() { RowSpacing = 8, ColumnSpacing = 8 };
    private readonly StackPanel _attention = new() { Spacing = 8, Visibility = Visibility.Collapsed };
    private readonly TextBlock _attentionHeading;
    private readonly StackPanel _projectList = new() { Spacing = 4 };
    private readonly TextBlock _context = Flow.Secondary("Loading the company overview…");
    private readonly List<UIElement> _metricItems = [];
    private readonly List<(string Label, Action Run)> _creates = [];

    public DashboardPage(AppShell shell)
    {
        _shell = shell;
        if (shell.CanEdit)
        {
            _creates.Add(("New client", () => shell.Navigate?.Invoke(Mark("clients"))));
            _creates.Add(("New project", () => shell.Navigate?.Invoke(Mark("projects"))));
            _creates.Add(("New letter", () => shell.Navigate?.Invoke(Mark("documents"))));
        }
        if (shell.CanIssue)
            _creates.Add(("New invoice", () => shell.Navigate?.Invoke(Mark("invoices"))));
        if (shell.CanPost)
            _creates.Add(("Record expense", () => shell.Navigate?.Invoke(Mark("acc-expenses"))));

        var page = new StackPanel
        {
            Spacing = 12,
            Margin = (Thickness)Application.Current.Resources["PageMargin"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        page.Children.Add(Flow.SheetMark("00", "Practice", "Pulse"));
        page.Children.Add(Flow.Heading("Pulse"));
        page.Children.Add(_context);
        page.Children.Add(Flow.Command("Refresh", Load));
        _attentionHeading = Flow.Section("Needs attention");
        _attentionHeading.Visibility = Visibility.Collapsed;
        page.Children.Add(_attentionHeading);
        page.Children.Add(_attention);
        page.Children.Add(Flow.Section("Overview"));
        page.Children.Add(_metrics);
        page.Children.Add(Flow.Section("Projects"));
        page.Children.Add(_projectList);
        var scroller = Flow.Scroll(page);
        var layer = new Grid();
        layer.Children.Add(scroller);
        Flow.Pin(layer, [.. _creates]);
        Content = layer;
        SizeChanged += (_, _) => Flow.Place(_metrics, _metricItems, 220, true);
        Load();
    }

    private string Mark(string page)
    {
        _shell.PendingCreate = page;
        return page;
    }

    private void Load()
    {
        _context.Text = "Loading the company overview…";
        _ = _shell.Run("Loading overview…", () =>
        {
            var profile = Flow.Try(_shell, "company_get_profile");
            var company = profile is JsonElement p ? Kit.Str(p, "name") : "";
            var clients = Flow.Try(_shell, "client_list", new { include_archived = false });
            var projects = Flow.Try(_shell, "project_list", new { client_id = (long?)null, include_archived = false });
            var drafts = Flow.Try(_shell, "doc_page", new { query = Flow.PageQuery(new Dictionary<string, string> { ["status"] = "draft" }) });
            JsonElement? invoices = _shell.CanFinance ? Flow.Try(_shell, "invoice_page", new { query = Flow.PageQuery() }) : null;
            JsonElement? draftInvoices = _shell.CanFinance ? Flow.Try(_shell, "invoice_page", new { query = Flow.PageQuery(new Dictionary<string, string> { ["status"] = "draft" }) }) : null;
            JsonElement? bank = _shell.CanFinance ? Flow.Try(_shell, "bank_summary", new { account_id = (long?)null }) : null;
            var year = FinancialYear();
            _shell.Window.DispatcherQueue.TryEnqueue(() => Paint(company, year, clients, projects, drafts, draftInvoices, invoices, bank));
        });
    }

    private string? FinancialYear()
    {
        var periods = Flow.Try(_shell, "period_list");
        if (periods is not JsonElement list || list.ValueKind != JsonValueKind.Array)
            return null;
        var today = Kit.Today();
        string? open = null;
        foreach (var period in list.EnumerateArray())
        {
            if (Kit.Str(period, "status") != "open")
                continue;
            open = Kit.Str(period, "fy");
            var start = Kit.Str(period, "start_date");
            var end = Kit.Str(period, "end_date");
            if (string.CompareOrdinal(today, start) >= 0 && string.CompareOrdinal(today, end) <= 0)
                return open;
        }
        return open;
    }

    private void Paint(string company, string? year, JsonElement? clients, JsonElement? projects, JsonElement? drafts, JsonElement? draftInvoices, JsonElement? invoices, JsonElement? bank)
    {
        _context.Text = string.IsNullOrWhiteSpace(company)
            ? "Figures come from the open company file."
            : year == null ? $"{company}. Figures come from the open company file." : $"{company}. Financial year {year}. Figures come from the open company file.";
        _metricItems.Clear();
        _attention.Children.Clear();
        if (clients is JsonElement clientRows && clientRows.ValueKind == JsonValueKind.Array)
            Metric("Clients", clientRows.GetArrayLength().ToString(), "Not archived", "clients");
        if (projects is JsonElement projectRows && projectRows.ValueKind == JsonValueKind.Array)
        {
            var active = 0;
            var shown = 0;
            foreach (var row in projectRows.EnumerateArray())
            {
                if (row.TryGetProperty("archived", out var archived) && archived.ValueKind == JsonValueKind.True)
                    continue;
                shown++;
                if (Kit.Str(row, "status") == "active")
                    active++;
            }
            Metric("Active projects", active.ToString(), $"{shown} not archived", "projects");
        }
        if (drafts is JsonElement draftPage)
        {
            var total = Kit.Str(draftPage, "total");
            Metric("Draft documents", total, "Letters and agreements not yet issued", "documents");
            if (int.TryParse(total, out var count) && count > 0)
                Attention($"{count} draft document{(count == 1 ? "" : "s")} not yet issued", "documents");
        }
        if (draftInvoices is JsonElement invoiceDrafts)
        {
            var total = Kit.Str(invoiceDrafts, "total");
            Metric("Draft invoices", total, "Not issued", "invoices");
            if (int.TryParse(total, out var count) && count > 0)
                Attention($"{count} draft invoice{(count == 1 ? "" : "s")} not issued", "invoices");
        }
        if (invoices is JsonElement invoicePage && invoicePage.TryGetProperty("sums", out var sums) && sums.TryGetProperty("outstanding", out var outstanding) && outstanding.TryGetInt64(out var paise))
        {
            Metric("Outstanding", Flow.Money(paise), "Balance on issued tax invoices", "invoices");
            if (paise > 0)
                Attention($"Outstanding fees {Flow.Money(paise)}", "invoices");
        }
        if (bank is JsonElement summary)
        {
            var unmatched = Kit.Str(summary, "unmatched");
            Metric("Unreconciled", unmatched, $"{Kit.Str(summary, "partial")} partly matched. {Kit.Str(summary, "rejected_rows")} rejected rows.", "bank");
            if (int.TryParse(unmatched, out var count) && count > 0)
                Attention($"{count} bank transaction{(count == 1 ? "" : "s")} not matched", "bank");
        }
        if (_metricItems.Count == 0)
            _metricItems.Add(Flow.Secondary("The overview could not be loaded."));
        Flow.Place(_metrics, _metricItems, 220, true);
        PaintProjects(projects);
        var showAttention = _attention.Children.Count > 0;
        _attention.Visibility = showAttention ? Visibility.Visible : Visibility.Collapsed;
        _attentionHeading.Visibility = showAttention ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PaintProjects(JsonElement? projects)
    {
        _projectList.Children.Clear();
        if (projects is not JsonElement rows || rows.ValueKind != JsonValueKind.Array)
        {
            _projectList.Children.Add(Flow.Secondary("Projects could not be loaded."));
            return;
        }
        var shown = 0;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.TryGetProperty("archived", out var archived) && archived.ValueKind == JsonValueKind.True)
                continue;
            if (Kit.Str(row, "status") != "active")
                continue;
            if (shown == 8)
                break;
            shown++;
            var name = Kit.Str(row, "name");
            var client = Kit.Str(row, "client_name");
            var service = Consultancy.Label(Kit.Str(row, "consultancy_type"));
            var button = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = $"{name}  ·  {client}  ·  {service}",
            };
            AutomationProperties.SetName(button, $"{name}, {client}, {service}");
            button.Click += (_, _) => _shell.Navigate?.Invoke("projects");
            _projectList.Children.Add(button);
        }
        if (shown == 0)
            _projectList.Children.Add(Flow.Secondary("No active projects yet. Create one when a client engages the practice."));
    }

    private void Metric(string label, string value, string hint, string page)
    {
        var caption = new TextBlock
        {
            Text = label,
            Style = Flow.Look("CaptionStyle"),
        };
        var figure = new TextBlock
        {
            Text = value,
            Style = Flow.Look("MetricValueStyle"),
            Margin = new Thickness(0, 2, 0, 2),
        };
        var note = new TextBlock
        {
            Text = hint,
            Style = Flow.Look("CaptionStyle"),
            TextWrapping = TextWrapping.WrapWholeWords,
            MaxHeight = 32,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var copy = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        copy.Children.Add(caption);
        copy.Children.Add(figure);
        copy.Children.Add(note);
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var mark = new Border { Background = Flow.Ink("BrandAccentBrush") };
        Grid.SetColumn(copy, 1);
        copy.Margin = new Thickness(12, 10, 12, 10);
        body.Children.Add(mark);
        body.Children.Add(copy);
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Padding = new Thickness(0),
            MinHeight = 88,
            Background = Flow.Ink("SurfaceBackgroundBrush"),
            BorderBrush = Flow.Ink("BorderSubtleBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Content = body,
        };
        AutomationProperties.SetName(button, $"{label}, {value}. {hint}");
        button.Click += (_, _) => _shell.Navigate?.Invoke(page);
        _metricItems.Add(button);
    }

    private void Attention(string text, string page)
    {
        var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, MinHeight = 32 };
        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => _shell.Navigate?.Invoke(page);
        _attention.Children.Add(button);
    }

    public void NewItem() { }
    public void Save() { }
    public void Print() { }
    public void FocusSearch() { }
}

internal sealed class ProjectsPage : UserControl, IPageCommands
{
    private readonly AppShell _shell;
    private readonly List<JsonElement> _rows = [];
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly ContentControl _header = new();
    private readonly TextBlock _summary = Flow.Secondary("Loading projects…");
    private readonly TextBlock _empty = Flow.Secondary("No projects match.");
    private readonly TextBox _search = new() { PlaceholderText = "Code, name, client or site", Header = "Search" };
    private readonly ComboBox _status = new() { Header = "Status" };
    private static readonly GridLength[] ProjectWidths = [new(120), new(1.6, GridUnitType.Star), new(1.8, GridUnitType.Star), new(1.2, GridUnitType.Star), new(110)];
    private readonly StackPanel _editor = new() { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _client = new() { Header = "Client" };
    private readonly TextBox _code = Flow.Input("Project code");
    private readonly TextBox _name = Flow.Input("Project name");
    private readonly ComboBox _state = new() { Header = "Status" };
    private readonly ComboBox _consultancy = new() { Header = "Consultancy type" };
    private readonly TextBox _site = Flow.Input("Site address", true);
    private readonly TextBlock _title = Flow.Heading("Projects");
    private readonly Button _save = new();
    private Button _archive = new() { Content = "Archive", Visibility = Visibility.Collapsed };
    private readonly InfoBar _error = new() { IsOpen = false, IsClosable = false, Severity = InfoBarSeverity.Error };
    private long? _id;
    private bool _dirty;
    private bool _filling;
    private bool _saving;

    public ProjectsPage(AppShell shell)
    {
        _shell = shell;
        Flow.UseTable(_list);
        Flow.Surface(_header);
        AutomationProperties.SetName(_list, "Projects");
        AutomationProperties.SetName(_search, "Search projects");
        AutomationProperties.SetHelpText(_search, "Filters the project list by code, name, client or site.");
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);
        _empty.Visibility = Visibility.Collapsed;
        _status.Items.Add(Item("All statuses", ""));
        _status.Items.Add(Item("Active", "active"));
        _status.Items.Add(Item("On hold", "on_hold"));
        _status.Items.Add(Item("Completed", "completed"));
        _status.SelectedIndex = 0;
        _state.Items.Add(Item("Active", "active"));
        _state.Items.Add(Item("On hold", "on_hold"));
        _state.Items.Add(Item("Completed", "completed"));
        foreach (var (key, label) in Consultancy.Types)
            _consultancy.Items.Add(Item(label, key));
        _consultancy.SelectedIndex = 0;
        _search.TextChanged += (_, _) => PaintList();
        _status.SelectionChanged += (_, _) => PaintList();
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is ListViewItem item && item.Tag is JsonElement row)
                Open(row);
        };
        foreach (var box in new[] { _code, _name, _site })
            box.TextChanged += (_, _) => { if (!_filling) _dirty = true; };
        _client.SelectionChanged += (_, _) => { if (!_filling) _dirty = true; };
        _state.SelectionChanged += (_, _) => { if (!_filling) _dirty = true; };
        _consultancy.SelectionChanged += (_, _) => { if (!_filling) _dirty = true; };

        var create = Flow.Command("New project", () => _ = BeginNew(), primary: true);
        AutomationProperties.SetName(create, "New project");
        if (!_shell.CanEdit)
            create.Visibility = Visibility.Collapsed;
        var titleRow = new Grid { ColumnSpacing = 12 };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel { Spacing = 4 };
        titles.Children.Add(Flow.SheetMark("01", "Projects", "Projects"));
        titles.Children.Add(Flow.Heading("Projects"));
        titles.Children.Add(Flow.Secondary("A coordinated project record."));
        titleRow.Children.Add(titles);
        Grid.SetColumn(create, 1);
        create.VerticalAlignment = VerticalAlignment.Top;
        titleRow.Children.Add(create);
        var listPane = new Grid { RowSpacing = 12 };
        listPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        listPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        listPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        listPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        listPane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var filters = new Grid();
        _search.HorizontalAlignment = HorizontalAlignment.Stretch;
        _status.HorizontalAlignment = HorizontalAlignment.Stretch;
        listPane.Children.Add(titleRow);
        Grid.SetRow(filters, 1);
        listPane.Children.Add(filters);
        SizeChanged += (_, _) => Flow.Place(filters, [_search, _status], 280, true);
        Grid.SetRow(_summary, 2);
        listPane.Children.Add(_summary);
        var table = new Grid();
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        table.Children.Add(_header);
        Grid.SetRow(_list, 1);
        table.Children.Add(_list);
        Grid.SetRow(_empty, 1);
        _empty.VerticalAlignment = VerticalAlignment.Top;
        _empty.Margin = new Thickness(8, 12, 8, 0);
        table.Children.Add(_empty);
        Grid.SetRow(table, 3);
        Grid.SetRowSpan(table, 2);
        listPane.Children.Add(table);

        _save.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        _save.Click += (_, _) => SaveProject();
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += async (_, _) => { if (await CanLeave()) ShowList(); };
        var archive = new Button { Content = "Archive" };
        AutomationProperties.SetName(archive, "Archive project");
        archive.Click += async (_, _) => await ArchiveProject();
        archive.Visibility = Visibility.Collapsed;
        _archive = archive;
        var actions = Flow.Bar(cancel, archive, _save);
        _editor.Children.Add(_title);
        _editor.Children.Add(Flow.Secondary("A client can have many projects. Each project keeps its own agreements, letters and invoices."));
        _editor.Children.Add(Flow.Section("Project details"));
        _editor.Children.Add(Flow.Pair(_name, _code));
        _editor.Children.Add(Flow.Pair(_client, _state));
        _editor.Children.Add(Flow.Section("Consultancy"));
        _editor.Children.Add(Flow.Secondary("The service follows the Council of Architecture Conditions of Engagement."));
        _editor.Children.Add(_consultancy);
        _editor.Children.Add(Flow.Section("Site"));
        _editor.Children.Add(_site);
        _editor.Children.Add(_error);
        _editor.Children.Add(actions);

        var host = new Grid { Margin = (Thickness)Application.Current.Resources["PageMargin"] };
        host.Children.Add(listPane);
        var editorScroll = Flow.Scroll(_editor);
        editorScroll.Visibility = Visibility.Collapsed;
        host.Children.Add(editorScroll);
        if (_shell.CanEdit)
            Flow.Pin(host, ("New project", () => _ = BeginNew()));
        Content = host;
        _ = _shell.Run("Loading projects…", () =>
        {
            var projects = _shell.Call("project_list", new { client_id = (long?)null, include_archived = false });
            var clients = _shell.Call("client_list", new { include_archived = false });
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                _rows.Clear();
                if (projects.ValueKind == JsonValueKind.Array)
                    foreach (var row in projects.EnumerateArray())
                        _rows.Add(row.Clone());
                _client.Items.Clear();
                _client.Items.Add(Item("Select a client", 0L));
                if (clients.ValueKind == JsonValueKind.Array)
                    foreach (var row in clients.EnumerateArray())
                        _client.Items.Add(Item(Kit.Str(row, "name"), Kit.Id(row) ?? 0));
                PaintList();
                if (Flow.TakeCreate(_shell, "projects"))
                    _ = BeginNew();
            });
        });
    }

    private Grid HostGrid => (Grid)Content;
    private UIElement ListPane => HostGrid.Children[0];
    private ScrollViewer EditorPane => (ScrollViewer)HostGrid.Children[1];

    private void ShowList()
    {
        _dirty = false;
        _id = null;
        EditorPane.Visibility = Visibility.Collapsed;
        ListPane.Visibility = Visibility.Visible;
        _list.SelectedItem = null;
    }

    private async Task BeginNew()
    {
        if (!await CanLeave())
            return;
        if (_client.Items.Count <= 1)
        {
            _shell.Problem("Add a client before creating a project.");
            return;
        }
        _filling = true;
        _id = null;
        _code.Text = "";
        _name.Text = "";
        _site.Text = "";
        _client.SelectedIndex = 0;
        _client.IsEnabled = true;
        _state.SelectedIndex = 0;
        _consultancy.SelectedIndex = 0;
        _title.Text = "New project";
        _save.Content = "Create";
        _archive.Visibility = Visibility.Collapsed;
        _error.IsOpen = false;
        _filling = false;
        _dirty = false;
        ListPane.Visibility = Visibility.Collapsed;
        EditorPane.Visibility = Visibility.Visible;
        _name.Focus(FocusState.Programmatic);
    }

    private void Open(JsonElement row)
    {
        _filling = true;
        _id = Kit.Id(row);
        _code.Text = Kit.Str(row, "code");
        _name.Text = Kit.Str(row, "name");
        _site.Text = Kit.Str(row, "site_address");
        Select(_client, Kit.Long(Kit.Str(row, "client_id")));
        _client.IsEnabled = false;
        Select(_state, Kit.Str(row, "status"));
        Select(_consultancy, string.IsNullOrEmpty(Kit.Str(row, "consultancy_type")) ? "comprehensive" : Kit.Str(row, "consultancy_type"));
        _title.Text = _name.Text.Length == 0 ? "Project" : _name.Text;
        _save.Content = "Save changes";
        _archive.Visibility = _shell.CanEdit ? Visibility.Visible : Visibility.Collapsed;
        _error.IsOpen = false;
        _filling = false;
        _dirty = false;
        ListPane.Visibility = Visibility.Collapsed;
        EditorPane.Visibility = Visibility.Visible;
    }

    private void PaintList()
    {
        var needle = _search.Text.Trim().ToLowerInvariant();
        var status = ((ComboBoxItem?)_status.SelectedItem)?.Tag as string ?? "";
        _list.Items.Clear();
        _header.Content = Flow.Header(["Code", "Project", "Consultancy", "Client", "Status"], ProjectWidths);
        var count = 0;
        foreach (var row in _rows)
        {
            if (status.Length > 0 && Kit.Str(row, "status") != status)
                continue;
            var service = Consultancy.Label(Kit.Str(row, "consultancy_type"));
            var blob = $"{Kit.Str(row, "code")} {Kit.Str(row, "name")} {Kit.Str(row, "client_name")} {Kit.Str(row, "site_address")} {service}".ToLowerInvariant();
            if (needle.Length > 0 && !blob.Contains(needle))
                continue;
            var cells = new[] { Kit.Str(row, "code"), Kit.Str(row, "name"), service, Kit.Str(row, "client_name"), StatusLabel(Kit.Str(row, "status")) };
            var item = new ListViewItem { Content = Flow.Line(Flow.Columns(cells, ProjectWidths), Flow.Edit(() => Open(row))), Tag = row };
            AutomationProperties.SetName(item, $"{cells[1]}, code {cells[0]}, client {cells[2]}, {cells[3]}");
            _list.Items.Add(item);
            count++;
        }
        var noun = count == 1 ? "project" : "projects";
        if (count == 0 && needle.Length == 0 && status.Length == 0)
        {
            _summary.Text = "This company has no projects yet.";
            _empty.Text = "Create a project to start an engagement.";
        }
        else if (count == 0)
        {
            _summary.Text = "No projects match this search.";
            _empty.Text = "Change the search or status filter.";
        }
        else
        {
            _summary.Text = $"{count} {noun}.";
            _empty.Text = "";
        }
        _empty.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SaveProject()
    {
        if (_saving)
            return;
        var clientId = ((ComboBoxItem?)_client.SelectedItem)?.Tag is long id ? id : 0;
        if (clientId == 0) { Fail("Select a client."); return; }
        if (_name.Text.Trim().Length == 0) { Fail("Project name is required."); return; }
        if (_code.Text.Trim().Length == 0) { Fail("Project code is required."); return; }
        var project = new
        {
            id = _id,
            client_id = clientId,
            code = _code.Text.Trim(),
            name = _name.Text.Trim(),
            site_address = _site.Text.Trim(),
            consultancy_type = ((ComboBoxItem?)_consultancy.SelectedItem)?.Tag as string ?? "comprehensive",
            status = ((ComboBoxItem?)_state.SelectedItem)?.Tag as string ?? "active",
            archived = false,
            client_name = "",
        };
        _saving = true;
        _save.IsEnabled = false;
        _ = _shell.Run(_id == null ? "Creating project…" : "Saving project…", () => _shell.Call("project_save", new { project }))
            .ContinueWith(task => _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                _saving = false;
                _save.IsEnabled = true;
                if (task.Status == TaskStatus.RanToCompletion && task.Result)
                {
                    _dirty = false;
                    _shell.Navigate?.Invoke("projects");
                    _shell.Window.DispatcherQueue.TryEnqueue(ReloadAfterSave);
                }
            }));
    }

    private void ReloadAfterSave()
    {
        ShowList();
        _ = _shell.Run("Loading projects…", () =>
        {
            var projects = _shell.Call("project_list", new { client_id = (long?)null, include_archived = false });
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                _rows.Clear();
                if (projects.ValueKind == JsonValueKind.Array)
                    foreach (var row in projects.EnumerateArray())
                        _rows.Add(row.Clone());
                PaintList();
            });
        });
    }

    private async Task ArchiveProject()
    {
        if (_id is not long id || !_shell.CanEdit)
            return;
        if (!await Flow.Confirm(this, "Archive this project?", "The project leaves the active list. Its letters and invoices stay in the company file.", "Archive"))
            return;
        _ = _shell.Run("Archiving project…", () => _shell.Call("project_set_archived", new { id, archived = true }))
            .ContinueWith(task => _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                if (!task.Result)
                    return;
                _dirty = false;
                ReloadAfterSave();
            }));
    }

    private void Fail(string message)
    {
        _error.Message = message;
        _error.IsOpen = true;
    }

    public async void NewItem() => await BeginNew();
    public void Save() => SaveProject();
    public void Print() { }
    public void FocusSearch() => _search.Focus(FocusState.Programmatic);
    public async Task<bool> CanLeave() => await Flow.Discard(this, _dirty);

    private static ComboBoxItem Item(string label, object tag) => new() { Content = label, Tag = tag };
    private static void Select(ComboBox box, object tag)
    {
        foreach (ComboBoxItem item in box.Items)
        {
            if (Equals(item.Tag, tag) || item.Tag?.ToString() == tag.ToString())
            {
                box.SelectedItem = item;
                return;
            }
        }
    }
    private static string StatusLabel(string status) => status switch { "on_hold" => "On hold", "completed" => "Completed", "active" => "Active", _ => status };
}

internal sealed class ClientsPage : UserControl, IPageCommands
{
    private readonly AppShell _shell;
    private readonly List<JsonElement> _rows = [];
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly ContentControl _header = new();
    private readonly TextBlock _summary = Flow.Secondary("Loading clients…");
    private readonly TextBlock _empty = Flow.Secondary("");
    private readonly TextBox _search = new() { PlaceholderText = "Name, contact, phone or email", Header = "Search", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _name = Flow.Input("Name");
    private readonly TextBox _contact = Flow.Input("Contact person");
    private readonly TextBox _email = Flow.Input("Email");
    private readonly TextBox _phone = Flow.Input("Phone");
    private readonly TextBox _address = Flow.Input("Address", true);
    private readonly TextBox _pan = Flow.Input("PAN");
    private readonly TextBox _gstin = Flow.Input("GSTIN");
    private readonly TextBlock _title = Flow.Heading("New client");
    private readonly Button _save = new();
    private readonly Button _archive = new() { Content = "Archive", Visibility = Visibility.Collapsed };
    private readonly InfoBar _error = new() { IsOpen = false, IsClosable = false, Severity = InfoBarSeverity.Error };
    private static readonly GridLength[] ClientWidths = [new(2, GridUnitType.Star), new(1.4, GridUnitType.Star), new(1, GridUnitType.Star)];
    private long? _id;
    private bool _archived;
    private bool _dirty;
    private bool _filling;
    private bool _saving;

    public ClientsPage(AppShell shell)
    {
        _shell = shell;
        Flow.UseTable(_list);
        Flow.Surface(_header);
        AutomationProperties.SetName(_list, "Clients");
        AutomationProperties.SetName(_search, "Search clients");
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);
        _empty.Visibility = Visibility.Collapsed;
        _search.TextChanged += (_, _) => Paint();
        _list.SelectionChanged += (_, _) => { if (_list.SelectedItem is ListViewItem item && item.Tag is JsonElement row) Open(row); };
        foreach (var box in new[] { _name, _contact, _email, _phone, _address, _pan, _gstin })
            box.TextChanged += (_, _) => { if (!_filling) _dirty = true; };
        var create = Flow.Command("New client", () => _ = BeginNew(), primary: true);
        AutomationProperties.SetName(create, "New client");
        if (!_shell.CanEdit)
            create.Visibility = Visibility.Collapsed;
        var titleRow = new Grid { ColumnSpacing = 12 };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel { Spacing = 4 };
        titles.Children.Add(Flow.SheetMark("02.01", "Third Parties", "Clients"));
        titles.Children.Add(Flow.Heading("Clients"));
        titles.Children.Add(Flow.Secondary("People and firms this practice works with."));
        titleRow.Children.Add(titles);
        Grid.SetColumn(create, 1);
        create.VerticalAlignment = VerticalAlignment.Top;
        titleRow.Children.Add(create);
        var list = new Grid { RowSpacing = 12 };
        list.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        list.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        list.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        list.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        list.Children.Add(titleRow);
        Grid.SetRow(_search, 1);
        list.Children.Add(_search);
        Grid.SetRow(_summary, 2);
        list.Children.Add(_summary);
        var table = new Grid();
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        table.Children.Add(_header);
        Grid.SetRow(_list, 1);
        table.Children.Add(_list);
        Grid.SetRow(_empty, 1);
        _empty.Margin = new Thickness(8, 12, 8, 0);
        _empty.VerticalAlignment = VerticalAlignment.Top;
        table.Children.Add(_empty);
        Grid.SetRow(table, 3);
        list.Children.Add(table);
        _save.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        _save.Click += (_, _) => SaveClient();
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += async (_, _) => { if (await CanLeave()) ShowList(); };
        _archive.Click += async (_, _) => await ArchiveClient();
        var actions = Flow.Bar(cancel, _archive, _save);
        var form = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        form.Children.Add(_title);
        form.Children.Add(Flow.Section("Client"));
        form.Children.Add(Flow.Pair(_name, _contact));
        form.Children.Add(Flow.Pair(_phone, _email));
        form.Children.Add(Flow.Section("Billing identity"));
        form.Children.Add(_address);
        form.Children.Add(Flow.Pair(_pan, _gstin));
        form.Children.Add(_error);
        form.Children.Add(actions);
        var host = new Grid { Margin = (Thickness)Application.Current.Resources["PageMargin"] };
        host.Children.Add(list);
        var editorScroll = Flow.Scroll(form);
        editorScroll.Visibility = Visibility.Collapsed;
        host.Children.Add(editorScroll);
        if (_shell.CanEdit)
            Flow.Pin(host, ("New client", () => _ = BeginNew()));
        Content = host;
        Reload();
        if (Flow.TakeCreate(_shell, "clients"))
            _ = BeginNew();
    }

    private Grid Host => (Grid)Content;
    private void ShowList() { _dirty = false; Host.Children[1].Visibility = Visibility.Collapsed; Host.Children[0].Visibility = Visibility.Visible; }

    private async Task BeginNew()
    {
        if (!await CanLeave()) return;
        _filling = true;
        _id = null;
        _archived = false;
        foreach (var box in new[] { _name, _contact, _email, _phone, _address, _pan, _gstin }) box.Text = "";
        _title.Text = "New client";
        _save.Content = "Create";
        _archive.Visibility = Visibility.Collapsed;
        _error.IsOpen = false;
        _filling = false;
        _dirty = false;
        Host.Children[0].Visibility = Visibility.Collapsed;
        Host.Children[1].Visibility = Visibility.Visible;
        _name.Focus(FocusState.Programmatic);
    }

    private void Open(JsonElement row)
    {
        _filling = true;
        _id = Kit.Id(row);
        _archived = row.TryGetProperty("archived", out var flag) && flag.ValueKind == JsonValueKind.True;
        _name.Text = Kit.Str(row, "name");
        _contact.Text = Kit.Str(row, "contact_person");
        _email.Text = Kit.Str(row, "email");
        _phone.Text = Kit.Str(row, "phone");
        _address.Text = Kit.Str(row, "address");
        _pan.Text = Kit.Str(row, "pan");
        _gstin.Text = Kit.Str(row, "gstin");
        _title.Text = _name.Text;
        _save.Content = "Save changes";
        _archive.Visibility = _shell.CanEdit ? Visibility.Visible : Visibility.Collapsed;
        _error.IsOpen = false;
        _filling = false;
        _dirty = false;
        Host.Children[0].Visibility = Visibility.Collapsed;
        Host.Children[1].Visibility = Visibility.Visible;
    }

    private void Paint()
    {
        var needle = _search.Text.Trim().ToLowerInvariant();
        _list.Items.Clear();
        _header.Content = Flow.Header(["Client", "Contact", "Phone"], ClientWidths);
        var count = 0;
        foreach (var row in _rows)
        {
            var blob = $"{Kit.Str(row, "name")} {Kit.Str(row, "contact_person")} {Kit.Str(row, "phone")} {Kit.Str(row, "email")}".ToLowerInvariant();
            if (needle.Length > 0 && !blob.Contains(needle)) continue;
            var cells = new[] { Kit.Str(row, "name"), Kit.Str(row, "contact_person"), Kit.Str(row, "phone") };
            var item = new ListViewItem { Content = Flow.Line(Flow.Columns(cells, ClientWidths), Flow.Edit(() => Open(row))), Tag = row };
            AutomationProperties.SetName(item, $"{cells[0]}, contact {cells[1]}, phone {cells[2]}");
            _list.Items.Add(item);
            count++;
        }
        if (count == 0 && needle.Length == 0)
        {
            _summary.Text = "This company has no clients yet.";
            _empty.Text = "Create a client before starting a project.";
        }
        else if (count == 0)
        {
            _summary.Text = "No clients match this search.";
            _empty.Text = "Change the search.";
        }
        else
        {
            _summary.Text = count == 1 ? "1 client." : $"{count} clients.";
            _empty.Text = "";
        }
        _empty.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Reload() => _ = _shell.Run("Loading clients…", () =>
    {
        var rows = _shell.Call("client_list", new { include_archived = false });
        _shell.Window.DispatcherQueue.TryEnqueue(() =>
        {
            _rows.Clear();
            if (rows.ValueKind == JsonValueKind.Array)
                foreach (var row in rows.EnumerateArray()) _rows.Add(row.Clone());
            Paint();
        });
    });

    private void SaveClient()
    {
        if (_saving) return;
        if (_name.Text.Trim().Length == 0) { _error.Message = "Name is required."; _error.IsOpen = true; return; }
        var client = new { id = _id, name = _name.Text.Trim(), contact_person = _contact.Text.Trim(), email = _email.Text.Trim(), phone = _phone.Text.Trim(), address = _address.Text.Trim(), pan = _pan.Text.Trim(), gstin = _gstin.Text.Trim(), archived = _archived };
        _saving = true;
        _save.IsEnabled = false;
        _ = _shell.Run(_id == null ? "Creating client…" : "Saving client…", () => _shell.Call("client_save", new { client }))
            .ContinueWith(task => _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                _saving = false;
                _save.IsEnabled = true;
                if (task.Result)
                {
                    _dirty = false;
                    ShowList();
                    Reload();
                }
            }));
    }

    private async Task ArchiveClient()
    {
        if (_id is not long id || !_shell.CanEdit)
            return;
        if (!await Flow.Confirm(this, "Archive this client?", "The client leaves the active list. Projects already recorded stay.", "Archive"))
            return;
        _ = _shell.Run("Archiving client…", () => _shell.Call("client_set_archived", new { id, archived = true }))
            .ContinueWith(task => _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                if (!task.Result)
                    return;
                _dirty = false;
                ShowList();
                Reload();
            }));
    }

    public async void NewItem() => await BeginNew();
    public void Save() => SaveClient();
    public void Print() { }
    public void FocusSearch() => _search.Focus(FocusState.Programmatic);
    public async Task<bool> CanLeave() => await Flow.Discard(this, _dirty);
}

internal sealed class LettersPage : UserControl, IPageCommands
{
    private readonly AppShell _shell;
    private readonly List<JsonElement> _rows = [];
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly ContentControl _header = new();
    private readonly TextBlock _summary = Flow.Secondary("Loading letters…");
    private readonly TextBlock _empty = Flow.Secondary("");
    private readonly TextBox _search = new() { PlaceholderText = "Number, subject, party or reference", Header = "Search", HorizontalAlignment = HorizontalAlignment.Stretch };
    private static readonly GridLength[] LetterWidths = [new(140), new(2, GridUnitType.Star), new(1.4, GridUnitType.Star), new(120)];
    private readonly ComboBox _kind = new() { Header = "Document type" };
    private readonly TextBox _date = Flow.Input("Date");
    private readonly TextBox _reference = Flow.Input("Reference");
    private readonly ComboBox _project = new() { Header = "Project" };
    private readonly TextBox _party = Flow.Input("Party");
    private readonly TextBox _address = Flow.Input("Address", true);
    private readonly TextBox _subject = Flow.Input("Subject");
    private readonly TextBox _body = Flow.Input("Body", true);
    private readonly TextBlock _title = Flow.Heading("New letter");
    private readonly TextBlock _state = Flow.Secondary("Draft. Save draft keeps it editable. Issue gives it a number and freezes the content.");
    private readonly Button _save = new() { Content = "Save draft" };
    private readonly Button _preview = new() { Content = "Preview" };
    private readonly Button _issue = new() { Content = "Issue" };
    private readonly Button _revise = new() { Content = "Revise", Visibility = Visibility.Collapsed };
    private readonly Button _attach = new() { Content = "Attach signed copy", Visibility = Visibility.Collapsed };
    private readonly Button _sign = new() { Content = "Mark signed", Visibility = Visibility.Collapsed };
    private readonly TextBox _cancelReason = Flow.Input("Reason for cancelling");
    private readonly Button _cancelDoc = new() { Content = "Cancel document", Visibility = Visibility.Collapsed };
    private readonly InfoBar _error = new() { IsOpen = false, IsClosable = false, Severity = InfoBarSeverity.Error };
    private long? _id;
    private string _status = "draft";
    private bool _dirty;
    private bool _filling;
    private bool _saving;

    public LettersPage(AppShell shell)
    {
        _shell = shell;
        Flow.UseTable(_list);
        Flow.Surface(_header);
        AutomationProperties.SetName(_list, "Letters");
        AutomationProperties.SetName(_search, "Search letters");
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);
        _empty.Visibility = Visibility.Collapsed;
        _kind.Items.Add(Item("Outward letter", "letter_out"));
        _kind.Items.Add(Item("Agreement", "agreement"));
        _body.MinHeight = 220;
        _search.TextChanged += (_, _) => Paint();
        _list.SelectionChanged += (_, _) => { if (_list.SelectedItem is ListViewItem item && item.Tag is JsonElement row) Open(row); };
        foreach (var box in new[] { _date, _reference, _party, _address, _subject, _body })
            box.TextChanged += (_, _) => { if (!_filling) _dirty = true; };
        var letter = Flow.Command("New letter", () => _ = BeginNew("letter_out"), primary: true);
        var agreement = Flow.Command("New agreement", () => _ = BeginNew("agreement"));
        AutomationProperties.SetName(letter, "New letter");
        AutomationProperties.SetName(agreement, "New agreement");
        if (!_shell.CanEdit)
            letter.Visibility = agreement.Visibility = Visibility.Collapsed;
        var commands = Flow.Bar(letter, agreement);
        var titleRow = new Grid { ColumnSpacing = 12 };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel { Spacing = 4 };
        titles.Children.Add(Flow.SheetMark("03.01", "Office", "Letters"));
        titles.Children.Add(Flow.Heading("Letters"));
        titles.Children.Add(Flow.Secondary("Drafts stay editable. Issued documents are frozen."));
        titleRow.Children.Add(titles);
        Grid.SetColumn(commands, 1);
        commands.VerticalAlignment = VerticalAlignment.Top;
        titleRow.Children.Add(commands);
        var list = new Grid { RowSpacing = 12 };
        list.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        list.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        list.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        list.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        list.Children.Add(titleRow);
        Grid.SetRow(_search, 1);
        list.Children.Add(_search);
        Grid.SetRow(_summary, 2);
        list.Children.Add(_summary);
        var table = new Grid();
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        table.Children.Add(_header);
        Grid.SetRow(_list, 1);
        table.Children.Add(_list);
        Grid.SetRow(_empty, 1);
        _empty.Margin = new Thickness(8, 12, 8, 0);
        _empty.VerticalAlignment = VerticalAlignment.Top;
        table.Children.Add(_empty);
        Grid.SetRow(table, 3);
        list.Children.Add(table);

        _save.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        _save.Click += (_, _) => SaveDraft();
        _preview.Click += (_, _) => Preview();
        _issue.Click += async (_, _) => await Issue();
        _revise.Click += async (_, _) => await Revise();
        _attach.Click += async (_, _) => await AttachSigned();
        _sign.Click += async (_, _) => await MarkSigned();
        _cancelDoc.Click += async (_, _) => await CancelDocument();
        _cancelReason.Visibility = Visibility.Collapsed;
        var cancel = new Button { Content = "Back" };
        cancel.Click += async (_, _) => { if (await CanLeave()) ShowList(); };
        var actions = Flow.Bar(cancel, _save, _preview, _issue, _revise, _attach, _sign, _cancelDoc);
        var form = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        form.Children.Add(_title);
        form.Children.Add(_state);
        form.Children.Add(Flow.Section("Document details"));
        form.Children.Add(Flow.Pair(_kind, _date));
        form.Children.Add(Flow.Pair(_reference, _project));
        form.Children.Add(Flow.Section("Recipient"));
        form.Children.Add(_party);
        form.Children.Add(_address);
        form.Children.Add(Flow.Section("Content"));
        form.Children.Add(_subject);
        form.Children.Add(_body);
        form.Children.Add(_cancelReason);
        form.Children.Add(_error);
        form.Children.Add(actions);
        var host = new Grid { Margin = (Thickness)Application.Current.Resources["PageMargin"] };
        host.Children.Add(list);
        var editorScroll = Flow.Scroll(form);
        editorScroll.Visibility = Visibility.Collapsed;
        host.Children.Add(editorScroll);
        if (_shell.CanEdit)
            Flow.Pin(host, ("New letter", () => _ = BeginNew("letter_out")), ("New agreement", () => _ = BeginNew("agreement")));
        Content = host;
        Reload();
        if (Flow.TakeCreate(_shell, "documents"))
            _ = BeginNew("letter_out");
    }

    private Grid Host => (Grid)Content;
    private void ShowList() { _dirty = false; Host.Children[1].Visibility = Visibility.Collapsed; Host.Children[0].Visibility = Visibility.Visible; }

    private async Task BeginNew(string kind)
    {
        if (!await CanLeave()) return;
        _filling = true;
        _id = null;
        _status = "draft";
        _date.Text = Kit.Today();
        _reference.Text = _party.Text = _address.Text = _subject.Text = _body.Text = "";
        Select(_kind, kind);
        LoadProjects(null);
        _title.Text = kind == "agreement" ? "New agreement" : "New letter";
        _state.Text = "Draft. Save draft keeps it editable. Issue gives it a number and freezes the content.";
        SetEditable(true);
        ShowIssuedActions(false, false);
        _error.IsOpen = false;
        _filling = false;
        _dirty = false;
        Host.Children[0].Visibility = Visibility.Collapsed;
        Host.Children[1].Visibility = Visibility.Visible;
        _subject.Focus(FocusState.Programmatic);
    }

    private void Open(JsonElement row)
    {
        var id = Kit.Id(row);
        if (id is not long docId) return;
        _ = _shell.Run("Opening document…", () =>
        {
            var detail = _shell.Call("doc_get", new { id = docId });
            _shell.Window.DispatcherQueue.TryEnqueue(() => Apply(detail));
        });
    }

    private void Apply(JsonElement detail)
    {
        _filling = true;
        var input = detail.TryGetProperty("input", out var body) ? body : detail;
        _id = Kit.Id(input);
        _status = Kit.Str(detail, "status");
        if (_status.Length == 0) _status = "draft";
        Select(_kind, Kit.Str(input, "kind"));
        _date.Text = Kit.Str(input, "doc_date");
        _reference.Text = Kit.Str(input, "reference");
        _party.Text = Kit.Str(input, "party_name");
        _address.Text = Kit.Str(input, "party_address");
        _subject.Text = Kit.Str(input, "subject");
        _body.Text = Kit.Str(input, "body");
        var project = input.TryGetProperty("project_id", out var projectId) && projectId.ValueKind == JsonValueKind.Number ? projectId.GetInt64() : (long?)null;
        LoadProjects(project);
        var number = Kit.Str(detail, "number");
        var draft = _status == "draft";
        _title.Text = number.Length > 0 ? number : _subject.Text;
        _state.Text = draft ? "Draft. Issue gives this document a number. After that, the content cannot be edited." : $"{_status}. This document is frozen. A change is a revision, not an edit.";
        SetEditable(draft && _shell.CanEdit);
        _issue.Visibility = draft && _shell.CanIssue ? Visibility.Visible : Visibility.Collapsed;
        ShowIssuedActions(_status is "issued" or "signed", Kit.Str(input, "kind") == "agreement" && _status == "issued");
        _error.IsOpen = false;
        _filling = false;
        _dirty = false;
        Host.Children[0].Visibility = Visibility.Collapsed;
        Host.Children[1].Visibility = Visibility.Visible;
    }

    private void SetEditable(bool editable)
    {
        foreach (var box in new Control[] { _kind, _date, _reference, _project, _party, _address, _subject, _body })
            box.IsEnabled = editable;
        _save.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowIssuedActions(bool issued, bool signable)
    {
        _revise.Visibility = issued && _shell.CanEdit ? Visibility.Visible : Visibility.Collapsed;
        _cancelDoc.Visibility = _status == "issued" && _shell.CanIssue ? Visibility.Visible : Visibility.Collapsed;
        _cancelReason.Visibility = _cancelDoc.Visibility;
        _attach.Visibility = signable && _shell.CanEdit ? Visibility.Visible : Visibility.Collapsed;
        _sign.Visibility = signable && _shell.CanIssue ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task Revise()
    {
        if (_id is not long id || !_shell.CanEdit)
            return;
        if (!await Flow.Confirm(this, "Revise this document?", "A new draft is created. The issued document stays as it was.", "Revise"))
            return;
        _ = _shell.Run("Creating a revision…", () =>
        {
            var created = _shell.Call("doc_revise", new { id });
            var next = created.ValueKind == JsonValueKind.Number ? created.GetInt64() : id;
            var detail = _shell.Call("doc_get", new { id = next });
            _shell.Window.DispatcherQueue.TryEnqueue(() => Apply(detail));
        });
    }

    private async Task AttachSigned()
    {
        if (_id is not long id)
            return;
        string? path;
        try { path = await FileDialogs.PickFile(_shell.Window.Handle, ".pdf", ".png", ".jpg", ".jpeg"); }
        catch (Exception ex) { _shell.Problem(ex.Message); return; }
        if (path == null)
            return;
        var filename = Path.GetFileName(path);
        var data = Convert.ToBase64String(await File.ReadAllBytesAsync(path));
        _ = _shell.Run("Attaching the signed copy…", () => _shell.Call("doc_attach", new { doc_id = id, kind = "signed_copy", filename, base64_data = data }));
    }

    private async Task MarkSigned()
    {
        if (_id is not long id || !_shell.CanIssue)
            return;
        if (!await Flow.Confirm(this, "Mark this agreement as signed?", "The signed copy must already be attached. The agreement then stays on record.", "Mark signed"))
            return;
        _ = _shell.Run("Marking signed…", () => _shell.Call("doc_mark_signed", new { id }))
            .ContinueWith(task => _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                if (!task.Result)
                    return;
                _dirty = false;
                ShowList();
                Reload();
            }));
    }

    private async Task CancelDocument()
    {
        if (_id is not long id || !_shell.CanIssue)
            return;
        var reason = _cancelReason.Text.Trim();
        if (reason.Length == 0)
        {
            _shell.Problem("Give a reason.");
            return;
        }
        if (!await Flow.Confirm(this, "Cancel this document?", "The number stays used. A signed agreement stays on record.", "Cancel document"))
            return;
        _ = _shell.Run("Cancelling document…", () => _shell.Call("doc_cancel", new { id, reason }))
            .ContinueWith(task => _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                if (!task.Result)
                    return;
                _dirty = false;
                ShowList();
                Reload();
            }));
    }

    private void LoadProjects(long? selected)
    {
        var rows = Flow.Try(_shell, "project_list", new { client_id = (long?)null, include_archived = false });
        _project.Items.Clear();
        _project.Items.Add(Item("No project", 0L));
        if (rows is JsonElement list && list.ValueKind == JsonValueKind.Array)
            foreach (var row in list.EnumerateArray())
                _project.Items.Add(Item($"{Kit.Str(row, "client_name")} — {Kit.Str(row, "name")}", Kit.Id(row) ?? 0));
        Select(_project, selected ?? 0);
    }

    private void Paint()
    {
        var needle = _search.Text.Trim().ToLowerInvariant();
        _list.Items.Clear();
        _header.Content = Flow.Header(["Date", "Subject", "Recipient", "Status"], LetterWidths);
        var count = 0;
        foreach (var row in _rows)
        {
            var blob = $"{Kit.Str(row, "number")} {Kit.Str(row, "subject")} {Kit.Str(row, "party_name")} {Kit.Str(row, "reference")}".ToLowerInvariant();
            if (needle.Length > 0 && !blob.Contains(needle)) continue;
            var subject = Kit.Str(row, "subject");
            if (subject.Length == 0) subject = Kit.Str(row, "number");
            var cells = new[] { Kit.Str(row, "doc_date"), subject, Kit.Str(row, "party_name"), Kit.Str(row, "status") };
            var item = new ListViewItem { Content = Flow.Line(Flow.Columns(cells, LetterWidths), Flow.Edit(() => Open(row))), Tag = row };
            AutomationProperties.SetName(item, $"{cells[1]}, {cells[0]}, recipient {cells[2]}, {cells[3]}");
            _list.Items.Add(item);
            count++;
        }
        if (count == 0 && needle.Length == 0)
        {
            _summary.Text = "This company has no letters yet.";
            _empty.Text = "Create a letter or an agreement.";
        }
        else if (count == 0)
        {
            _summary.Text = "No letters match this search.";
            _empty.Text = "Change the search.";
        }
        else
        {
            _summary.Text = count == 1 ? "1 letter." : $"{count} letters.";
            _empty.Text = "";
        }
        _empty.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Reload() => _ = _shell.Run("Loading documents…", () =>
    {
        var rows = _shell.Call("doc_list", new { kind = (string?)null, q = (string?)null });
        _shell.Window.DispatcherQueue.TryEnqueue(() =>
        {
            _rows.Clear();
            if (rows.ValueKind == JsonValueKind.Array)
                foreach (var row in rows.EnumerateArray()) _rows.Add(row.Clone());
            Paint();
        });
    });

    private object Input()
    {
        var projectId = ((ComboBoxItem?)_project.SelectedItem)?.Tag is long id && id > 0 ? id : (long?)null;
        return new
        {
            id = _id,
            kind = ((ComboBoxItem?)_kind.SelectedItem)?.Tag as string ?? "letter_out",
            project_id = projectId,
            party_name = _party.Text,
            party_address = _address.Text,
            subject = _subject.Text,
            reference = _reference.Text,
            doc_date = _date.Text,
            body = _body.Text,
            template_version_id = (long?)null,
            fee = "0",
            milestones = Array.Empty<object>(),
        };
    }

    private void SaveDraft()
    {
        if (_saving || _status != "draft") return;
        if (_subject.Text.Trim().Length == 0) { _error.Message = "Subject is required."; _error.IsOpen = true; return; }
        var input = Input();
        _saving = true;
        _ = _shell.Run("Saving draft…", () =>
        {
            var saved = _shell.Call("doc_save_draft", new { input });
            if (saved.ValueKind == JsonValueKind.Number) _id = saved.GetInt64();
        }).ContinueWith(task => _shell.Window.DispatcherQueue.TryEnqueue(() =>
        {
            _saving = false;
            if (task.Result) { _dirty = false; _error.IsOpen = false; _state.Text = "Draft saved."; }
        }));
    }

    private void Preview()
    {
        if (_id is not long id) { _error.Message = "Save the draft before previewing it."; _error.IsOpen = true; return; }
        _ = _shell.Run("Preparing PDF…", () =>
        {
            var encoded = _shell.Call("doc_pdf", new { id }).GetString() ?? "";
            _shell.Window.DispatcherQueue.TryEnqueue(async () => await Kit.PreviewPdf(_shell, encoded, "document"));
        });
    }

    private async Task Issue()
    {
        if (_saving || _status != "draft" || !_shell.CanIssue) return;
        if (!await Flow.Confirm(this, "Issue this document?", "Issue gives the document a number. The content can no longer be edited. This is not the same as saving a draft.", "Issue"))
            return;
        if (_subject.Text.Trim().Length == 0) { _error.Message = "Subject is required."; _error.IsOpen = true; return; }
        var input = Input();
        _saving = true;
        _issue.IsEnabled = false;
        _ = _shell.Run("Issuing document…", () =>
        {
            var saved = _shell.Call("doc_save_draft", new { input });
            var id = saved.ValueKind == JsonValueKind.Number ? saved.GetInt64() : _id ?? throw new AdmsException(1, "Save the draft before issuing it.");
            _shell.Call("doc_issue", new { id });
        }).ContinueWith(task => _shell.Window.DispatcherQueue.TryEnqueue(() =>
        {
            _saving = false;
            _issue.IsEnabled = true;
            if (!task.Result) return;
            _dirty = false;
            _status = "issued";
            ShowList();
            Reload();
        }));
    }

    public async void NewItem() => await BeginNew("letter_out");
    public void Save() => SaveDraft();
    public void Print() => Preview();
    public void FocusSearch() => _search.Focus(FocusState.Programmatic);
    public async Task<bool> CanLeave() => await Flow.Discard(this, _dirty);

    private static ComboBoxItem Item(string label, object tag) => new() { Content = label, Tag = tag };
    private static void Select(ComboBox box, object tag)
    {
        foreach (ComboBoxItem item in box.Items)
            if (Equals(item.Tag, tag) || item.Tag?.ToString() == tag.ToString()) { box.SelectedItem = item; return; }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }
}

internal sealed class BankWorkspace : UserControl, IPageCommands
{
    private readonly AppShell _shell;
    private readonly ListView _accounts = new();
    private readonly ListView _preview = new();
    private readonly ListView _transactions = new();
    private readonly ComboBox _importAccount = new() { Header = "Bank account" };
    private readonly ComboBox _mapDate = new() { Header = "Date", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _mapParticulars = new() { Header = "Particulars", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _mapCredit = new() { Header = "Credit", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _mapDebit = new() { Header = "Debit", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _dateOrder = new() { Header = "Date order", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _fileColumns = new() { Spacing = 4 };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.WrapWholeWords };
    private readonly InfoBar _notice = new() { IsOpen = false, IsClosable = true };
    private readonly TextBox _accountName = Flow.Input("Account name");
    private readonly TextBox _bank = Flow.Input("Bank");
    private readonly TextBox _number = Flow.Input("Account number");
    private readonly TextBox _ifsc = Flow.Input("IFSC");
    private readonly Button _saveAccount = new() { Content = "Create" };
    private string? _csv;
    private string? _csvName;
    private bool _heading = true;
    private bool _mapping;
    private int _previewTicket;
    private bool _previewClean;
    private long? _accountId;
    private bool _dirty;
    private StackPanel? _accountForm;

    public BankWorkspace(AppShell shell)
    {
        _shell = shell;
        Flow.UseTable(_accounts);
        Flow.UseTable(_preview);
        Flow.UseTable(_transactions);
        _dateOrder.Items.Add(new ComboBoxItem { Content = "Day, month, year", Tag = "dmy" });
        _dateOrder.Items.Add(new ComboBoxItem { Content = "Month, day, year", Tag = "mdy" });
        _dateOrder.Items.Add(new ComboBoxItem { Content = "Year, month, day", Tag = "ymd" });
        _dateOrder.SelectedIndex = 0;
        foreach (var box in new ComboBox[] { _mapDate, _mapParticulars, _mapCredit, _mapDebit, _dateOrder })
            box.SelectionChanged += (_, _) => { if (!_mapping && _csv != null) CheckRows(); };
        var pivot = new Pivot { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        pivot.Items.Add(new PivotItem { Header = "Accounts", Content = AccountsPane() });
        pivot.Items.Add(new PivotItem { Header = "Import statement", Content = ImportPane() });
        pivot.Items.Add(new PivotItem { Header = "Reconciliation", Content = ReconciliationPane() });
        var column = new Grid { Margin = new Thickness(8) };
        column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        column.Children.Add(Flow.SheetMark("04.09", "Accounts", "Reconciliation"));
        Grid.SetRow(pivot, 1);
        column.Children.Add(pivot);
        Content = column;
        LoadAccounts();
        if (Flow.TakeCreate(_shell, "bank"))
            pivot.SelectedIndex = 1;
    }

    private UIElement AccountsPane()
    {
        var form = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        form.Children.Add(Flow.Heading("Bank account"));
        form.Children.Add(Flow.Pair(_accountName, _bank));
        form.Children.Add(Flow.Pair(_number, _ifsc));
        _saveAccount.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        _saveAccount.Click += (_, _) => SaveAccount();
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => { _dirty = false; form.Visibility = Visibility.Collapsed; };
        var actions = Flow.Bar(cancel, _saveAccount);
        form.Children.Add(actions);
        form.Visibility = Visibility.Collapsed;
        _accountForm = form;
        foreach (var box in new[] { _accountName, _bank, _number, _ifsc })
            box.TextChanged += (_, _) => _dirty = true;
        var add = new Button { Content = "New account", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        add.Click += (_, _) => BeginAccount();
        var pane = new Grid { RowSpacing = 8, Margin = new Thickness(8) };
        pane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        pane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pane.Children.Add(add);
        Grid.SetRow(_accounts, 1);
        pane.Children.Add(_accounts);
        Grid.SetRow(form, 2);
        pane.Children.Add(form);
        _accounts.SelectionChanged += (_, _) =>
        {
            if (_accounts.SelectedItem is ListViewItem item && item.Tag is JsonElement row)
                EditAccount(row, form);
        };
        return pane;
    }

    private UIElement ImportPane()
    {
        var pick = new Button { Content = "Choose statement" };
        pick.Click += async (_, _) => await ChooseStatement();
        var check = new Button { Content = "Check rows" };
        check.Click += (_, _) => CheckRows();
        var commit = new Button { Content = "Import", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        commit.Click += (_, _) => CommitImport();
        _importAccount.HorizontalAlignment = HorizontalAlignment.Stretch;
        var bar = Flow.Bar(_importAccount, pick, check, commit);
        var columns = new Grid();
        var pane = new StackPanel { Spacing = 8, Margin = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Stretch };
        pane.Children.Add(new TextBlock { Text = "Banks keep Date, Particulars, Credit and Debit in their own order. Choose a CSV or Excel statement, map each column, check the rows, then import. A row with no date is left out, including any other text or amount on that row.", TextWrapping = TextWrapping.WrapWholeWords, Style = Flow.Look("BodyStyle") });
        pane.Children.Add(bar);
        pane.Children.Add(_fileColumns);
        pane.Children.Add(Flow.Section("Map the columns"));
        pane.Children.Add(columns);
        pane.Children.Add(_notice);
        pane.Children.Add(_preview);
        pane.SizeChanged += (_, _) => Flow.Place(columns, [_mapDate, _mapParticulars, _mapCredit, _mapDebit, _dateOrder], 180, true);
        return Flow.Scroll(pane);
    }

    private UIElement ReconciliationPane()
    {
        var refresh = new Button { Content = "Refresh summary" };
        refresh.Click += (_, _) => LoadSummary();
        var pane = new Grid { RowSpacing = 8, Margin = new Thickness(8) };
        pane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        pane.Children.Add(_summary);
        Grid.SetRow(refresh, 1);
        pane.Children.Add(refresh);
        Grid.SetRow(_transactions, 2);
        pane.Children.Add(_transactions);
        LoadSummary();
        return pane;
    }

    private void BeginAccount()
    {
        _accountId = null;
        _accountName.Text = _bank.Text = _number.Text = _ifsc.Text = "";
        _saveAccount.Content = "Create";
        _dirty = false;
        if (_accountForm != null)
            _accountForm.Visibility = Visibility.Visible;
    }

    private void EditAccount(JsonElement row, StackPanel form)
    {
        _accountId = Kit.Id(row);
        _accountName.Text = Kit.Str(row, "name");
        _bank.Text = Kit.Str(row, "bank_name");
        _number.Text = Kit.Str(row, "account_number");
        _ifsc.Text = Kit.Str(row, "ifsc");
        _saveAccount.Content = "Save changes";
        _dirty = false;
        form.Visibility = Visibility.Visible;
    }

    private void SaveAccount()
    {
        var account = new { id = _accountId, name = _accountName.Text.Trim(), bank_name = _bank.Text.Trim(), account_holder = "", account_number = _number.Text.Trim(), ifsc = _ifsc.Text.Trim(), is_primary = false, active = true };
        _ = _shell.Run(_accountId == null ? "Creating account…" : "Saving account…", () => _shell.Call("bank_account_save", new { account }))
            .ContinueWith(task => _shell.Window.DispatcherQueue.TryEnqueue(() => { if (task.Result) { _dirty = false; LoadAccounts(); } }));
    }

    private void LoadAccounts() => _ = _shell.Run("Loading accounts…", () =>
    {
        var rows = _shell.Call("bank_account_list");
        _shell.Window.DispatcherQueue.TryEnqueue(() =>
        {
            _accounts.Items.Clear();
            _importAccount.Items.Clear();
            var widths = new GridLength[] { new(1.4, GridUnitType.Star), new(1.2, GridUnitType.Star), new(1, GridUnitType.Star), new(120) };
            _accounts.Items.Add(Flow.Header(["Account", "Bank", "Number", "IFSC"], widths));
            if (rows.ValueKind != JsonValueKind.Array) return;
            foreach (var row in rows.EnumerateArray())
            {
                var clone = row.Clone();
                var edit = Flow.Edit(() => { if (_accountForm != null) EditAccount(clone, _accountForm); });
                _accounts.Items.Add(new ListViewItem
                {
                    Content = Flow.Line(Flow.Columns([Kit.Str(clone, "name"), Kit.Str(clone, "bank_name"), Mask(Kit.Str(clone, "account_number")), Kit.Str(clone, "ifsc")], widths), edit),
                    Tag = clone,
                });
                _importAccount.Items.Add(new ComboBoxItem { Content = Kit.Str(clone, "name"), Tag = Kit.Id(clone) ?? 0 });
            }
            if (_importAccount.Items.Count > 0 && _importAccount.SelectedIndex < 0)
                _importAccount.SelectedIndex = 0;
        });
    });

    private async Task ChooseStatement()
    {
        string? path;
        try { path = await FileDialogs.PickFile(_shell.Window.Handle, ".csv", ".txt", ".xlsx"); }
        catch (Exception ex) { _shell.Problem(ex.Message); return; }
        if (path == null)
            return;
        StatementFile file;
        try { file = await Task.Run(() => BankStatement.Open(path)); }
        catch (Exception ex) { _shell.Problem(ex.Message); return; }
        _csv = file.Text;
        _csvName = Path.GetFileName(path);
        _heading = file.Heading;
        _previewClean = false;
        ShowColumns(file);
        CheckRows();
    }

    private void ShowColumns(StatementFile file)
    {
        _mapping = true;
        _fileColumns.Children.Clear();
        _fileColumns.Children.Add(Flow.Secondary(file.Heading
            ? "Columns in this file, in the bank's order."
            : "This file has no headings. The sample shows each column."));
        if (file.Headers.Length > 0)
            _fileColumns.Children.Add(Flow.Header(file.Headers));
        foreach (var row in file.Sample)
            _fileColumns.Children.Add(Flow.Columns(Pad(row, file.Headers.Length)));
        foreach (var box in new[] { _mapDate, _mapParticulars, _mapCredit, _mapDebit })
            box.Items.Clear();
        for (var i = 0; i < file.Labels.Length; i++)
        {
            foreach (var box in new[] { _mapDate, _mapParticulars, _mapCredit, _mapDebit })
                box.Items.Add(new ComboBoxItem { Content = file.Labels[i], Tag = i });
        }
        var used = new HashSet<int>();
        Pick(_mapDate, Take(used, file.Labels.Length, BankStatement.Suggest(file.Headers, "date"), 0));
        Pick(_mapParticulars, Take(used, file.Labels.Length, BankStatement.Suggest(file.Headers, "particulars", "narration", "description", "remarks", "details"), 1));
        Pick(_mapCredit, Take(used, file.Labels.Length, BankStatement.Suggest(file.Headers, "credit", "deposit", "cr"), 2));
        Pick(_mapDebit, Take(used, file.Labels.Length, BankStatement.Suggest(file.Headers, "debit", "withdrawal", "withdraw", "dr"), 3));
        _mapping = false;
    }

    private static int Take(HashSet<int> used, int count, int? found, int fallback)
    {
        if (count <= 0)
            return 0;
        if (found is int match && match >= 0 && match < count && used.Add(match))
            return match;
        for (var index = Math.Clamp(fallback, 0, count - 1); index < count; index++)
        {
            if (used.Add(index))
                return index;
        }
        for (var index = 0; index < count; index++)
        {
            if (used.Add(index))
                return index;
        }
        return 0;
    }

    private static void Pick(ComboBox box, int index)
    {
        foreach (ComboBoxItem item in box.Items)
        {
            if (item.Tag is int value && value == index)
            {
                box.SelectedItem = item;
                return;
            }
        }
        if (box.Items.Count > 0)
            box.SelectedIndex = 0;
    }

    private static string[] Pad(string[] row, int width)
    {
        var cells = new string[width];
        for (var i = 0; i < width; i++)
            cells[i] = i < row.Length ? row[i] : "";
        return cells;
    }

    private void CheckRows()
    {
        if (_csv == null)
        {
            _shell.Problem("Choose a statement first.");
            return;
        }
        var ticket = ++_previewTicket;
        var text = _csv;
        var mapping = Mapping();
        _previewClean = false;
        _ = _shell.Run("Reading statement…", () =>
        {
            var parsed = _shell.Call("bank_csv_preview", new { text, mapping });
            _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                if (ticket == _previewTicket)
                    ShowPreview(parsed);
            });
        });
    }

    private void ShowPreview(JsonElement parsed)
    {
        _preview.Items.Clear();
        _previewClean = parsed.ValueKind == JsonValueKind.Array && parsed.GetArrayLength() > 0;
        if (parsed.ValueKind != JsonValueKind.Array)
        {
            _notice.Severity = InfoBarSeverity.Error;
            _notice.Message = "The file could not be read.";
            _notice.IsOpen = true;
            return;
        }
        var widths = new GridLength[] { new(120), new(2, GridUnitType.Star), new(140), new(140) };
        var money = new[] { false, false, true, true };
        _preview.Items.Add(Flow.Header(["Date", "Particulars", "Credit", "Debit"], widths, money));
        var errors = 0;
        foreach (var row in parsed.EnumerateArray())
        {
            var error = Kit.Str(row, "error");
            if (error.Length > 0) errors++;
            var particulars = Kit.Str(row, "narration");
            if (error.Length > 0)
                particulars = particulars.Length == 0 ? error : $"{particulars} — {error}";
            _preview.Items.Add(Flow.Columns([Kit.Str(row, "txn_date"), particulars, Paise(row, "credit_paise"), Paise(row, "debit_paise")], widths, money));
        }
        _previewClean = errors == 0 && parsed.GetArrayLength() > 0;
        _notice.Severity = _previewClean ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        _notice.Message = _previewClean ? $"{parsed.GetArrayLength()} rows are ready to import." : $"{errors} rows need attention. Nothing has been imported.";
        _notice.IsOpen = true;
    }

    private void CommitImport()
    {
        if (!_previewClean || _csv == null || _csvName == null)
        {
            _shell.Problem("Preview the statement and resolve row errors before importing.");
            return;
        }
        if (_importAccount.SelectedItem is not ComboBoxItem item || item.Tag is not long accountId || accountId == 0)
        {
            _shell.Problem("Select the bank account for this statement.");
            return;
        }
        var text = _csv;
        var filename = _csvName;
        var mapping = Mapping();
        _ = _shell.Run("Importing statement…", () => _shell.Call("bank_import_commit", new { account_id = accountId, filename, text, mapping }))
            .ContinueWith(task => _shell.Window.DispatcherQueue.TryEnqueue(() =>
            {
                if (!task.Result) return;
                _csv = null;
                _previewClean = false;
                _notice.Severity = InfoBarSeverity.Success;
                _notice.Message = "The statement was imported.";
                _notice.IsOpen = true;
                _preview.Items.Clear();
                LoadSummary();
            }));
    }

    private void LoadSummary() => _ = _shell.Run("Loading reconciliation…", () =>
    {
        var summary = _shell.Call("bank_summary", new { account_id = (long?)null });
        var txns = _shell.Call("bank_txn_list", new { status = (string?)null, account_id = (long?)null });
        _shell.Window.DispatcherQueue.TryEnqueue(() =>
        {
            _summary.Text = $"{Kit.Str(summary, "unmatched")} unmatched · {Kit.Str(summary, "partial")} partly matched · {Kit.Str(summary, "matched")} matched · {Kit.Str(summary, "rejected_rows")} rejected rows";
            _transactions.Items.Clear();
            var widths = new GridLength[] { new(120), new(2, GridUnitType.Star), new(140), new(120) };
            var money = new[] { false, false, true, false };
            _transactions.Items.Add(Flow.Header(["Date", "Particulars", "Amount", "Status"], widths, money));
            if (txns.ValueKind != JsonValueKind.Array) return;
            foreach (var row in txns.EnumerateArray())
                _transactions.Items.Add(Flow.Columns([Kit.Str(row, "txn_date"), Kit.Str(row, "narration"), Kit.Str(row, "amount_display"), Kit.Str(row, "status")], widths, money));
        });
    });

    private static string Paise(JsonElement row, string key)
    {
        if (!row.TryGetProperty(key, out var value) || !value.TryGetInt64(out var paise) || paise == 0)
            return "";
        return Flow.Money(paise);
    }

    private object Mapping() => new
    {
        delimiter = ",",
        has_header = _heading,
        date_order = _dateOrder.SelectedItem is ComboBoxItem order && order.Tag is string tag ? tag : "dmy",
        date = SelectedColumn(_mapDate, 0),
        narration = SelectedColumn(_mapParticulars, 1),
        reference = (int?)null,
        debit = SelectedColumn(_mapDebit, 3),
        credit = SelectedColumn(_mapCredit, 2),
        amount = (int?)null,
        balance = (int?)null,
    };

    private static int SelectedColumn(ComboBox box, int fallback) =>
        box.SelectedItem is ComboBoxItem item && item.Tag is int index ? index : fallback;

    private static string Mask(string number)
    {
        var digits = number.Replace(" ", "");
        return digits.Length <= 4 ? digits : "ending " + digits[^4..];
    }

    public void NewItem() => BeginAccount();
    public void Save() => SaveAccount();
    public void Print() { }
    public void FocusSearch() { }
    public async Task<bool> CanLeave() => await Flow.Discard(this, _dirty);
}
